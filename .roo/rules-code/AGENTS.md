# Code Mode - Non-Obvious Patterns

## Critical Implementation Details
- Files shared with another process must be published atomically (temp file plus rename); a plain write is visible half-finished to a reader
- Process cleanup requires `Kill(true)` not just `Kill()` to terminate the process tree
- Peer-unreachable errors are an expected operating condition on this router, not defects - classify them rather than logging stack traces on every poll

## Blazor Component Gotchas
- Chart initialization requires TWO render cycles: first loads the JS module, second initializes charts
- Always use `InvokeAsync(StateHasChanged)` even in sync code for thread safety
- Timer disposal in `DisposeAsync` can throw if the component already disconnected

## Data Processing Quirks
- Loss is a ratio, so its resolution is one over the samples in its window; a short window turns one dropped packet into a large percentage
- Latency and loss therefore use separate windows on purpose - do not collapse them back into one
- Throughput fields differ in units between sources; check whether a value is bits or bytes before converting
