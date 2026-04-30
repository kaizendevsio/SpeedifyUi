#!/bin/bash
set -euo pipefail

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_NAME="xnetwork.service"

cd "$APP_DIR"

echo "Pulling latest changes..."
git pull

echo "Publishing XNetwork..."
dotnet publish XNetwork/XNetwork.csproj -c Release

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
