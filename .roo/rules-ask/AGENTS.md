# Ask Mode - Non-Obvious Context

## Misleading Naming
- "XNetwork" project name vs "SpeedifyUi" solution name - same project
- Adapter "Name" is technical ID, "Isp" is display name
- `WhitelistedLinks` expects adapter Names (IDs), not ISP names

## Hidden Dependencies
- Requires Speedify CLI (`speedify_cli`) installed and in PATH
- Linux-only for NetworkMonitorService features
- Chart.js and Tailwind loaded via CDN, not bundled

## Configuration Gotchas
- Port 8080 hardcoded in appsettings.json, not in launchSettings.json
- `DownTimeoutSeconds` applies per adapter, not globally
- No actual tests despite XNetwork.csproj being a web project