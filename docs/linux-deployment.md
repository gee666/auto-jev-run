# Linux deployment

The Linux build runs without a tray. The Windows launcher is unchanged.

## Build on NVIDIA DGX Spark

Install the .NET 8 SDK for Linux ARM64 into `~/.dotnet`, CMake, a C++ compiler, and CUDA 13. CUDA must support architecture 121 for GB10. Then run:

```sh
bash scripts/build-linux.sh
bash scripts/install-linux-service.sh
```

The build pins jev.cpp to `aa885f0a86efe36079d70b522f927cd2fc38683b` and applies `patches/autojev-backend.patch`, exported from the working Windows backend. The older incremental patches are retained for reference. Python 3, ffmpeg and ffprobe are needed for image conversion. They must be on the service PATH or installed under `runtime/media/`.

Copy weights separately into `models/autojev-27b/`. Copy the SQLite database using SQLite's backup API, not a raw copy while its source is running. Keep private data out of Git. App keys are stored as hashes in the database. Existing client key strings remain valid; the internal dashboard key and process control tokens regenerate on startup.

## Connect

The service listens only on loopback. From your computer:

```sh
ssh -N -L 8765:127.0.0.1:8765 oira666@spark-3ecb.local
```

Open `http://localhost:8765` for the playground and `http://localhost:8765/usage` for keys and usage. If a local instance already uses 8765, use `-L 18765:127.0.0.1:8765` and open port 18765 instead. Use SSH keepalives such as `-o ServerAliveInterval=30 -o ServerAliveCountMax=3`, and reconnect the tunnel after a network outage. The server itself does not depend on the tunnel or GitHub after installation.

Do not publish the dashboard directly to the internet. It intentionally permits local key administration. `AUTOJEV_HOST` can explicitly expose authenticated inference on a LAN, but dashboard/control routes still require loopback. HTTP is unencrypted; use SSH or a properly configured TLS proxy.

## Operations

```sh
systemctl --user status autojev
systemctl --user restart autojev
journalctl --user -u autojev -n 100
curl --fail http://127.0.0.1:8765/health
```

`loginctl show-user "$USER" -p Linger` must show `yes` for startup before login. The installer checks this. Systemd restarts the service after crashes and kills all inference children when stopping it. SQLite uses WAL with FULL synchronous durability. In-flight requests do not survive disconnects, process crashes, or reboot; clients must retry. Keys and recorded usage persist. Hardware failure and disk loss require separate off-device backups.

Launcher/inference logs are in `logs/`. Configure log rotation for long-running installations. Back up `data/usage.sqlite` with Python's sqlite3 backup API before upgrades. Keep backups private and outside Git.

After pulling updates, stop the service before publishing into `app/`, run the build script, then restart. Do not overwrite `data/`, `state/` or model files during code updates.
