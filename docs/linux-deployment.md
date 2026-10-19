# Linux deployment

The Linux build runs without a tray. The Windows launcher is unchanged.

## Build on NVIDIA DGX Spark

Install the .NET 8 SDK for Linux ARM64 into `~/.dotnet`, CMake, a C++ compiler, and CUDA 13. CUDA must support architecture 121 for GB10. Then run:

```sh
bash scripts/install-media-linux.sh
bash scripts/build-linux.sh
bash scripts/install-linux-service.sh
```

The build pins jev.cpp to `aa885f0a86efe36079d70b522f927cd2fc38683b` and applies `patches/autojev-backend.patch`, exported from the working Windows backend. The older incremental patches are retained for reference. Python 3, ffmpeg and ffprobe are needed for image conversion. They must be on the service PATH or installed under `runtime/media/`.

Copy weights separately into `models/autojev-27b/`. Copy the SQLite database using SQLite's backup API, not a raw copy while its source is running. Keep private data out of Git. App keys are stored as hashes in the database. Existing client key strings remain valid; the internal dashboard key and process control tokens regenerate on startup.

## Direct LAN access

The Spark installation supports direct access at `http://192.168.0.105:8765/`, without an SSH tunnel. Dashboard username is `admin`. The generated password is stored privately in `.local/lan-dashboard-password.txt` on the server and the source computer. Passwords are not committed to Git.

The service reads optional `.local/server.env`. To enable this on another installation, set `AUTOJEV_HOST=0.0.0.0` and `AUTOJEV_DASHBOARD_PASSWORD_FILE=/absolute/path/to/private-password-file` there, then restart the service. Use a random password of at least 16 characters and owner-only file permissions. Binding all interfaces lets the service start before Wi-Fi connects and continue after network-address changes. The URL may change if DHCP assigns a new IP; reserve the device address in your router.

Dashboard and management routes require browser Basic authentication from private-network addresses. App inference requests retain their existing API keys and do not need the dashboard password. Cross-origin administration remains blocked. HTTP does not encrypt credentials or traffic; use this only on a trusted LAN, and do not forward the port to the internet. Use a TLS reverse proxy for untrusted networks.

## Optional SSH access

Without the optional LAN configuration, the service listens only on loopback. From your computer:

```sh
ssh -N -L 8765:127.0.0.1:8765 oira666@spark-3ecb.local
```

Open `http://localhost:8765` for the playground and `http://localhost:8765/usage` for keys and usage. If a local instance already uses 8765, use `-L 18765:127.0.0.1:8765` and open port 18765 instead. Use SSH keepalives such as `-o ServerAliveInterval=30 -o ServerAliveCountMax=3`, and reconnect the tunnel after a network outage. The server itself does not depend on the tunnel or GitHub after installation.

Do not publish the dashboard directly to the internet. Without a configured dashboard password, non-loopback clients can access only authenticated inference and minimal metadata.

## Operations

```sh
systemctl --user status autojev
systemctl --user restart autojev
journalctl --user -u autojev -n 100
curl --fail http://127.0.0.1:8765/health
```

`loginctl show-user "$USER" -p Linger` must show `yes` for startup before login. The installer checks this. Systemd restarts the service after crashes and kills all inference children when stopping it. SQLite uses WAL with FULL synchronous durability. In-flight requests do not survive disconnects, process crashes, or reboot; clients must retry. Keys and recorded usage persist. Hardware failure and disk loss require separate off-device backups.

Launcher/inference logs are in `logs/`. The app closes and rotates its own log files at 20 MiB, retaining seven older files per log. Do not configure external copytruncate rotation. The installed daily maintenance timer creates integrity-checked SQLite backups under `backups/`, retaining seven daily snapshots. Check `systemctl --user status autojev-maintenance.timer`. These backups are on the same disk, so keep a separate off-device backup too.

After pulling updates, stop the service before publishing into `app/`, run the build script, then restart. Do not overwrite `data/`, `state/` or model files during code updates.
