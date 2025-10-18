# Architect Mode - Non-Obvious Architecture

## Architectural Constraints
- Blazor Server requires SignalR connection - all UI updates go through websocket
- No client-side state management - everything is server-side
- Chart.js runs client-side but data flows through Blazor's JS interop

## Hidden Coupling
- SpeedifyService is singleton but components dispose their own streaming tokens
- NetworkMonitorService directly calls SpeedifyService - circular monitoring possible
- Timer-based updates (3s) can conflict with streaming updates

## Performance Bottlenecks
- JSON buffering up to 32KB per streaming command
- Chart updates batched at 1.1s intervals to avoid JS interop overhead
- Multiple concurrent `speedify_cli` processes can exhaust system resources