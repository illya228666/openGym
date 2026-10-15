# Nutrition food diary

Open **Home → Nutrition** for a personal food diary and product catalog. React uses
the existing OpenGym controls, theme, localization and API client. A signed-in account
is required; guest/local mode has no Nutrition storage. Nutrition is online-only.

## API and authentication

React → existing nginx `/api` proxy → Node gateway → internal ASP.NET Core (.NET 10).
Node keeps its existing session and CSRF checks. ASP.NET independently verifies the
forwarded Cookie/Authorization credentials with Node `/api/me` on **every request**.
Only that response supplies the user id; client identity headers, body ids and query
ids never establish ownership. HttpClient keeps no cookie jar and follows no redirects.
Every product/entry lookup and write is scoped to the authenticated user, including
retry keys. A foreign id returns 404, not someone else's data.

| Endpoint | Operation |
| --- | --- |
| `GET /api/nutrition/products?search=&offset=0` | Unicode-normalized substring search of name/brand; 50 rows and `hasMore` |
| `POST /api/nutrition/products` | Create a product |
| `GET /api/nutrition/external/products?search=&brand=&barcode=&page=1` | Search OFF candidates; paginated, no automatic selection |
| `GET /api/nutrition/external/products/{externalId}` | Read exact OFF nutrition and validation status |
| `POST /api/nutrition/products/import` | Import by source/externalId/fingerprint; 201 created, 200 reused |
| `GET, PUT, DELETE /api/nutrition/products/{id}` | Read, replace nutritional definition, remove from catalog |
| `GET /api/nutrition/diary?date=2026-10-13` | Entries and unrounded daily totals |
| `POST /api/nutrition/entries` | Create an entry with a client-generated UUID |
| `GET, PUT, DELETE /api/nutrition/entries/{id}` | Read, edit, delete an entry |

The existing `/api/nutrition/hello` remains for compatible clients and internal auth
smoke checks; the React screen no longer uses it. See [OpenAPI](../api/openapi.yaml)
and the generated [API reference](../website/api.html) for payloads and responses.

Missing/revoked sessions return 401; validation 400; missing/foreign items 404;
retry conflicts 409; retry of a deleted entry 410. Service/auth/database failure is
503; an invalid upstream response is 502. All responses are `no-store`. Nutrition
failure does not stop the workout API. Internal `/health` is available only after
startup migrations succeed. No new public port is exposed.

## Historical data and calculations

- Products have a UUID, user id, name (1–200 characters), optional brand (≤100),
  kcal (0–1000), protein/fat/carbohydrates (each 0–100 g), all **per 100 g**.
- Inputs accept at most 3 decimal places. Consumption quantity is positive and
  ≤100000 g. Date is a local calendar date (`yyyy-MM-dd`); optional time is `HH:mm`.
  There is no timezone conversion or meal category. Timed entries precede untimed
  ones, with deterministic creation-order ties.
- `NutritionCalculator` multiplies each value by `quantityGrams / 100m` using C#
  decimal. SQLite stores decimals as TEXT, not binary floating point. Totals sum
  unrounded values; React rounds only displayed numbers to at most 2 decimal places.
- An entry stores product id **and** a copy of name, brand and all nutritional
  values. Product edits/deletion never affect history. Editing quantity/date/time
  retains that copy. An explicit `productId` in PUT takes a new snapshot; null or
  omitted retains the historical snapshot, even when the product was deleted.
- POST entry requires `id`, `productId`, `quantityGrams`, `date`, optional `time`.
  Reuse **the same UUID and payload** after an uncertain network failure. First
  creation returns 201; equivalent retries return 200 with the entry's current
  state. A different payload with that UUID returns 409. Unique `(UserId, Id)` keys
  handle concurrent retries. Deletion markers retain the UUID/fingerprint so a late
  retry returns 410 and cannot resurrect food. The React form retains its UUID while
  open, including failed saves; after closing/reloading, check the diary before
  adding again. This is not an offline queue. Manual product creation is not idempotent;
  external import is idempotent by user/source/external ID, including concurrent requests.

