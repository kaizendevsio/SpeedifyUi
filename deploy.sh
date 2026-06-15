#!/bin/bash
set -euo pipefail

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_NAME="xnetwork.service"
PUBLISH_DIR="$APP_DIR/XNetwork/bin/Release/net9.0/publish"
PRESERVE_DIR=""

cd "$APP_DIR"

echo "Pulling latest changes..."
git pull

PRESERVE_DIR="$(mktemp -d)"
if [[ -f "$PUBLISH_DIR/appsettings.json" ]]; then
  cp "$PUBLISH_DIR/appsettings.json" "$PRESERVE_DIR/appsettings.json"
fi
if [[ -f "$PUBLISH_DIR/auto-server-switch-state.json" ]]; then
  cp "$PUBLISH_DIR/auto-server-switch-state.json" "$PRESERVE_DIR/auto-server-switch-state.json"
fi

echo "Cleaning previous publish output..."
mkdir -p "$PUBLISH_DIR"
shopt -s dotglob nullglob
rm -rf "$PUBLISH_DIR"/*
shopt -u dotglob nullglob

echo "Publishing XNetwork..."
dotnet publish XNetwork/XNetwork.csproj -c Release

DIAGNOSTICS_DIR="/var/lib/xnetwork/diagnostics"
echo "Ensuring diagnostics directory..."
if install -d -m 0755 "$DIAGNOSTICS_DIR" 2>/dev/null; then
  chown "$(id -u):$(id -g)" "$DIAGNOSTICS_DIR" 2>/dev/null || true
elif command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
  sudo install -d -o "$(id -un)" -g "$(id -gn)" -m 0755 "$DIAGNOSTICS_DIR"
else
  echo "Warning: unable to create $DIAGNOSTICS_DIR; the app will use its local artifact fallback if needed." >&2
fi

APP_VERSION="$(sed -nE 's/.*CurrentVersion = "([^"]+)".*/\1/p' XNetwork/Models/AppChangelog.cs | head -n 1)"
if [[ -z "$APP_VERSION" ]]; then
  echo "Unable to determine AppChangelog.CurrentVersion" >&2
  exit 1
fi

GIT_COMMIT="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
GIT_BRANCH="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)"
BUILD_TIME_UTC="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
cat > "$PUBLISH_DIR/build-info.json" <<EOF
{
  "version": "$APP_VERSION",
  "commit": "$GIT_COMMIT",
  "branch": "$GIT_BRANCH",
  "builtAtUtc": "$BUILD_TIME_UTC"
}
EOF

if [[ -f "$PRESERVE_DIR/appsettings.json" ]]; then
  cp "$PRESERVE_DIR/appsettings.json" "$PUBLISH_DIR/appsettings.json"
fi
if [[ -f "$PRESERVE_DIR/auto-server-switch-state.json" ]]; then
  cp "$PRESERVE_DIR/auto-server-switch-state.json" "$PUBLISH_DIR/auto-server-switch-state.json"
fi
rm -rf "$PRESERVE_DIR"

echo "Restarting $SERVICE_NAME..."
MAIN_PID="$(systemctl show -p MainPID --value "$SERVICE_NAME")"
if [[ -n "$MAIN_PID" && "$MAIN_PID" != "0" ]]; then
  kill -KILL "$MAIN_PID"
else
  echo "$SERVICE_NAME has no active MainPID; attempting to start without sudo may fail if it is stopped."
  systemctl start "$SERVICE_NAME"
fi

sleep 2
systemctl status "$SERVICE_NAME" --no-pager

echo "Deployment complete."
