#!/bin/bash
set -euo pipefail

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_NAME="xnetwork.service"
PUBLISH_DIR="$APP_DIR/XNetwork/bin/Release/net9.0/publish"
CONFIG_BACKUP=""

cd "$APP_DIR"

echo "Pulling latest changes..."
git pull

if [[ -f "$PUBLISH_DIR/appsettings.json" ]]; then
  CONFIG_BACKUP="$(mktemp)"
  cp "$PUBLISH_DIR/appsettings.json" "$CONFIG_BACKUP"
fi

echo "Publishing XNetwork..."
dotnet publish XNetwork/XNetwork.csproj -c Release

if [[ -n "$CONFIG_BACKUP" && -f "$CONFIG_BACKUP" ]]; then
  cp "$CONFIG_BACKUP" "$PUBLISH_DIR/appsettings.json"
  rm -f "$CONFIG_BACKUP"
fi

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
