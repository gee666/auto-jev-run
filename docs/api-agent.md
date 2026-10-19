# AutoJev local API

Base URL: the origin serving this document, normally `http://localhost:8765`. Use the same host and port for every path below. Localhost refers to the machine running AutoJev; it is not accessible from a remote agent's machine. This API evaluates text/images and returns decisions and probabilities. It does not generate chat text, stream tokens, or implement OpenAI chat/completions.

## Connection and authentication

Send `Content-Type: application/json`. For every model request, send `Authorization: Bearer YOUR_KEY` or `X-API-Key: YOUR_KEY`. Missing keys return 401 and are not tracked. Unknown/revoked keys, cloud keys and dummy keys such as `local` return 401. Do not retry anonymously to hide a supplied-key error.

Set the client timeout to at least 1200 seconds for a single request, and longer when queueing batches. Model loading alone can take roughly 9 seconds. The TypeSafe SDK's 10-second default is too short. With that SDK, use this base URL without `/v1`, your local key, and `timeout=1200`.

## Browser origins

In `config/server.json`, set `"allowedOrigins": ["*"]` to allow all origins, or list exact origins such as `["http://localhost:3030", "https://my-app.example", "chrome-extension://YOUR_ID"]`. Run `AutoJev.cmd restart`; no rebuild or relay is needed. Inference still requires an API key. This applies to `/v1/systemone`, `/v1/models`, `/health` and `/v1/health`, not `/local/*`.

## Evaluate: POST /v1/systemone

```json
{
  "model": "jev-latest",
  "state": "A software vendor released a patch for a critical vulnerability.",
  "questions": {
    "security": {
      "type": "noul",
      "instructions": "Is the article about computer security?"
    },
    "topic": {
      "type": "choice",
      "instructions": "Select the primary topic.",
      "criteria": {
        "technology": "Software, computers or cybersecurity.",
        "weather": "Weather forecasts or storms.",
        "other": "None of the listed topics."
      }
    },
    "urgency": {
      "type": "score",
      "instructions": "Rate the urgency of acting on this article.",
      "criteria": ["No action needed", "Review soon", "Act immediately"]
    }
  }
}
```

- `state` is required: a string, JSON object or array. No scalar numbers, booleans or null.
- `questions` is required: a nonempty object with caller-chosen unique names. All questions share the state and images. Use `criteria`, not `options`.
- `model` is optional, default `jev-latest`. `autojev`, `autojev-27b`, `autojev-27b-q4_k_m` and `jev-*` aliases all select the same local model. Discover names with `GET /v1/models`.
- `instructions` describes the question. Text is simplest; structured objects/arrays are also supported for instructions and criterion descriptions.
- `noul` means yes/no: optional `criteria` has string keys `"true"` and/or `"false"` describing each outcome.
- `choice` requires a `criteria` object mapping option names to descriptions, up to 255 options. Returns one selected option, not multiple tags. For independent tags, send one `noul` question per tag. Large option sets can reduce accuracy; 255 is a format limit, not a quality guarantee.
- `score` requires 1–10 ordered descriptions. Indices run from 0 to N-1. One level deterministically returns 0 without evaluating the model; use at least two for a meaningful evaluation.

Successful HTTP 200 responses contain `model`, `answers` keyed by your question names, and `usage`:

| Answer type | Fields and interpretation |
|---|---|
| `noul` | `type`, `noul`: probability of true, 0–1; apply your own decision threshold. |
| `choice` | `type`, `choice`: selected option name; `probabilities`: option-to-probability map; `confidence`: separate model confidence metric, not necessarily the selected probability. |
| `score` | `type`, `score`: probability-weighted mean index, possibly fractional; `probabilities`: index-to-probability map with JSON string keys; `legend`: index-to-description map; `confidence`. |

`usage.input_tokens` counts evaluated input, including reused tokens. `usage.output_tokens` is normally 0 because this is a decision model. Responses identify the actual model as `autojev-27b-q4_k_m`. Probabilities are estimates; validate thresholds on your own examples.

## Images and limits

Optional top-level `images` is an array of up to four `data:image/png;base64,...`, `data:image/jpeg;base64,...` or `data:image/webp;base64,...` strings. No URLs or file paths. Each decoded image must be at most 8,000,000 bytes and 16 megapixels. Images share the context budget; crop small text. Use `state: ""` for image-only input. Check `visionEnabled` in `GET /local/status` before sending images.

