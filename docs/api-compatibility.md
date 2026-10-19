# Jev API compatibility

This bridge implements the two endpoints in the official TypeSafe OpenAPI snapshot saved beside this file. It uses the original AutoJev inference implementation for model input formatting, temperature calibration, and probability calculation.

| Route | Behavior |
|---|---|
| `POST /v1/systemone` | Text or structured state, optional images, and named `noul`, `choice`, `score` questions |
| `GET /v1/models` | TypeSafe `models` array with name, description, and release date fields |
| `GET /health`, `/v1/health` | 200 when the API accepts requests, including unloaded/loading model states; 503 after a model failure |
| `GET /local/status` | Model state, mode, process IDs, queue count, idle timeout, startup registration, tray presence, and project location |
| `GET /` | Browser playground |

The base URL is `http://localhost:8765`, without `/v1` when using the official SDK. Local keys are required for model requests and identify apps for usage tracking. Missing, unknown, revoked or deleted keys return 401. Keys are not forwarded to the inference backend. See [keys and usage](usage.md). A separately generated private token authenticates requests to the inference backend. The server never contacts the hosted Jev API.

All `jev-*` request names are mapped to the single installed AutoJev checkpoint. Explicit `autojev`, `autojev-27b`, and `autojev-27b-q4_k_m` names also work. Other model names are rejected. Responses name `autojev-27b-q4_k_m` and include `X-AutoJev-Provider: local-autojev-27b-q4_k_m`; aliases do not disguise the underlying model.

Successful responses preserve the official answer structures and input/output token usage. Structured states and criteria pass through to the upstream engine. A single-level score is handled deterministically as score 0 with probability 1; multi-level scores use the model. The native classifier supports up to 255 choice options and up to 10 score levels. Input exceeding the configured context is rejected, not silently truncated.

Requests use one shared FIFO queue across all apps, with one active request and up to 1,024 waiting requests by default. Order is established when the bridge accepts the HTTP headers into the queue. Bodies are parsed only when a request reaches the front, so waiting requests do not retain parsed JSON documents. Queue overflow returns 429 with `Retry-After: 2`. A request when the model is unloaded loads it and then performs inference within that same HTTP request. Other requests wait for the same load. Load failures return 503; a later request retries loading. Client disconnects and local processing timeouts cancel the request. Error status codes are useful to normal SDK retry handling; every error payload is not guaranteed byte-for-byte identical to the hosted service.

Queue waiting and processing now have separate deadlines. `queueTimeoutSeconds: 0` means no server-imposed waiting deadline while the caller stays connected. Once a request reaches the front, `requestTimeoutSeconds`, default 1,200, covers reading its body, model loading, inference and response writing. An optional queue deadline returns 504 with a queue-specific message; it does not consume the processing budget. Requests and results are not persisted across restarts. See [using the queue from applications](applications.md).

`GET /local/status` exposes `activeRequests`, `waitingRequests`, `maxQueuedRequests`, `queueTimeoutSeconds`, and `requestTimeoutSeconds`. The older `queuedRequests` field remains the total of active plus waiting for compatibility. Responses admitted to the front include `X-AutoJev-Queue-Milliseconds`, the time spent waiting before processing. The playground shows active/waiting counts and the tray shows the waiting count when nonzero.

The model unloads after 300 idle seconds by default, counted from completed inference. Active and queued requests prevent automatic unloading. Health, status, discovery, and web requests never load or keep the model warm. `/health` indicates API availability rather than loaded weights; check the status field for `unloaded`, `loading`, `ready`, or `failed`. The token-protected `POST /local/unload` releases the model only when no requests are active. `POST /local/stop` quits the service and tray; `POST /local/settings/open` opens the native Settings dialog in tray mode. These controls require the project-local control token and are not part of the Jev provider API.

Local limits differ from the cloud. Request bodies default to 48 MiB, processing timeout to 1,200 seconds, and context to 12,288 tokens. Set your application's HTTP timeout to cover queue waiting plus processing, especially in CPU mode. Existing 10-second client timeouts will not reliably cover long inputs. Queue size, queue timeout and processing timeout can be changed through tray Settings or `config/server.json` followed by a restart.

Not included: cloud billing/account APIs, streaming chat/completions, cloud quotas, or nonstandard `/v1/systemone/batches` extensions. The current official OpenAPI snapshot exposes only the two `/v1` endpoints listed above. For batches, issue ordinary requests through the SDK; the local queue processes them in turn.

The browser playground is same-origin. Calls from arbitrary websites are rejected. Native desktop, backend, Python, and command-line clients can call the loopback API directly. A web application hosted at a different origin should use its own backend to call AutoJev rather than assuming permissive CORS.

This is API compatibility, not a reproduction of the closed Jev model. See the benchmark reports before making reliability assumptions.

## Image input

Add an optional `images` array containing up to four base64 data URIs, such as `data:image/png;base64,...`, to the same `/v1/systemone` request. PNG, JPEG and WebP are supported. Each decoded file must be at most 8,000,000 bytes and 16 megapixels. URLs and local file paths are rejected. All questions share the images in array order. An empty text state is allowed with images. See [working Python example](../examples/image_request.py).

Image input is enabled in this installation and can be toggled in tray Settings. `/local/status` exposes `visionEnabled`, `maxImages`, `imageMaxTokens` and `maxBodyBytes`. The matching projector lives beside the existing text GGUF; `py -3.12 scripts/install_vision.py` installs or verifies it against its pinned checksum. WebP uses the bundled FFmpeg/FFprobe helpers in `runtime/media`.

The configured image budget is 64 to 256 visual tokens per image, matching the model metadata. Those tokens share the 12,288-token context with text and options. Crop small screenshot text before submitting. The same FIFO queue and idle unloading apply to images. The model still returns decision probabilities, not free-form image captions. A single-level score remains deterministic and does not evaluate image contents.

## Optional question/options prefix cache

`X-AutoJev-Prefix-Cache: true` opts a request into the experimental question/options-first format and prefix reuse. `false` forces the original order. Omit it to follow the server setting, initially disabled. The JSON body and answer shapes are unchanged. Responses expose reused token count and recurrent checkpoint bytes in HTTP headers. Images always use the original order. See [configuration, lifetime and application example](options-cache.md).