Domain models, EF persistence, `NutritionService`, and endpoint contracts are separate
small files. No repository/mediator framework was introduced. Imported products have
nullable source, external ID/barcode, package quantity, source URL, nutrition origin,
retrieval time and source fingerprint. UUIDs are not derived from names. Existing
products and diary snapshots retain their identities and values across the additive
migration. Search text and brands are backfilled with Unicode normalization before
serving; no flavors, numbers or package variants are removed.

## Open Food Facts and MCP import

MCP uses the existing HTTP API and OAuth scopes: `nutrition.read` for lookup,
`nutrition.write` for import. Personal product search also accepts `brand`, `barcode`,
`externalId` and `externalSource` filters. Every personal operation remains user-scoped.
External caches contain only public OFF data, never personal products or credentials.
Barcode zero-padding aliases are canonicalized using OFF rules (8/13-digit padding);
nonzero GTIN-14 package codes remain distinct.

Search uses the legacy full-text endpoint; exact reads explicitly request `/api/v3.6/product`
(the unversioned v3 response defaults to legacy nutrient fields). Reads accept only complete explicit manufacturer or
packaging input sets for the as-sold product per **100 g**. Manufacturer has priority;
conflicts at the same priority or incomplete preferred sets block import. Missing,
estimated, computed-only, approximate, prepared, per-serving and per-100ml data are
not treated as trustworthy grams. Explicit zeroes are valid. Units are converted
deterministically (including kJ/4.184 for kcal), then rounded to the existing three
decimal places. OFF nutrition quality errors/warnings block automatic import.

The server re-fetches the selected card on a new import, compares its fingerprint and
saves the values itself. Changed cards return `409 external_product_changed`; invalid
nutrition returns `422 external_product_unverified`. Existing personal products are
reused before accessing OFF and never overwritten. Deleted products return
`410 product_deleted`; manual nutrient edits mark `nutritionSource: user` while
retaining external identity and original source metadata. History remains unchanged.

The singleton OFF client uses a 5-second timeout, no redirects/cookie jar, a custom
User-Agent and a 2 MiB response limit. Its 1000-entry cache retains searches 5 minutes,
cards 15 minutes and missing cards 1 minute; identical concurrent reads share a request.
Outgoing rolling budgets are 10 searches/minute and 15 product reads/minute for this
single service instance. OFF 429 responses pause requests according to Retry-After;
errors are not retried automatically. `429 external_rate_limited` includes retry time;
`503 external_unavailable` and `502 external_invalid_response` leave the catalog alone.
Existing personal products remain usable offline from OFF. Multiple replicas/shared
egress require a shared rate budget before deployment; the current deployment is one
Nutrition instance.