Current defaults: 48 MiB HTTP body, 12,288-token context, 300-second model idle unload. Oversized context is rejected, not truncated. `GET /local/status` gives current limits/settings, model state, and `activeRequests`/`waitingRequests`. `GET /health` indicates API availability, including unloaded/loading states; it does not preload the model. Status and documentation requests do not keep the model loaded.

## Queue and retries

Send separate HTTP requests for separate inputs; there is no batch-job endpoint. One request runs at a time, with up to 1,024 waiting by default. Queued callers keep their connections open. Default queue wait has no deadline; the 1200-second processing deadline starts at the front. Avoid unnecessary concurrency: it increases waiting, not model throughput. Disconnects cancel requests; restarts lose pending work. There is no idempotency/deduplication guarantee, so retries may repeat completed work.

On failure, inspect the HTTP status and JSON `error.message` when present. Preserve `X-TypeSafe-Request-Id` for diagnosis.

| Status | Action |
|---|---|
| 400 / 422 | Fix JSON, question schema, image input or context size using the returned message. |
| 401 | Correct the supplied local key. |
| 403 | Set `allowedOrigins` in `config/server.json` to `["*"]` or your exact origin, then restart. Local management remains restricted. |
| 404 / 413 / 415 | Correct endpoint/model, reduce payload, or use JSON content type respectively. |
| 429 | Queue full; respect `Retry-After`, then retry with bounded backoff. |
| 500 / 502 / 503 / 504 | Inspect the message/status, then use bounded retries for transient failures. 504 means queue or processing timeout. |

Dashboard status 499 means the caller disconnected or work was interrupted; it is a stored diagnostic, not a response sent to that caller. Check client timeouts first.

## Prefix cache modes

Send `X-AutoJev-Cache-Mode` with one value: `none` uses the original trained prompt without reuse; `options` caches fixed question/options before changing state and may change accuracy; `context` preserves State -> Question -> Options order and reuses exact shared state/question text before changing options. Different question text is not falsely reused. Images bypass both caches and report `none`. These are model checkpoints, not cached answers.

Omission follows the effective server `cacheMode`, initially `none`. Legacy `X-AutoJev-Prefix-Cache: true|false` maps to `options|none` only when the new header is absent. The new header wins even over an invalid legacy value; an invalid new value returns 422. The mode applies to all questions in that request.

Response `X-AutoJev-Cache-Mode` reports the applied mode. `X-AutoJev-Cached-Tokens`, `X-AutoJev-Cache-Write-Tokens` and `X-AutoJev-Checkpoint-Bytes` report reuse, saved tokens and recurrent checkpoint bytes. These headers are exposed to allowed browser origins. A cold request can apply a cache mode with zero reused tokens. Idle TTL is `prefixCacheSeconds`, normally 300 seconds; model unload/restart discards checkpoints. `X-AutoJev-Queue-Milliseconds` reports queue wait.

## App keys and usage

Use a separate key per application. The local UI at `/usage` creates, renames and revokes keys and shows per-request tokens, cache use, status and timing. Every model request requires a valid key. The UI, documentation, health and local management remain accessible without a key on loopback.

- `POST /local/keys` with `{"name":"My app"}` returns `id`, `name`, and `key` once. Store the secret in your application's configuration.
- `PATCH /local/keys/ID` with `{"name":"New name"}` renames; `POST /local/keys/ID/revoke` with `{}` revokes while preserving history.
- `DELETE /local/keys/ID` with `{}` permanently deletes an app key and all its usage history. Returns 409 if requests for it are active/queued; revoke, wait, then delete. Built-in keys cannot be deleted.
- `GET /local/usage?days=0&keyId=ID&offset=0` returns all key summaries plus 50 request rows filtered by key. Omit `keyId` for all apps; `days=0` means all time.
- For deliberate playground/testing traffic only, `GET /local/dashboard-key` returns the automatic Dashboard & testing key. It rotates at server restart. Do not use it for production app attribution.

Management is local and requires no login. SQLite stores usage and key hashes, not request content or raw app keys. Failed requests may have unknown token counts. Never put secrets into URLs.
