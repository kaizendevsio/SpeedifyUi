# Debug Mode - Non-Obvious Issues

## Hidden Failure Points
- NetworkMonitorService silently fails on Windows - check `OperatingSystem.IsLinux()` first
- `ip link` commands require root permissions but the error is not obvious
- Chart initialization failures are often the DOM not being ready, not actual JS errors
- Something that works right after a deploy and stops later is usually kernel or system state tied to an interface that flapped, not the code that installed it

## Tunnel Debugging
- `/run/xbond/client-status.json` is the primary source of truth for path state
- `journalctl -u xbond-client` carries structured JSON events, including `scheduler-tick-slow` with a per-phase breakdown
- Separate what a metric *reports* from what the network is *doing*: bind a probe to the interface and compare, rather than trusting either alone

## Silent Data Issues
- Missing adapters in `WhitelistedLinks` fail silently - check logs
- A policy route table that has been emptied still leaves its firewall marking in place, so traffic is marked, finds no route, and silently falls through to the main table
