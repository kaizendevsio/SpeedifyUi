# Architect Mode - Non-Obvious Architecture

## Architectural Constraints
- Blazor Server requires SignalR connection - all UI updates go through websocket
- No client-side state management - everything is server-side
- Chart.js runs client-side but data flows through Blazor's JS interop
- The dashboard does not carry traffic: `xbond-client` owns the tunnel, and the dashboard only reads its published state

## Hidden Coupling
- The dashboard and the tunnel communicate through `/run/xbond/client-status.json`, written by a separate process about once per second
- Per-path `rtt_ms` / `loss_rate` are not display-only: they feed path scoring, the stability penalty, and the redundancy policy's duplication threshold, so changing how they are measured changes routing
- Timer-based UI updates can conflict with streaming updates
- Policy routes installed for traffic bypass are deleted by the kernel whenever their interface goes down, so they need periodic reconciliation rather than one-shot setup

## Performance Bottlenecks
- Subprocess calls (`ip`, `nmcli`) reachable from the scheduler tick have caused real tunnel latency spikes; cache system state instead
- Chart updates batched to avoid JS interop overhead
- Tunnel throughput is bounded by per-packet crypto cost on the router CPU, not by link capacity alone
