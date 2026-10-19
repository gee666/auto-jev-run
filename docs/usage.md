# API keys and local usage

Open **API keys & usage** from the playground, or visit http://localhost:8765/usage. Create a named key for each app and copy its secret when shown. You can rename or revoke it later; its history remains. Set `Authorization: Bearer YOUR_KEY` or `X-API-Key: YOUR_KEY`. Unknown and revoked keys return 401. Requests without either header return 401 and are not recorded. Supplied cloud keys and the old `local` placeholder are unknown keys.

The playground automatically obtains a **Dashboard & testing** key. Its secret rotates on each server start, but the group's identity and usage history persist. Automated local tests can fetch the current key from `GET /local/dashboard-key` and send it in the same Bearer header. Give real apps their own keys. Page loads, status checks and usage polling do not count as inference requests or keep the model loaded.

This is a single-user loopback service, with no accounts. Local key management is available without a key; every model request requires a valid key. This is not a multi-user access boundary: local management and the automatic dashboard key remain accessible on loopback. The existing loopback Host and same-origin checks apply to management endpoints.

## What is recorded

Each admitted inference request has a request ID, app key ID, start/completion timestamps, status, input/output token counts, cache reads/writes and total/queue/processing durations. `X-TypeSafe-Request-Id` matches its database record. Unknown credentials appear under **unrecognized-key**; revoked credentials remain attributed to their original app. Errors and cancelled requests are retained. Requests still pending when the service restarts become `interrupted` with status 499; they are not replayed.

- Input/output counts come from the model's response. Input includes reused tokens. This classifier normally generates zero output tokens.
- Cache reads count restored question/options prefix tokens. Cache writes count newly saved reusable prefix checkpoint tokens, summed across evaluated questions. They do not count all prompt processing or bytes of RAM. They are also exposed as `X-AutoJev-Cached-Tokens` and `X-AutoJev-Cache-Write-Tokens`.
- Cache read/write counters are zero when this reusable checkpoint mechanism is unused. Prefix caching remains opt-in; see [cache behavior](options-cache.md).
- Total time covers server handling, including waiting and model loading. Processing is total minus queue time, not pure model compute time. Client network time is excluded.
- Missing measurements, including failed inference without reported counts, are stored as NULL and displayed as a dash. They are never inferred as zero; app totals show the unmetered-request count.

All history starts with this feature's installation. The dashboard filters by app and rolling period, shows totals and average duration, and pages through requests in groups of 50. Averages use available completed timings, including errors. Counters include pending requests. Polling runs every ten seconds while the page is visible.

## Storage and backup

History is stored in `data/usage.sqlite`, excluded from Git. SQLite uses WAL with durable synchronous writes. Key secrets are random 256-bit values; only hashes and short prefixes are stored. Request text, images, answers and raw keys are not stored in this database. History is retained until you remove it. Stop AutoJev before copying the database for a simple backup, or use SQLite's backup API while it runs.

## Local management API

- `GET /local/usage?days=0&keyId=ID&offset=0`: app summaries and request history. Zero days means all time; omit keyId for all apps. The key filter applies to request rows; the response always includes every key's summary.
- `POST /local/keys` with `{"name":"News classifier"}`: returns ID, name and the one-time secret.
- `PATCH /local/keys/ID` with `{"name":"New name"}`: rename.
- `POST /local/keys/ID/revoke` with `{}`: revoke.

Mutations require `Content-Type: application/json`. Built-in groups cannot be renamed or revoked. Status exposes `usageTrackingError` if a completion record could not be saved; the dashboard displays the same warning.

`DELETE /local/keys/ID` with JSON `{}` permanently deletes an app key and its usage history. The UI asks for confirmation. Active or queued requests prevent deletion with 409; revoke the key and wait for those requests to finish. Built-in groups cannot be deleted. Deleted credentials return 401.
