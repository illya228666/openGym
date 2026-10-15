#!/usr/bin/env bash
# Deny direct pushes, unverified merges or merges with source trees differing from the tested PR.
set -euo pipefail
die() { echo "::error::$*" >&2; exit 1; }
: "${GITHUB_REPOSITORY:?}"
: "${GITHUB_SHA:?}"
: "${GH_TOKEN:?}"
[[ "${GITHUB_EVENT_NAME:-}" == push && "${GITHUB_REF:-}" == refs/heads/main ]] ||
    die "Release check is main-push only."

# Standard merge commits retain the tested PR head as the second parent.
read -r -a parents <<< "$(git rev-list --parents -n 1 "$GITHUB_SHA")"
[[ ${#parents[@]} -eq 3 && "${parents[0]}" == "$GITHUB_SHA" ]] ||
    die "Only normal two-parent PR merge commits may be released."
tree="$(git rev-parse 'HEAD^{tree}')"
pr_list="$(gh api "repos/$GITHUB_REPOSITORY/commits/$GITHUB_SHA/pulls")" ||
    die "GitHub PR provenance unavailable."
pr="$(jq -c --arg sha "$GITHUB_SHA" --arg repo "$GITHUB_REPOSITORY" \
    '[.[] | select(.merged_at != null and .merge_commit_sha == $sha and
      .base.ref == "main" and .head.repo.full_name == $repo)] | first' <<< "$pr_list")"
[[ -n "$pr" && "$pr" != null ]] || die "No corresponding merged PR to main."
pr_number="$(jq -r '.number' <<< "$pr")"
pr_head="$(jq -r '.head.sha' <<< "$pr")"
[[ "$pr_head" == "${parents[2]}" ]] || die "PR head differs from main merge second parent."
[[ "$pr_head" =~ ^[a-f0-9]{40}$ && "$tree" =~ ^[a-f0-9]{40}$ ]] || die "Invalid Git SHA."

# The merge may reach main while the PR's CI is still running. Wait for its
# successful attestation (and artifact availability), never bypass the tree check.
# Timeout is bounded because a stuck or missing PR check must fail closed.
max_wait="${PR_RELEASE_MAX_WAIT_SECONDS:-1200}"
poll="${PR_RELEASE_POLL_SECONDS:-15}"
[[ "$max_wait" =~ ^[0-9]+$ && "$max_wait" -ge 1 && "$max_wait" -le 2700 ]] ||
    die "PR_RELEASE_MAX_WAIT_SECONDS must be 1..2700."
[[ "$poll" =~ ^[0-9]+$ && "$poll" -ge 1 && "$poll" -le 60 ]] ||
    die "PR_RELEASE_POLL_SECONDS must be 1..60."
deadline=$(( $(date +%s) + max_wait ))
verified=0
echo "Waiting up to $max_wait seconds for the successful PR #$pr_number run and its exact Git tree artifact."
while :; do
    if runs="$(gh api -X GET "repos/$GITHUB_REPOSITORY/actions/workflows/ci.yml/runs" \
        -f event=pull_request -f head_sha="$pr_head" -f per_page=100)"; then
        jq -e '.workflow_runs | type == "array"' <<< "$runs" >/dev/null ||
            die "GitHub returned invalid PR workflow data."
        mapfile -t ids < <(jq -r --arg head "$pr_head" \
            '.workflow_runs[] | select(.status=="completed" and .conclusion=="success" and
              .head_sha==$head and .path==".github/workflows/ci.yml") | .id' <<< "$runs")
        for run_id in "${ids[@]}"; do
            dir="$(mktemp -d)"
            if gh run download "$run_id" --repo "$GITHUB_REPOSITORY" \
                --name "verified-pr-$pr_number" --dir "$dir" >/dev/null 2>&1; then
                evidence="$dir/verified-pr-tree.txt"
                if [[ -f "$evidence" ]] &&
                    grep -Fxq "tree=$tree" "$evidence" &&
                    grep -Fxq "head=$pr_head" "$evidence" &&
                    grep -Fxq "pr=$pr_number" "$evidence"; then
                    verified=1
                    echo "Verified PR #$pr_number, workflow $run_id, exact tested tree $tree."
                    rm -rf "$dir"
                    break
                fi
            fi
            rm -rf "$dir"
        done
    else
        echo "::warning::GitHub API temporarily unavailable; retrying release provenance check." >&2
    fi
    [[ "$verified" -eq 1 ]] && break
    now="$(date +%s)"
    (( now < deadline )) ||
        die "Timed out waiting for successful PR #$pr_number CI and matching tree artifact. Publish blocked; rerun publish after PR CI succeeds."
    remaining=$((deadline - now))
    if (( remaining < poll )); then sleep "$remaining"; else sleep "$poll"; fi
done
{
    echo "### Release provenance validated"
    echo "PR #$pr_number completed all CI checks for Git tree \`$tree\`."
} >> "$GITHUB_STEP_SUMMARY"
