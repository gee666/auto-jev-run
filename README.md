# AutoJev Local

A local, general-purpose decision server using AutoJev-27B Q4_K_M. It runs natively on Windows. The launcher, inference binaries, runtime libraries, source code, model, and research reports are all in this folder.

See the [consolidated report of all tested models](reports/all-tested-models.md) for Bash scores, architectures, weight sizes, hardware evidence, startup times, and response times by context length.

## Start and stop

Double-click **Start AutoJev.cmd**. It starts the tray application and local API, then opens the playground. The model stays unloaded until an inference request arrives.

Double-click **Stop AutoJev.cmd** to stop the launcher and release the model's RAM and GPU memory.

For applications or a terminal:

```powershell
.\AutoJev.cmd start
.\AutoJev.cmd status
.\AutoJev.cmd stop
.\AutoJev.cmd restart --cpu
.\AutoJev.cmd restart --cuda
```

`start` returns exit code 0 once the API is available, including when the model is unloaded. Calling it again reuses the running server. `status` returns 0 for an available API, including loading and unloaded states. Use `serve` instead of `start` if your application needs to own the launcher process without a tray icon. The inference child is automatically terminated when its launcher exits or crashes.

## Tray, startup, and idle memory

Look for the green **A** icon in the Windows notification area, possibly under the hidden-icons arrow. Right-click it to open the web interface, open Settings, toggle **Start when I sign in**, unload the model immediately, or quit the whole application. Double-click opens the web interface. Closing Settings leaves the tray and API running; Quit stops both and releases model memory.

You can also open Settings with **AutoJev settings.cmd** or `AutoJev.cmd settings`. Settings let you change the API port, internal model port, CPU/CUDA mode, image input, idle timeout, and sign-in startup. Saving restarts the API and unloads the model. Active requests must finish first. If you change the API port, update client URLs and open the web interface again from the tray. An occupied port is rejected before changing the running server.

Startup is optional and currently enabled on this installation. It uses a project- and user-specific Windows Scheduled Task named `AutoJev-55888808-<user SID>` here. The task waits 15 seconds after sign-in, runs in your interactive session without elevation or a stored password, and retries a failed launch up to three times at one-minute intervals. It has no execution time limit and permits battery operation. It starts the tray/API silently without opening a browser or loading weights. The legacy Run registry entry is removed after task registration succeeds.

Toggle **Start when I sign in** off to remove the task, or use the Settings checkbox. Command-line equivalents are `AutoJev.cmd startup enable`, `AutoJev.cmd startup disable`, and `AutoJev.cmd startup status`. Manage this task through AutoJev or Windows Task Scheduler, rather than the legacy Run-entry list in Task Manager. Early diagnostics are in `logs/startup.log`, with a fallback under `%LOCALAPPDATA%/AutoJev/startup.log` if the project log cannot be written. See [startup repair validation](reports/startup-repair-2026-09-27.md).

The default idle timeout is **300 seconds**, five minutes. A completed inference request resets the timer. Concurrent requests queue behind one model load and reuse the same process. Active or queued inference prevents idle unloading. On expiry, the inference process exits and releases its RAM and GPU allocations, while the small tray/API process remains available. The next inference request loads the model and returns its answer within that same HTTP request. Health checks, model discovery, and the web page do not load the model or extend its idle timeout. A failed load can be retried by a later request. Canceling a request while it loads stops that load; a queued request can start a new load.

All apps share a FIFO queue with one active request and **1,024 waiting requests** by default. Accepted requests can wait as long as the caller stays connected; the 20-minute processing timeout starts at the front of the queue. Queue size and separate waiting/processing timeouts are available in Settings. Clients must allow enough HTTP time for the backlog, and retry 429 responses if capacity is reached. Requests do not survive disconnects or restarts. See [using the queue from applications](docs/applications.md).

Cold requests include model loading time, around nine seconds in the current CUDA tests plus inference. Keep a sufficiently long client timeout. This is low idle resource use, not zero: the tray, local HTTP listener, and idle timer remain running. The initial unloaded tray/API working set measured approximately 81 MB on this machine.

`/local/status` exposes `status` as `unloaded`, `loading`, `ready`, or `failed`, plus `backendPid`, `idleUnloadSeconds`, `startupEnabled`, and `tray`. `/health` returns 200 while the API can accept requests, including unloaded/loading states; inspect `status` when you need to know whether weights are loaded. Control endpoints require the local control token.

