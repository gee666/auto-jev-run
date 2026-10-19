#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$ROOT"/{logs,state,data,tmp} "$HOME/.config/systemd/user"
chmod 700 "$ROOT/data" "$ROOT/state" "$ROOT/tmp"
DOTNET=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
[[ -x "$DOTNET" && -x "$ROOT/runtime/linux-cuda/llama-server" && -f "$ROOT/app/AutoJev.dll" ]]
cat > "$HOME/.config/systemd/user/autojev.service" <<EOF
[Unit]
Description=AutoJev inference and dashboard
StartLimitIntervalSec=0

[Service]
Type=simple
WorkingDirectory=$ROOT
ExecStart=$DOTNET $ROOT/app/AutoJev.dll serve --root $ROOT
Restart=always
RestartSec=5
TimeoutStopSec=45
KillMode=control-group
UMask=0077
Environment=AUTOJEV_HOST=127.0.0.1
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=PATH=$HOME/.dotnet:/usr/local/cuda/bin:/usr/bin:/bin
Environment=LD_LIBRARY_PATH=$ROOT/runtime/linux-cuda:/usr/local/cuda/lib64
NoNewPrivileges=true
LimitCORE=0

[Install]
WantedBy=default.target
EOF
cat > "$ROOT/config/logrotate-linux.conf" <<EOF
$ROOT/logs/*.log {
    daily
    maxsize 20M
    rotate 7
    compress
    missingok
    notifempty
    copytruncate
    su $USER $(id -gn)
}
EOF
cat > "$HOME/.config/systemd/user/autojev-maintenance.service" <<EOF
[Unit]
Description=AutoJev private database backup and log rotation
[Service]
Type=oneshot
WorkingDirectory=$ROOT
ExecStart=/usr/bin/python3 $ROOT/scripts/backup-linux.py
ExecStart=/usr/sbin/logrotate --state $ROOT/state/logrotate.state $ROOT/config/logrotate-linux.conf
UMask=0077
NoNewPrivileges=true
EOF
cat > "$HOME/.config/systemd/user/autojev-maintenance.timer" <<EOF
[Unit]
Description=Daily AutoJev maintenance
[Timer]
OnCalendar=daily
Persistent=true
RandomizedDelaySec=10m
[Install]
WantedBy=timers.target
EOF
systemctl --user daemon-reload
systemctl --user enable --now autojev.service autojev-maintenance.timer
if [[ $(loginctl show-user "$USER" -p Linger --value) != yes ]]; then
  echo 'WARNING: run loginctl enable-linger to start without an interactive login.' >&2
  exit 1
fi
systemctl --user status autojev.service --no-pager
