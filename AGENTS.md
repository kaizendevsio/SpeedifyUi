# AGENTS.md

This file provides guidance to agents when working with code in this repository.

## Non-Obvious Implementation Patterns

### Speedify CLI Integration
- `SpeedifyService.GetSettingsAsync()` is NOT implemented - throws `AbandonedMutexException` (line 218)
- Streaming commands buffer JSON until complete `[]` array detected, with 32KB safeguard
- Process cleanup requires `Kill(true)` with 2-second timeout for streaming commands

### Platform-Specific Gotchas
- NetworkMonitorService only works on Linux (checks `OperatingSystem.IsLinux()`)
- Uses `/bin/bash -c` for executing `ip link` commands, not direct process execution
- Adapter restart via `ip link set down/up` requires root permissions (undocumented)

### Blazor Chart Initialization
- Charts MUST use two-step initialization: first render loads JS module, second render initializes charts
- `_readyForChartInitializationStep` flag coordinates DOM readiness (Statistics.razor:216)
- Chart disposal can throw `JSDisconnectedException` - must handle silently

### Real-time Data Patterns
- Stats streaming filters out "speedify" aggregate and "%proxy" connections (SpeedifyService:267-269)
- Multiple timers: auto-refresh (3s), chart updates (1.1s), stats streaming (continuous)
- Components use `InvokeAsync(StateHasChanged)` even in synchronous contexts for thread safety

### Configuration Specifics
- `WhitelistedLinks` in appsettings.json uses adapter IDs like "enxc8a3627e629b", not display names
- Port 8080 hardcoded in appsettings.json under `Kestrel.Endpoints.MyHttpEndpoint`