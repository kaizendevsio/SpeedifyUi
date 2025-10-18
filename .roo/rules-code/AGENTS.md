# Code Mode - Non-Obvious Patterns

## Critical Implementation Details
- `SpeedifyService.GetSettingsAsync()` throws `AbandonedMutexException` - NOT a bug, intentionally unimplemented
- Streaming commands MUST buffer JSON until complete `[]` array detected - partial JSON will fail
- Process cleanup requires `Kill(true)` not just `Kill()` to terminate process tree

## Blazor Component Gotchas
- Chart initialization requires TWO render cycles: first loads JS module, second initializes charts
- Always use `InvokeAsync(StateHasChanged)` even in sync code for thread safety
- Timer disposal in `DisposeAsync` can throw if component already disconnected

## Data Processing Quirks
- Stats streaming explicitly filters "speedify" (aggregate) and "%proxy" connections - don't remove this filtering
- ConnectionItem stats use `ReceiveBps`/`SendBps` in bytes/sec, convert to Mbps for display