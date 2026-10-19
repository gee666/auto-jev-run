# Using AutoJev from your apps

Keep using `POST http://localhost:8765/v1/systemone`. The request and response formats have not changed. With the TypeSafe SDK, set the base URL to `http://localhost:8765`, without `/v1`.

All apps share one FIFO queue. The server evaluates one request at a time, which was fastest on this machine. Each caller waits on its original HTTP connection and receives only its own result. Apps can submit concurrently; they do not need a job ID, polling, or a new API.

## Defaults

| Setting | Default | Meaning |
| --- | --- | --- |
| `maxQueuedRequests` | 1024 | Waiting requests, in addition to one active request |
| `queueTimeoutSeconds` | 0 | No server waiting deadline while the app stays connected |
| `requestTimeoutSeconds` | 1200 | Processing deadline, starting at the front of the queue |
| `idleUnloadSeconds` | 300 | Unload the model after five idle minutes |

You can change the queue controls through the tray's Settings window. Saving requires active and waiting requests to finish. Changing `config/server.json` requires a restart. Queue capacity can be 0 to 10,000; zero permits only an active request. A nonzero waiting timeout can be up to 86,400 seconds.

Processing includes receiving/parsing the active body, model loading, inference and returning the answer. Waiting has its own deadline. Only the active request is parsed into JSON; waiting uploads are subject to HTTP transport backpressure. A slow upload at the front can delay later requests, so apps should send their complete body promptly.

## Set the app's timeout too

A short client timeout disconnects the request even when the server is willing to wait. The archived TypeSafe SDK defaults to ten seconds. For example, allow up to one hour per HTTP operation:

```python
from typesafe_sdk import TypeSafeClient, Noul

with TypeSafeClient(
    base_url="http://localhost:8765",
    api_key="YOUR_LOCAL_APP_KEY",  # Create one in /usage; use the hosted key for the hosted provider.
    timeout=3600,
) as client:
    result = client.system_one(
        state="Python: a = [1, 2]; b = a; b.append(3)",
        questions={"shared": Noul(instructions="Does a now contain three elements?")},
    )
    print(result.nouls["shared"].noul)
```

One hour is an example, not a guarantee that every backlog clears within that time. Configure any proxy and your app's own overall deadline consistently. At 2.5 seconds per request, 100 requests take about four minutes to drain; at 14 seconds, they take about 23 minutes. These estimates exclude model loading and variation. A full 1,024-request queue could take hours.

For sustained work, keep the submission rate below processing capacity or keep pending work in the producer and send it as earlier calls finish. A larger queue absorbs bursts; it does not increase inference speed.

## Status and failures

- The playground shows the shared active and waiting counts. `GET /local/status` exposes the same counts without loading the model.
- An accepted response includes `X-AutoJev-Queue-Milliseconds`. The legacy `queuedRequests` status field still means active plus waiting; use `waitingRequests` for the actual backlog.
- At capacity, the server returns 429 and `Retry-After: 2`. Keep the input in the app and retry after that delay, with jitter if many apps retry together. The SDK retries transient errors by default, but its retry budget is finite. Handle exhausted retries rather than discarding work.
- If you set a waiting deadline, expiration returns 504 and removes that request from the queue. Processing expiration also returns 504, with a different error message.
- Invalid input returns an error; it is not retried internally. Backend load failures return 503 and disconnections return 502. A failed request releases its place so the next caller can proceed.
- Disconnecting or canceling removes a waiting request. Quitting AutoJev or restarting Windows cancels pending work. The queue is in memory and does not save requests or results to disk.

All apps share acceptance order. The queue does not reserve per-app priority: a large batch from one app can delay another app. This keeps the existing JEV API unchanged. It cannot guarantee delivery across disconnects or accept an unlimited arrival rate.
