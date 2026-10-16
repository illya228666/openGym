# openGym CD on Proxmox

Production is Compose in `/opt/opengym`, project `opengym`, LXC 105
(`saitama`). Only web publishes `8080:80`; the public origin remains
`https://gym.niichavo.pp.ua`. API and Nutrition are internal services.

## Release contract

PR CI runs tests, three-image smoke and the isolated production-Compose deployment
test. Main publication requires a successful PR with the identical Git tree and
Merge PRs using **Create a merge commit** (two parents), not squash/rebase; the release gate verifies the merged PR head and its exact tested tree.
does not repeat tests. CD requires successful main publication, pulls immutable
`sha-<full SHA>` images and checks all three revisions and actual image IDs.
A successful Actions run is not proof of deployment.

The timer checks ten minutes after the preceding run finishes. `flock` serializes
transactions. Candidate Compose is downloaded from `deploy/compose.prod.yml`
at the exact selected SHA and validated before container/config changes.
`release-compose` is only a last-successful cache, never a deployment input.
Changes to the host script/helper require explicit installation: releases do not
execute downloaded host scripts.

## Storage contract

- API uses existing `/opt/opengym/data`, `create_host_path: false` and
  `REQUIRE_EXISTING_DATA=1`. Missing account DB/signing key prevents startup.
- Nutrition uses external **`opengym_nutrition-data`**, independent of project
  name, `/data/nutrition.db` and `RequireExistingDatabase=true`. Missing, corrupt,
  uninitialized or newer-than-application schema prevents startup.
- Existing `coach-auth` is also a non-auto-created bind. Media, ports, OAuth
  settings and secrets stay unchanged.

The storage helper validates effective candidate Compose, actual running mounts,
expected volume/file, SQLite integrity and Nutrition ownership against API UserIds.
It checks all existing user/product/entry IDs after update or rollback.
It never initializes, replaces, merges or restores production data automatically.
Missing/wrong storage stops deployment before containers change.

Fresh installation or migration from an ephemeral container requires **manual
bootstrap**: preserve the whole SQLite directory including WAL/SHM, create and
verify a consistent standalone backup, provision the external volume, copy without
overwriting an existing DB, set the image's non-root ownership, and verify IDs
before enabling managed deployment. Never run `up` first to see whether data
appears. Never prune volumes or use `down -v`.

## Backup before every update

A root-only `backup/` directory accompanies each deployment snapshot:

- `nutrition.db`: SQLite backup API includes committed WAL content;
- `api-data.tar.gz`: complete API data, accounts, state, signing key and grants;
- `env`, `compose`, `images.json`: settings and exact image metadata;
- `inventory.json`: ownership, row IDs and migrations;
- `checksums.json`, `COMPLETE`: SHA-256 manifest and completion marker written last.

Before switching, the helper validates integrity, reads the full compressed archive,
checks accounts/key/ownership and verifies checksums. Backup failure or insufficient
disk aborts deployment. API files are atomically replaced by the application.
Concurrent account changes that make captured stores inconsistent fail backup;
retry with writes quiesced. This is not a distributed point-in-time transaction.

After a successful update, keep the latest 14 completed automatic backup sets plus
`previous` if older. Rotation removes only their backup subdirectories, never
application data, incident backups, config, images or pending transactions.
Monitor disk usage: retention limits count, not bytes. Copy backups off the LXC
using an existing host backup process; local backups cannot survive disk loss.

Offline verification (no private rows or secrets printed):

```sh
python3 /usr/local/sbin/opengym-storage.py verify-backup /path/to/snapshot/backup
```

## Rollback and recovery

CD restores prior Compose, env and exact images after failure **only when applied
migration IDs are unchanged** and a verified backup exists. It never rolls back
SQLite automatically. Even additive migrations conservatively block automatic
downgrade: inspect compatibility first. A schema change retains `pending` and its
backup, writes `hold` and logs a critical failure without starting older code.
Pre-fix snapshots lacking persistent storage/verified backups cannot be rolled back
automatically. Failed release SHAs remain blocked.

```sh
systemctl stop opengym-deploy.timer
/usr/local/sbin/opengym-deploy --rollback
# Only after investigation, with no incompatible pending transaction:
/usr/local/sbin/opengym-deploy --resume
systemctl enable --now opengym-deploy.timer
```

Explicit database recovery:

1. Stop the timer, ensure no deployment holds the deploy lock, preserve all of
   `pending`. Do not use `--resume` to bypass an incompatible transaction.
2. Verify the selected backup. Extract API data into a **new staging directory**,
   not over live data. Inspect the standalone DB's migrations/IDs read-only;
   use saved image/config metadata to select a compatible release.
3. Stop API and Nutrition to quiesce writes; preserve current full stores,
   SQLite WAL/SHM and configuration in a separate incident backup. Obtain explicit
   approval: restoring loses subsequent writes and may revoke newer sessions/grants.
4. Restore API and SQLite as a coherent pair. Move existing DB **and sidecars**
   aside together; never place an old standalone DB beside a newer WAL. Preserve
   modes/ownership (current Nutrition UID/GID 1654) and keep originals recoverable.
5. Start compatible exact images/config. Check mounts, integrity, ownership,
   row IDs, authenticated reads and an approved write before resolving pending
   state and resuming CD. Prefer a forward fix if restore would discard new writes.

There is no automated database merge or downgrade.

## Installing host files

Use the verified checkout, keep the existing env, stop the timer and ensure the
deployment service is inactive. Back up current host files and complete the
one-time persistent-store bootstrap if needed.

```sh
install -m 755 deploy/opengym-deploy /usr/local/sbin/opengym-deploy
install -m 755 deploy/opengym-storage.py /usr/local/sbin/opengym-storage.py
docker compose --project-name opengym --project-directory /opt/opengym \
  --env-file /opt/opengym/.env -f deploy/compose.prod.yml config --quiet
```

Timer/service units do not need changing. Active Compose must satisfy the managed
storage contract before resuming updates. Do not overwrite production Compose from
an unverified checkout; the deployment transaction selects release-SHA Compose.

## Verification scope

`deploy/test_deploy.py` exercises real Bash with fake Docker/HTTP.
`deploy/test_storage.py` uses disposable SQLite/API files for WAL backup,
corruption, staged restore, owner/schema guards and rotation.
`scripts/ci/deploy-storage-smoke.py` exercises the real Bash transaction with real
Compose and CI images, replacing only GitHub discovery/download and registry pulls.
It verifies IDs/UserId, Compose refresh, rollback, failed-deploy recovery and
storage/project-name guards in unique isolated paths/volume/project; no production
data and no volume deletion.

Readiness checks API, frontend HTML/assets/content types, Nutrition health,
spoofed-identity rejection, and opt-in MCP discovery/challenge through nginx.
Production probes do not register accounts or copy sessions. Authenticated MCP
acceptance needs a functioning user-owned connector.
