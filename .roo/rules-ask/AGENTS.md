# Ask Mode - Non-Obvious Context

## Misleading Naming
- "XNetwork" project name vs "uLink" product and solution name - same thing
- "xbond" is the Rust tunnel workspace; "uLink" is the product as a whole
- Adapter "Name" is a technical ID, "Isp" is the display name
- `WhitelistedLinks` expects adapter Names (IDs), not ISP names

## Hidden Dependencies
- The router-side integrations are Linux-only: nftables, `ip` policy routing, systemd, NetworkManager, dnsmasq
- `xbond-client` must be running for the dashboard to show any tunnel state
- Chart.js and Tailwind are loaded via CDN, not bundled

## Configuration Gotchas
- Port 8080 comes from `Kestrel.Endpoints` in appsettings.json, not launchSettings.json
- `DownTimeoutSeconds` applies per adapter, not globally
- Tunnel behaviour is configured separately, in `/etc/xbond/client.toml` on the router
