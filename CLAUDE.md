# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

X Network (XNetwork) is a Blazor Server web application that provides a web-based UI for managing and monitoring Speedify VPN network adapters. The application runs on port 8080 and offers real-time statistics, adapter management, and connection controls.

## Technology Stack

- **.NET 9.0** with Blazor Server
- **UI**: Tailwind CSS (CDN), Chart.js (CDN) for statistics visualization
- **Real-time updates**: SignalR (built into Blazor Server)
- **External dependency**: Speedify CLI (accessed via `speedify_cli` command)

## Commands

### Build and Run
```bash
# Build the project
dotnet build

# Run in development mode
dotnet run --project XNetwork

# Run from solution directory
dotnet run --project XNetwork/XNetwork.csproj

# Publish for deployment
dotnet publish -c Release
```

### Development
- Application runs on http://localhost:8080 (configured in appsettings.json)
- Hot reload is enabled by default in development mode
- No test infrastructure currently exists - add tests as needed

## Architecture

### Core Services

**SpeedifyService** (XNetwork/Services/SpeedifyService.cs)
- Interfaces with Speedify CLI via process execution
- Key operations: GetAdaptersAsync, GetStatsAsync (streaming), SetPriorityAsync, connection control methods
- Uses JSON serialization for CLI communication
- Throws SpeedifyException for CLI errors

**NetworkMonitorService** (XNetwork/Services/NetworkMonitorService.cs)  
- Background service that monitors adapter states (Linux only)
- Auto-restarts failed connections after configurable timeout
- Configuration in appsettings.json under "NetworkMonitor" section

### UI Components

- **Home.razor**: Main dashboard with adapter status and priority controls
- **Statistics.razor**: Real-time charts with auto-refresh (15-second intervals)
- **Controls.razor**: Administrative controls
- All components use server-side interactivity with `@rendermode InteractiveServer`

### Data Flow

1. SpeedifyService executes `speedify_cli` commands and parses JSON output
2. Blazor components call service methods via dependency injection
3. Statistics page uses streaming for real-time data updates
4. NetworkMonitorService runs in background, checking adapter states periodically

## Important Implementation Details

### Speedify CLI Integration
- All Speedify operations go through `ProcessStartInfo` with `speedify_cli` command
- JSON output is parsed using `System.Text.Json`
- Streaming statistics use `IAsyncEnumerable<ConnectionStatsPayload>`

### State Management
- Components handle disconnected states gracefully
- Auto-refresh timers dispose properly on component disposal
- Error boundaries prevent cascading failures

### Platform Considerations
- NetworkMonitorService uses Linux `ip link` commands - won't work on Windows
- Application is cross-platform but network monitoring is Linux-specific
- HTTPS redirection enabled for production

## Configuration

Key settings in appsettings.json:
- **Kestrel.Endpoints**: Port configuration (default 8080)
- **NetworkMonitor.Enabled**: Enable/disable background monitoring
- **NetworkMonitor.WhitelistedLinks**: Adapter IDs to monitor
- **NetworkMonitor.DownTimeoutSeconds**: Time before restarting adapter

## Project Structure

```
SpeedifyUi.sln
└── XNetwork/
    ├── Components/
    │   ├── Layout/         # Main layout with navigation
    │   ├── Pages/          # Home, Statistics, Controls, Error
    │   └── Custom/         # Reusable UI components
    ├── Services/           # SpeedifyService, NetworkMonitorService
    ├── Models/             # Adapter, ConnectionItem, etc.
    └── wwwroot/app.css     # Custom styles
```

## Common Tasks

### Adding New Speedify Commands
1. Add method to SpeedifyService following existing patterns
2. Use `ExecuteCommandAsync` for single responses, `StreamCommandAsync` for streaming
3. Define corresponding model classes in Models/
4. Handle SpeedifyException for error cases

### Modifying UI Components
1. Components use Tailwind CSS classes - maintain consistency
2. Mobile-first responsive design is critical
3. Use existing Custom components (ActionButton, StatePill, etc.) for consistency
4. Ensure proper disposal of timers and subscriptions

### Debugging Speedify Integration
- Check if `speedify_cli` is accessible in PATH
- Verify JSON output format matches model classes
- Use logging to capture raw CLI output when debugging