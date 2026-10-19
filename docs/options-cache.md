# Prefix cache modes

Keep the URL, `/v1/systemone` endpoint and JSON body unchanged. Send one `X-AutoJev-Cache-Mode` value for all questions in the request:

| Value | Prompt and reuse |
|---|---|
| `none` | Original State -> Question -> Options order, no reusable cache. Initial server default. |
| `options` | Fixed question/options first, changing state afterward. Reuses their prefix. Experimental reordering can change accuracy. |
| `context` | Original State -> Question -> Options order. Reuses the longest safe exact shared prefix before options. Same state with different questions can share state; identical questions with different options can also share question text. |

Only exact matching text/token prefixes can be reused. Different question text must still be evaluated, along with the current options. No answers are cached and no prewarm call is needed. Images bypass both cache modes and report `none`: safe pixel identity and multimodal token offsets are not established. Text-only requests can cache while vision support is enabled.

## Defaults and compatibility

The new header overrides the legacy `X-AutoJev-Prefix-Cache: true|false`, even when the legacy value is invalid. With no new header, the old boolean maps to `options|none`; invalid headers return 422. With neither header, nullable `cacheMode` in `config/server.json` wins when set; absent/null falls back to `prefixCacheEnabled`, true for `options`, false for `none`. Invalid configured modes fail validation. Tray Settings saves an explicit mode. Request overrides never change the server default.

The wrapper always sends the effective request mode to native via `X-AutoJev-Cache-Mode`. Native's `AUTOJEV_CACHE_MODE` uses the same values; old `AUTOJEV_OPTIONS_FIRST`/`AUTOJEV_PREFIX_CACHE` are fallbacks only when the new environment/header is absent. Private legacy `1|0` remains supported. Public legacy values remain `true|false`.

## Lifetime and observation

`prefixCacheSeconds` remains the idle TTL, normally 300 seconds. Retained checkpoints are capped per slot at 256 MiB for `options`, 512 MiB for `context`, and are not persisted; expiry, mode/prefix changes and model unload/restart can discard reuse. A miss still evaluates the full request. Model idle unloading is separate from checkpoint expiry.

Response headers, also exposed through CORS:

- `X-AutoJev-Cache-Mode`: applied mode, including `none` for images or requests answered without inference. An applied cache mode does not guarantee a hit.
- `X-AutoJev-Cached-Tokens` and `X-AutoJev-Cache-Write-Tokens`: reused and saved prefix tokens, summed across questions.
- `X-AutoJev-Checkpoint-Bytes`: retained recurrent checkpoint bytes, excluding attention KV buffers and weights.

`GET /local/status` reports the effective default `cacheMode`, legacy `prefixCacheEnabled`, and TTL. The playground shows the selected request header separately from copied JSON and displays the applied mode from the response, plus reused tokens and checkpoint size. If an older native runtime omits the mode header, text requests show `unknown` rather than claiming the selected mode.

See [context-cache validation](../reports/context-cache-2026-10-10/report.md) and [options-first measurements](../reports/prefix-cache/report.md). Check accuracy on your own data before using reordered prompts.