## API guide for agents

Give agents [http://localhost:8765/docs](http://localhost:8765/docs). It serves a self-contained Markdown guide, without requiring JavaScript or a key. Source: [docs/api-agent.md](docs/api-agent.md).

## Use it from a Jev application

**Provider base URL: `http://localhost:8765`**

**Endpoint: `POST http://localhost:8765/v1/systemone`**

For the official TypeSafe Python client, the base URL does **not** include `/v1`. Change `TYPESAFE_BASE_URL` from `https://api.typesafe.ai` to `http://localhost:8765`. The existing `jev-latest`, `jev-preview`, and `jev-*` version names work as local compatibility aliases. The response identifies the real model as `autojev-27b-q4_k_m`.

Create a free local app key at [API keys & usage](http://localhost:8765/usage), then send it as `Authorization: Bearer YOUR_KEY` (or `X-API-Key`). Every model request requires a valid key. Missing keys return 401. Unknown or revoked keys return 401, including the old placeholder `local` and hosted-provider keys. Set the key alongside the provider URL when switching providers.

```python
import os
from typesafe_sdk import TypeSafeClient, Noul

with TypeSafeClient(
    base_url="http://localhost:8765",
    api_key=os.environ["TYPESAFE_API_KEY"],
    timeout=1200,
) as client:
    result = client.system_one(
        state="Python: a = [1, 2]; b = a; b.append(3)",
        questions={"shared": Noul(instructions="Does a now contain three elements?")},
    )
    print(result.nouls["shared"].noul)
```

The same client code works with hosted Jev by using its base URL and your real key. Switch both URL and key; a hosted credential is not a local app key. CPU-only requests can take several minutes, so the SDK's short default timeout may need increasing. API compatibility does not mean equal model accuracy or calibration.

See [the API contract and limits](docs/api-compatibility.md) and [examples](examples/). The service supports the official `noul`, `choice`, and `score` response shapes, structured states, model discovery, and synchronous or asynchronous clients. The official Python SDK 0.7.1 was used for integration tests.

## API keys and usage

Open [API keys & usage](http://localhost:8765/usage) to create, rename or revoke app keys, filter usage by app and period, and inspect individual requests. The playground automatically uses its built-in **Dashboard & testing** key. Requests without a key are rejected without usage tracking. Usage persists in `data/usage.sqlite`; secrets, prompts and answers are not stored there. See [tracking details](docs/usage.md).

## Prefix cache modes

Send `X-AutoJev-Cache-Mode: none|options|context`, choosing one value. `none` is the default, with the original trained prompt and no reusable cache. `options` puts fixed questions/options before changing state; this can change accuracy. `context` keeps State -> Question -> Options order and reuses exact shared state/question prefixes before changing options. Images bypass both caches and report `none`. No answers are cached.

The playground selects a per-request mode and displays the applied response mode; tray Settings selects the default and lifetime. The legacy `X-AutoJev-Prefix-Cache: true|false` still maps to `options|none` when the new header is absent. See [the cache contract](docs/options-cache.md). See [context-cache validation](reports/context-cache-2026-10-10/report.md) and [options-first measurements](reports/prefix-cache/report.md).

## Web question builder

Open http://localhost:8765 after starting the server. The workbench follows Decision Studio's shared-context approach:

1. Paste plain text or select JSON for a structured object or array. Optionally upload up to four PNG, JPEG or WebP images; image-only requests also work.
2. Add any combination of yes/no, choice, and score questions. Give each a unique result name and clear instructions.
3. For choices, enter a short option name and a description of when it applies. For scores, describe levels in ascending order; the arrows let you reorder them. Yes/no criteria are optional.
4. Evaluate to see probability bars, the selected answer or expected score, elapsed request time, and input token count.

The programming examples provide starting points. Expand **Generated API request** to copy the exact JSON into another application; **Raw API response** shows the original result. Validation catches missing descriptions, duplicate names, and invalid JSON before sending. Cancel stops waiting and forwards cancellation to the local server. Results remain labeled as a previous request when you edit the form.

The UI uses no external assets or hosted inference. Form inputs are not saved across page refreshes. The score is the expected zero-based level index, not a percentage. Request time includes local queueing and inference; it includes model loading when the model is sleeping.

## Folder map

| Folder | Contents |
|---|---|
| `app/` | Ready-to-run launcher, its bundled .NET runtime, and playground |
| `src/AutoJev.Local/` | Launcher and API bridge source |
| `repos/jev.cpp/` | Pinned upstream inference repository, including the small Windows build fix |
| `repos/typesafe-sdk-python/` | Official SDK source used to verify compatibility |
| `models/autojev-27b/` | The 15.83 GB Q4_K_M model, 0.93 GB vision projector, and checksum metadata |
| `runtime/windows-cuda/` | Tested CUDA inference engine and required CUDA/VC libraries |
| `runtime/media/` | Bundled FFmpeg/FFprobe helpers for WebP decoding, license and provenance |
| `runtime/windows-cpu/` | Tested CPU inference engine and required VC libraries |
| `config/server.json` | Ports, CPU/CUDA mode, context limit, threads, and timeouts |
| `reports/` | Research, benchmark results, integration checks, and cleanup record |
| `tests/` | Reusable integration checks; not needed to run the server |
| `scripts/` | Build and model verification helpers |
| `logs/` | Launcher and backend diagnostics; request bodies and API keys are not logged by the bridge |
| `state/` | Local process lock, temporary files, and private backend/control tokens |

## Hardware and settings

The default is the tested RTX 4070 configuration, with automatic partial GPU offload, 12,288 tokens, 8 CPU threads, and 1,536 MiB GPU reserve. The weights exceed 12 GB VRAM, so some remain in system RAM. Image input is enabled with the matching BF16 vision projector. The existing Q4_K_M model is unchanged. The projector and WebP helpers add about 1.13 GB on disk. Disable image input in tray Settings when you only need text and want to avoid the extra vision memory allocation. Both components load on demand and unload together. See [image input and measured performance](reports/vision-validation.md).

### Browser origins

In `config/server.json`:

```json
"allowedOrigins": ["*"]
```

`*` allows all origins. To restrict access, list exact origins instead, e.g. `["http://localhost:3030", "https://my-app.example"]`. Run `AutoJev.cmd restart`; no rebuild or relay is needed. API keys are still required for inference. This setting covers model/health APIs, not local management.

Edit `config/server.json`, then run `AutoJev.cmd restart`. Ports default to 8765 for your applications and 8766 for the private inference backend. Both bind only to this computer. The private backend uses its own generated key. An occupied port causes a clear failure; the launcher does not kill other applications or silently pick a different provider URL.

The packaged inference binaries were built and tested for this Windows x64 machine. A different CPU or GPU may need a rebuild. Windows and the NVIDIA display driver remain system prerequisites. Python, Ollama, a separate .NET installation, and the CUDA toolkit are not needed to run this packaged server.

## Build and verify

The launcher build requires the .NET 8 SDK and internet access to restore the pinned runtime on the first build:

```powershell
.\AutoJev.cmd stop
.\scripts\Build-Launcher.ps1
.\scripts\Verify-Model.ps1
.\AutoJev.cmd start
```

The tested `jev.cpp` commit is `aa885f0a86efe36079d70b522f927cd2fc38683b`. Local patches include the MSVC fix and both prefix cache modes. Apply `patches/autojev-prefix-cache.patch` first, then `patches/autojev-context-cache.patch`, when preparing a fresh native checkout. See [runtime provenance](runtime/provenance.json). Do not replace these inference binaries with standard llama.cpp or Ollama; AutoJev's classifier head requires this fork.

Sources: [original AutoJev code and training](https://github.com/denis-pplx/autojev), [original model on Hugging Face](https://huggingface.co/denis-pplx/autojev-27b), [installed GGUF weights](https://huggingface.co/thomasgauthier/autojev-27b-GGUF), [native inference fork](https://github.com/thomasgauthier/jev.cpp), [official TypeSafe SDK](https://github.com/typesafe-ai/typesafe-sdk-python).

## Validation and cleanup status

See [capacity and caching tests](reports/capacity-2026-09-27/report.md) for measured option limits, repeated-input caching, concurrent requests, and RAM/VRAM use on this machine. The recommended configuration remains one inference slot with a bounded queue.

The packaged service passed official SDK checks in both CUDA and CPU modes, normal shutdown, and launcher-crash cleanup. See [setup validation](reports/setup-validation.md).

Automatic approval review blocked removal of the old testing folder and the new `.build` cache. They are not runtime dependencies. The validation report lists the exact folders that remain and can be removed manually, while preserving this project and the research reports.
