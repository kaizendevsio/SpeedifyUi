# Debug Mode - Non-Obvious Issues

## Hidden Failure Points
- NetworkMonitorService silently fails on Windows - check `OperatingSystem.IsLinux()` first
- `ip link` commands require root permissions but error is not obvious
- Chart initialization failures often due to DOM not ready, not actual JS errors

## Process Debugging
- Speedify CLI streaming commands don't terminate naturally - use cancellation token
- Process cleanup may hang if not using `Kill(true)` with timeout
- `speedify_cli` stderr is captured separately and logged after process exits

## Silent Data Issues
- Stats with `Connected = false` stop the entire streaming loop (Home.razor:382)
- Chart data batching happens every 1.1 seconds, not real-time
- Missing adapters in `WhitelistedLinks` fail silently - check logs