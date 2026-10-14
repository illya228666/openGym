#!/usr/bin/env bash
# Deny direct pushes, unverified merges or merges with source trees differing from the tested PR.
set -euo pipefail
die() { echo "::error::$*" >&2; exit 1; }
: "$${GITHUB_REPOSITORY:?}"
: "$${GITHUB_SHA:?}"
: "$${GH_TOKEN:?}"
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

# GitHub returns the PR head_sha (not the synthetic PR merge SHA) in the runs API.
runs="$(gh api -X GET "repos/$GITHUB_REPOSITORY/actions/workflows/ci.yml/runs" \
    -f event=pull_request -f head_sha="$pr_head" -f per_page=100)" ||
    die "Cannot query successful PR checks."
mapfile -t ids < <(jq -r --arg head "$pr_head" \
    '.workflow_runs[] | select(.status=="completed" and .conclusion=="success" and
      .head_sha==$head and .path==".github/workflows/ci.yml") | .id' <<< "$runs")
(( ${#ids[@]} > 0 )) || die "No passing PR workflow for the merged head."
verified=0
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
            rm -rf "$dir"
            echo "Verified PR #$pr_number, workflow $run_id, exact tested tree $tree."
            break
        fi
    fi
    rm -rf "$dir"
done
[[ "$verified" -eq 1 ]] ||
    die "No successful PR run attests this exact main Git tree; publishing blocked."
{
    echo "### Release provenance validated"
    echo "PR #$pr_number completed all CI checks for Git tree \`$tree\`."
} >> "$GITHUB_STEP_SUMMARY"
