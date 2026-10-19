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
EnvironmentFile=-$ROOT/.local/server.env
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=PATH=$HOME/.dotnet:/usr/local/cuda/bin:/usr/bin:/bin
Environment=LD_LIBRARY_PATH=$ROOT/runtime/linux-cuda:/usr/local/cuda/lib64
NoNewPrivileges=true
LimitCORE=0

[Install]
WantedBy=default.target
EOF
cat > "$HOME/.config/systemd/user/autojev-maintenance.service" <<EOF
[Unit]
Description=AutoJev private database backup
[Service]
Type=oneshot
WorkingDirectory=$ROOT
ExecStart=/usr/bin/python3 $ROOT/scripts/backup-linux.py
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
