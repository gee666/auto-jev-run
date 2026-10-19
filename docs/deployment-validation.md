# DGX Spark deployment validation

Validated on 2026-10-19 in `/home/oira666/workshop/my-jev`.

- Ubuntu ARM64, NVIDIA GB10, CUDA 13.0, native SM 121 backend.
- Both model files matched their recorded SHA-256 hashes after copying.
- SQLite migration used the online backup API after stopping the source instance. Integrity check passed. All 49,104 original request rows and all non-dashboard key records matched exactly. The internal dashboard key and process-control tokens intentionally regenerate on startup.
- Text decisions, image decisions, all cache modes, three concurrent requests, usage reporting, missing/invalid key rejection, and browser-origin protection passed live tests.
- Browser playground evaluation and historical usage dashboard passed through an SSH tunnel.
- Origin checks: 115 passed. Cache checks: 183 passed. Queue tests passed, including 500 release/cancellation races.
- SIGKILL of the launcher triggered automatic systemd recovery after approximately five seconds. The old inference child was removed. Database row count and integrity survived. Inference passed again after recovery and an ordinary service restart.
- `autojev.service` is enabled, uses `Restart=always` and `KillMode=control-group`. User lingering is enabled, allowing startup without an interactive login.
- Daily private SQLite backup timer is enabled and an integrity-checked backup completed successfully.
- The source Windows instance was stopped and its sign-in startup disabled to avoid writing to two separate databases after migration.
- The service binds only to loopback and does not need external network services after installation. Clients need an SSH tunnel or a separately secured proxy. Failed or disconnected requests must be retried; they are not a durable queue.

A full device reboot was not tested because login1 requires administrator authentication. Boot startup configuration was verified, but an administrator should perform a reboot and check `systemctl --user status autojev` afterwards.

Weights, databases, private reports, runtime executables and transfer archives are not in Git. Private reports were copied separately. Temporary migration files and build scratch files were removed after testing.