`OpenFoodFacts__BaseUrl` is trusted operator configuration, defaulting to
`https://world.openfoodfacts.org`; only disposable tests replace it with the HTTP
fixture. It is never a tool argument. No authentication or write requests are sent
to OFF. Attribution: [Open Food Facts](https://world.openfoodfacts.org), available
under [ODbL](https://opendatacommons.org/licenses/odbl/1-0/). OFF is community data,
with no guarantee of accuracy; validation does not imply independent manufacturer
verification. Preserve attribution in redistribution and follow OFF reuse terms.

The AI searches personal products first, resolves ambiguous flavor/brand/package
details, reads the selected OFF card, imports with its fingerprint and records actual
user-supplied grams. Unambiguous checked products need no redundant confirmation.
Photos, portions/pieces, inferred weights and internal AI services are not included.

## Verified custom foods through MCP

After a personal catalog miss, `nutrition_search_reference_foods` calls authenticated
`GET /api/nutrition/reference-foods?search=` with English food/preparation terms.
`nutrition_create_custom_product` uses existing `POST /api/nutrition/products` with
`id`, name/optional brand, complete per-100g calories/protein/fat/carbohydrates,
`source: {provider: usda, foodId, url}`, `foodState` and optional `cookingMethod`.
The server fetches USDA independently and compares all four values, rounded to three
decimal places. It accepts Foundation, SR Legacy and Survey (FNDDS), not arbitrary
branded substitutes for homemade food. Incomplete search values are null, never zero-filled.

USDA data are [public domain / CC0](https://fdc.nal.usda.gov/api-guide/).
`Nutrition__UsdaApiKey` optionally supplies a free data.gov key via `X-Api-Key`.
Default `DEMO_KEY` is evaluation-only: 30 requests/hour, 50/day per IP. Requests use a
USDA HTTPS origin, numeric IDs, no redirects/cookies, five-second timeout and
2 MiB response limit; supplied URLs are validated provenance, never fetch targets.
Only trusted deployment configuration can override `Nutrition__UsdaBaseUrl` (an origin,
like the existing OFF fixture setting); MCP cannot supply an HTTP target.
No paid API, AI service, extra container or public port. BLS is **not** implemented; the existing Open Food Facts import is preserved.
Additional sources need separate verification
and licensing review, not a URL allow-all.

`foodState`: raw/ready/cooked. Cooked requires `cookingMethod`: cooked (unspecified),
roasted/baked/grilled/boiled/fried/air_fried/steamed/stewed/braised. A different method
requires explicit user confirmation and `confirmedPreparationAnalog: true`; saved
provenance records `preparationAnalog: true` alongside the original source description.
Confirmation cannot override raw/cooked mismatch. Matching food identity, ingredients
(including oil), brand, grams and date remains the client's job. Do not convert raw
mass to cooked mass without known yields. Source verification proves reference values,
not that the user's meal exactly equals that reference.

Products and new entries expose source description, dataset type, URL, verification
timestamp, preparation and `source_verified_reference` status: reference averages,
**not exact lab measurements of the meal**. Manual edits mark product provenance
`user_edited`. Entry nutrients and provenance are independent snapshots; existing
history is neither rewritten nor backfilled with invented sources.

Custom creation saves the product and UUID retry key atomically. Reuse the same UUID
and payload after a timeout; replay works even if USDA is offline. New keys for the
same per-user source/brand/preparation reuse the existing product and persist aliases.
Use the **returned ID** for `nutrition_create_entry`. Conflicting keys return 409;
deleted products return 410 and are not resurrected. Manual products without sources
are not merged just because names look similar. After successful creation, save eaten
grams/date with a stable entry UUID. A fresh independent UUID is a new operation:
check the diary and clarify if a natural-language command might be a retry or another
identical meal. Report partial success when product creation succeeds but diary fails.

Missing/unsupported sources, missing nutrients, mismatched values, unconfirmed analogs
or unavailable USDA stop automatic creation; never guess values. Existing CI uses
the existing catalog-fixture container for both OFF and USDA (`--usda-fixture`) and
the disposable OAuth/API smoke. The same smoke supports
`--with-usda` for a live empty-catalog → verified chicken → 150 g flow through real
OAuth and .NET. Run only against a fresh disposable loopback stack, never production.
No new workflow or duplicate image builds are needed.

## SQLite, Docker and migrations

Both Compose files mount `nutrition-data:/data` (normally `opengym_nutrition-data`).
The image creates `/data` owned by its non-root app user. Docker initializes an empty
named volume with that ownership. The database is `/data/nutrition.db`; WAL/SHM files
stay beside it. Container recreation, updates and exact-image rollback retain the
volume. **Do not use `docker compose down -v` or prune this volume.** If substituting
a bind mount, create it with permissions for the image's `APP_UID` first.

`Nutrition__DatabasePath` overrides the file path; native development defaults to
`data/nutrition.db` under the service's content root. Each test uses an isolated
temporary SQLite file. Never point tests at deployment data.

Committed EF migrations run before the service starts accepting requests. This MVP
uses one service instance and accepts startup-migration tradeoffs; migration failures
stop startup, never silently reset the database. EF uses its SQLite migration lock.
After an interrupted migration, inspect the database and backup before clearing any
abandoned `__EFMigrationsLock`; do not automatically drop locks or delete the database.
For future schema changes: inspect migration SQL, test upgrade on a copy, take a full
backup, and keep migrations compatible with the previous image when CD rollback is
needed. Image rollback **does not undo a database migration**.

Developer migration workflow from `nutrition/`:

```sh
dotnet tool restore
dotnet ef migrations add DescriptiveChange --project src/OpenGym.Nutrition/OpenGym.Nutrition.csproj --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/OpenGym.Nutrition/OpenGym.Nutrition.csproj
```

The fork still publishes all three tested images (`api`, `web`, `nutrition`) to GHCR
from main, with a shared `sha-<commit>` tag. Proxmox uses the existing transactional
deployment script; no host/timer changes are included here. The installed Compose
file must include the new volume before deploying this feature. See
[PROXMOX_CD.md](PROXMOX_CD.md) for the controlled installation/update procedure.

## Complete instance backup and restore

Nutrition is **not** part of workout JSON exports or client auto-backup. The existing
server `data/` archive must be paired with the Nutrition volume archive. For a
consistent instance backup, pause the CD timer if installed, stop both writers, and
archive both stores. Use the same Compose file/project as the running stack (for
Proxmox, add `-f compose.prod.yml` to every `docker compose` command below).

```sh
mkdir -p backups
chmod 700 backups
docker compose stop api nutrition
tar czf backups/opengym-data.tar.gz data/
docker compose run --rm --no-deps --user 0 --entrypoint tar nutrition -C /data -czf - . > backups/opengym-nutrition.tar.gz
docker compose up -d api nutrition
```

Copy both archives together to protected backup storage. Do not copy only a live
`nutrition.db`: committed data may still be in its WAL. Check both command exit codes
and archive contents before treating a backup as successful. Restart writers even
if backup fails. Exclude `data/uploads` only if knowingly omitting workout media.

Restore into an empty instance with the matching app/schema version, preserving
`data/secret` and user ids; mismatched account data makes the diary inaccessible.
Stop the writers, restore the data archive, create the Nutrition volume, then:

```sh
docker compose create nutrition
docker compose run --rm --no-deps --user 0 --entrypoint tar nutrition -C /data -xzf - < backups/opengym-nutrition.tar.gz
docker compose up -d api nutrition
```

Do not overlay an older archive onto a nonempty SQLite volume (stale WAL/SHM could
remain). Preserve existing data separately and follow a deliberate restore procedure.
Keep archive numeric ownership when restoring. These archives contain private diary
and account data and are not encrypted by the application. Protect them accordingly.

## Development and verification

Use Node 22 and the .NET 10 SDK. `scripts/dev.ps1` starts the existing three local
processes with hot reload; [LOCAL_WINDOWS.md](LOCAL_WINDOWS.md) documents setup.
Node defaults to `NUTRITION_URL=http://127.0.0.1:5080`; ASP.NET uses
`OpenGym__ApiBaseUrl=http://127.0.0.1:3000`. Those are trusted internal origins, never
client-supplied configuration.

```sh
cd nutrition
dotnet test tests/OpenGym.Nutrition.Tests/OpenGym.Nutrition.Tests.csproj -c Release
cd ../api
node --test test/server-nutrition.test.js
cd ../frontend
npm test -- src/views/Nutrition.test.jsx
node scripts/check-locales.mjs
npm run build
```

Tests cover exact decimal values, snapshots, CRUD/search/pagination, user isolation,
real authentication middleware, SQLite restart/repeated migration, concurrent/repeated
creation, gateway validation/CSRF, and React forms/date/account/error paths. CI's
three-container smoke now exercises real diary CRUD as well as auth through nginx →
Node → ASP.NET → `/api/me`, using disposable `PASSWORD_LOGIN=1` test accounts only.
Native local smoke uses the same script against an isolated API, not production.

MVP limitations: online only, no nutrition import/export in client backups, last-write
wins for simultaneous edits, no automatic orphan/tombstone cleanup or account-deletion
propagation. These retained rows are not visible to another account; deliberate
retention/purge policy belongs to a later account-lifecycle task.
