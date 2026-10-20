# Upstream integration and the next training MCP phase

## Pinned update

This integration merges upstream `e348fc17c42c7e9a211c5972e573ad3698190ee2`
(OpenGym 1.4.1) into fork main `37b9bc8a`, preserving both histories. The common
ancestor is `f049465c`; the fork had 54 unique commits and upstream had 281.
Future updates should merge upstream into a separate branch, not rebase fork history.
Review reused rerere resolutions, including files Git merges without conflicts.

Nutrition, remote OAuth/Streamable HTTP MCP and the three-image release process
remain fork extensions. Training reads use the upstream shared model; this update
does not introduce training writes or upgrade existing OAuth grants.

## Release boundary and storage

The PR must pass `ci.yml`, including three-image smoke, persistence/rollback checks,
catalogue checks and the exact Git-tree attestation. Upstream-only publishing,
mirror, CLA and catalogue jobs do not run for this fork. Merge only after separate
owner approval, using a normal two-parent PR merge (not squash/rebase). If main
changes, update the PR and verify its new tree before merging. Main packaging then
uses the existing attestation gate; do not repeat or bypass it.

Before that approval, inspect live mounts, installed deploy/storage helpers, hold
and timer state, free space and current image IDs. Verify a consistent backup of
API profiles, OAuth stores/keys and Nutrition SQLite including WAL. Preserve `.env`,
credentials, API/coach-auth mounts, external `opengym_nutrition-data` and existing-store
guards. An image rollback does not undo data changes: incompatible state requires
stopping and agreeing on recovery, not restoring over newer user records.

Exercise media now ship in web under `/exercise-media/`, with unprivileged nginx.
There is no startup downloader or legacy img/gif mount. Keep the old server-side
`media/` directory for rollback until acceptance; this PR deletes no server data.
Retain unchanged 180 px media and notices for this non-commercial OpenGym fork;
commercial use requires separately resolving the licence. Never send these media
to MCP clients or AI tools. See [third-party notices](../NOTICE.md).

## Data compatibility checks

The catalogue has 5,632 exercises plus alias IDs. Existing state IDs must not be
rewritten wholesale. Run upstream catalogue continuity/custom-exercise tests and
`scripts/catalogue/migration-check.mjs` against copies of old state, using a checkout
of the previous release as its first argument. Investigate changed names, muscle
maps and corrected statistics rather than enforcing identical old bugs. Never use
production originals as migration output. Browser-only active sessions require a
device backup as well; they are not in the server state.

## Next training MCP phase (not implemented here)

`S.active` is still device-local and excluded from API sync. Completed workouts
remain `S.workouts[].entries[].sets[]`. Workout IDs exist, but entry IDs identify
catalogue exercises rather than unique occurrences; sets have no general stable ID.
The read-only MCP projection is not a lossless editing contract.

A separate design must define a server-persisted MCP session and its relationship
to device-local workouts, stable entry/set IDs compatible with old records, and a
single revision/stamp-aware mutation path using existing session/history functions.
It must handle retry IDs, lost responses and stale edits without duplicating or
silently overwriting records, and preserve every supported set shape. Require a
separate `training.write` consent; do not upgrade existing grants. Clarify ambiguous
exercise/date/unit/edit targets and confirm deletion. Plans, scheduling, templates
and programme generation remain out of scope.
