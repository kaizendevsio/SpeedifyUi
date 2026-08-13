# Router Wi-Fi Disable Switch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each Raspberry Pi Wi-Fi adapter a switch that stops it joining any network, enforced against reboots, manual `nmcli` use, and NetworkManager roaming.

**Architecture:** A new `WifiControlService` (singleton + hosted) persists a per-interface disabled flag, applies it with `nmcli device set <iface> autoconnect no` + `nmcli device disconnect <iface>`, and re-asserts it on a 30s enforcement pass. `WifiService.ConnectAsync` and `NetworkMonitorService.RestartLink` consult it so neither fights the setting.

**Tech Stack:** .NET 9, Blazor Server, `nmcli` via `Process`, xUnit, Tailwind CSS.

**Dependency direction:** `WifiService` → `WifiControlService`; `NetworkMonitorService` → `WifiControlService`. `WifiControlService` depends on neither — it parses device status with the existing static `WifiService.ParseWifiInterfaces`.

**Spec:** `docs/superpowers/specs/2026-08-13-router-wifi-disable-switch-design.md`

---

## File Structure

**Create:**
- `XNetwork/Models/WifiControlSettings.cs` — persisted per-interface disabled flags + enforcement interval.
- `XNetwork/Models/WifiControlStatus.cs` — per-adapter enforcement state for the UI.
- `XNetwork/Services/WifiControlSettingsStore.cs` — JSON persistence.
- `XNetwork/Services/WifiControlService.cs` — apply + enforce + pure decision statics.
- `XNetwork.Tests/WifiControlServiceTests.cs`
- `XNetwork.Tests/WifiControlSettingsStoreTests.cs`

**Modify:**
- `XNetwork/Services/WifiService.cs` — refuse `ConnectAsync` for a disabled adapter.
- `XNetwork/Services/NetworkMonitorService.cs` — skip restarting a disabled Wi-Fi interface.
- `XNetwork/Components/Pages/Settings.razor` — per-adapter toggles in Router Wi-Fi.
- `XNetwork/Program.cs` — DI registration.
- `XNetwork/appsettings.json` — `WifiControl` section.
- `XNetwork/Models/AppChangelog.cs`, `XNetwork.Tests/AppChangelogTests.cs`, `XNetwork.Tests/BuildInfoTests.cs`, `AGENTS.md` — release.

---

### Task 1: Models

**Files:** Create `XNetwork/Models/WifiControlSettings.cs`, `XNetwork/Models/WifiControlStatus.cs`

- [ ] **Step 1: `WifiControlSettings`**

```csharp
namespace XNetwork.Models;

/// <summary>Which Wi-Fi adapters are blocked from connecting, and how often that is re-asserted.</summary>
public sealed class WifiControlSettings
{
    /// <summary>Interface name -> disabled. A missing interface means enabled.</summary>
    public Dictionary<string, bool> DisabledInterfaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int EnforcementIntervalSeconds { get; set; } = 30;

    public TimeSpan EnforcementInterval => TimeSpan.FromSeconds(Math.Clamp(EnforcementIntervalSeconds, 10, 3600));

    public bool IsDisabled(string interfaceName) =>
        !string.IsNullOrWhiteSpace(interfaceName) &&
        DisabledInterfaces.TryGetValue(interfaceName, out var disabled) &&
        disabled;
}
```

- [ ] **Step 2: `WifiControlStatus`**

```csharp
namespace XNetwork.Models;

public sealed class WifiControlStatus
{
    public bool IsSupported { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<WifiControlInterfaceStatus> Interfaces { get; init; } = Array.Empty<WifiControlInterfaceStatus>();
}

public sealed class WifiControlInterfaceStatus
{
    public string InterfaceName { get; init; } = "";

    public bool IsDisabled { get; init; }

    public string DeviceState { get; init; } = "unknown";

    public DateTimeOffset? LastAppliedUtc { get; init; }

    public string? LastError { get; init; }

    public int ReassertCount { get; init; }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build SpeedifyUi.sln -c Release`
Expected: Build succeeded. (Use Release: a local Debug instance may hold the output.)

- [ ] **Step 4: Commit**

```bash
git add XNetwork/Models/WifiControlSettings.cs XNetwork/Models/WifiControlStatus.cs
git commit -m "feat: add wifi control models"
```

---

### Task 2: Pure decision logic (TDD)

**Files:** Create `XNetwork/Services/WifiControlService.cs` (statics only for now), `XNetwork.Tests/WifiControlServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using XNetwork.Services;

namespace XNetwork.Tests;

public class WifiControlServiceTests
{
    [Theory]
    [InlineData("connected", WifiEnforcementAction.Disconnect)]
    [InlineData("connecting", WifiEnforcementAction.Disconnect)]
    [InlineData("connecting (getting IP configuration)", WifiEnforcementAction.Disconnect)]
    [InlineData("disconnected", WifiEnforcementAction.None)]
    [InlineData("unavailable", WifiEnforcementAction.None)]
    [InlineData("unmanaged", WifiEnforcementAction.None)]
    [InlineData("", WifiEnforcementAction.None)]
    public void EvaluateDisabledAdapter(string deviceState, WifiEnforcementAction expected)
    {
        Assert.Equal(expected, WifiControlService.Evaluate(disabled: true, deviceState));
    }

    [Theory]
    [InlineData("connected")]
    [InlineData("disconnected")]
    [InlineData("unavailable")]
    public void EvaluateEnabledAdapterNeverActs(string deviceState)
    {
        Assert.Equal(WifiEnforcementAction.None, WifiControlService.Evaluate(disabled: false, deviceState));
    }

    [Fact]
    public void BuildsAutoconnectArguments()
    {
        Assert.Equal(["device", "set", "wlan0", "autoconnect", "no"], WifiControlService.BuildAutoconnectArgs("wlan0", false));
        Assert.Equal(["device", "set", "wlan0", "autoconnect", "yes"], WifiControlService.BuildAutoconnectArgs("wlan0", true));
    }

    [Fact]
    public void BuildsDisconnectArguments()
    {
        Assert.Equal(["device", "disconnect", "wlan0"], WifiControlService.BuildDisconnectArgs("wlan0"));
    }

    [Theory]
    [InlineData(4, "Error: not authorized to control networking.", true)]
    [InlineData(4, "Insufficient privileges", true)]
    [InlineData(4, "Access denied: permission denied", true)]
    [InlineData(0, "", false)]
    [InlineData(10, "Error: Device 'wlan0' not found.", false)]
    public void DecidesWhenToRetryWithSudo(int exitCode, string error, bool expected)
    {
        Assert.Equal(expected, WifiControlService.ShouldRetryWithSudo(exitCode, error));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName~WifiControlServiceTests"`
Expected: build error — `WifiControlService` / `WifiEnforcementAction` do not exist.

- [ ] **Step 3: Implement the statics and enum**

In `XNetwork/Services/WifiControlService.cs`:

```csharp
namespace XNetwork.Services;

public enum WifiEnforcementAction
{
    None,
    Disconnect
}

public sealed partial class WifiControlService
{
    private static readonly string[] SudoRetryMarkers =
    [
        "not authorized", "insufficient privileges", "permission denied", "access denied"
    ];

    /// <summary>Decides whether a disabled adapter needs the disable steps re-applied.</summary>
    public static WifiEnforcementAction Evaluate(bool disabled, string? deviceState)
    {
        if (!disabled || string.IsNullOrWhiteSpace(deviceState))
        {
            return WifiEnforcementAction.None;
        }

        var state = deviceState.Trim();
        return state.StartsWith("connected", StringComparison.OrdinalIgnoreCase) ||
               state.StartsWith("connecting", StringComparison.OrdinalIgnoreCase)
            ? WifiEnforcementAction.Disconnect
            : WifiEnforcementAction.None;
    }

    public static IReadOnlyList<string> BuildAutoconnectArgs(string interfaceName, bool allowAutoconnect) =>
        ["device", "set", interfaceName, "autoconnect", allowAutoconnect ? "yes" : "no"];

    public static IReadOnlyList<string> BuildDisconnectArgs(string interfaceName) =>
        ["device", "disconnect", interfaceName];

    public static bool ShouldRetryWithSudo(int exitCode, string? error)
    {
        if (exitCode == 0 || string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return SudoRetryMarkers.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName~WifiControlServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/WifiControlService.cs XNetwork.Tests/WifiControlServiceTests.cs
git commit -m "feat: add wifi enforcement decision logic"
```

---

### Task 3: Settings store (TDD)

**Files:** Create `XNetwork/Services/WifiControlSettingsStore.cs`, `XNetwork.Tests/WifiControlSettingsStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class WifiControlSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ulink-wifi-{Guid.NewGuid():N}");

    [Fact]
    public async Task RoundTripsDisabledInterfaces()
    {
        var path = Path.Combine(_directory, "wifi-control-settings.json");
        var store = new WifiControlSettingsStore(NullLogger<WifiControlSettingsStore>.Instance, path);
        var saved = new WifiControlSettings();
        saved.DisabledInterfaces["wlan0"] = true;
        saved.DisabledInterfaces["wlan1"] = false;

        await store.SaveAsync(saved);

        var loaded = new WifiControlSettings();
        store.Load(loaded);

        Assert.True(loaded.IsDisabled("wlan0"));
        Assert.True(loaded.IsDisabled("WLAN0"));
        Assert.False(loaded.IsDisabled("wlan1"));
        Assert.False(loaded.IsDisabled("wlan9"));
    }

    [Fact]
    public void LoadWithoutFileLeavesSettingsUntouched()
    {
        var store = new WifiControlSettingsStore(
            NullLogger<WifiControlSettingsStore>.Instance,
            Path.Combine(_directory, "missing.json"));
        var settings = new WifiControlSettings();

        store.Load(settings);

        Assert.Empty(settings.DisabledInterfaces);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName~WifiControlSettingsStoreTests"`
Expected: build error — `WifiControlSettingsStore` does not exist.

- [ ] **Step 3: Implement the store**

Copy `XNetwork/Services/AdapterIdentitySettingsStore.cs` structure exactly: same `GetAppDataDirectory`,
`RestrictOwnerAccess`, `_saveLock`, temp-file-then-move write. File name `wifi-control-settings.json`.
`Load` assigns `DisabledInterfaces` (rebuilt with `StringComparer.OrdinalIgnoreCase`) and
`EnforcementIntervalSeconds` when present; `SaveAsync` serializes the whole `WifiControlSettings`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName~WifiControlSettingsStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/WifiControlSettingsStore.cs XNetwork.Tests/WifiControlSettingsStoreTests.cs
git commit -m "feat: persist wifi control settings"
```

---

### Task 4: Apply and enforce (TDD)

**Files:** Modify `XNetwork/Services/WifiControlService.cs`, `XNetwork.Tests/WifiControlServiceTests.cs`

- [ ] **Step 1: Add the failing tests**

Append to `WifiControlServiceTests`:

```csharp
    [Fact]
    public async Task SetDisabledAppliesAutoconnectThenDisconnect()
    {
        var calls = new List<string>();
        var service = CreateService(new WifiControlSettings(), (file, args, _) =>
        {
            calls.Add($"{file} {string.Join(' ', args)}");
            return Task.FromResult((0, "", ""));
        });

        await service.SetDisabledAsync("wlan0", true, CancellationToken.None);

        Assert.Equal(
        [
            "nmcli device set wlan0 autoconnect no",
            "nmcli device disconnect wlan0"
        ], calls);
        Assert.True(service.IsDisabled("wlan0"));
    }

    [Fact]
    public async Task SetEnabledRestoresAutoconnectOnly()
    {
        var calls = new List<string>();
        var settings = new WifiControlSettings();
        settings.DisabledInterfaces["wlan0"] = true;
        var service = CreateService(settings, (file, args, _) =>
        {
            calls.Add($"{file} {string.Join(' ', args)}");
            return Task.FromResult((0, "", ""));
        });

        await service.SetDisabledAsync("wlan0", false, CancellationToken.None);

        Assert.Equal(["nmcli device set wlan0 autoconnect yes"], calls);
        Assert.False(service.IsDisabled("wlan0"));
    }

    [Fact]
    public async Task EnforceReappliesWhenDisabledAdapterIsConnected()
    {
        var settings = new WifiControlSettings();
        settings.DisabledInterfaces["wlan0"] = true;
        var calls = new List<string>();
        var service = CreateService(settings, (file, args, _) =>
        {
            var joined = $"{file} {string.Join(' ', args)}";
            calls.Add(joined);
            return Task.FromResult(joined.Contains("device status")
                ? (0, "wlan0:wifi:connected:XNetwork Wi-Fi Asia\n", "")
                : (0, "", ""));
        });

        await service.EnforceAsync(CancellationToken.None);

        Assert.Contains("nmcli device set wlan0 autoconnect no", calls);
        Assert.Contains("nmcli device disconnect wlan0", calls);
        Assert.Equal(1, service.GetStatus().Interfaces.Single(item => item.InterfaceName == "wlan0").ReassertCount);
    }

    [Fact]
    public async Task EnforceDoesNothingWhenDisabledAdapterIsAlreadyDisconnected()
    {
        var settings = new WifiControlSettings();
        settings.DisabledInterfaces["wlan0"] = true;
        var calls = new List<string>();
        var service = CreateService(settings, (file, args, _) =>
        {
            var joined = $"{file} {string.Join(' ', args)}";
            calls.Add(joined);
            return Task.FromResult(joined.Contains("device status")
                ? (0, "wlan0:wifi:disconnected:\n", "")
                : (0, "", ""));
        });

        await service.EnforceAsync(CancellationToken.None);

        Assert.DoesNotContain(calls, call => call.Contains("disconnect"));
        Assert.Equal(0, service.GetStatus().Interfaces.Single(item => item.InterfaceName == "wlan0").ReassertCount);
    }

    [Fact]
    public async Task RecordsLastErrorWhenApplyFails()
    {
        var service = CreateService(new WifiControlSettings(), (_, args, _) =>
            Task.FromResult(args.Contains("set") ? (4, "", "Error: Device 'wlan0' not found.") : (0, "", "")));

        await service.SetDisabledAsync("wlan0", true, CancellationToken.None);

        var status = service.GetStatus().Interfaces.Single(item => item.InterfaceName == "wlan0");
        Assert.Contains("not found", status.LastError);
    }

    [Fact]
    public async Task RetriesWithSudoOnAuthorizationFailure()
    {
        var calls = new List<string>();
        var service = CreateService(new WifiControlSettings(), (file, args, _) =>
        {
            calls.Add($"{file} {string.Join(' ', args)}");
            return Task.FromResult(file == "nmcli"
                ? (4, "", "Error: not authorized to control networking.")
                : (0, "", ""));
        });

        await service.SetDisabledAsync("wlan0", true, CancellationToken.None);

        Assert.Contains("sudo -n nmcli device set wlan0 autoconnect no", calls);
    }

    private static WifiControlService CreateService(
        WifiControlSettings settings,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output, string Error)>> runner) =>
        new(NullLogger<WifiControlService>.Instance, settings, null, TimeProvider.System, runner);
```

Add `using Microsoft.Extensions.Logging.Abstractions;` and `using XNetwork.Models;` to the test file.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName~WifiControlServiceTests"`
Expected: build errors for the missing instance members.

- [ ] **Step 3: Implement the service body**

Make `WifiControlService` a `BackgroundService`. Constructor:

```csharp
public WifiControlService(
    ILogger<WifiControlService> logger,
    WifiControlSettings settings,
    WifiControlSettingsStore? store,
    TimeProvider? timeProvider,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output, string Error)>>? runner)
```

with a DI-friendly `(logger, settings, store)` overload delegating with `null, null`. The default runner
executes `ProcessStartInfo` with `RedirectStandardOutput`/`RedirectStandardError`,
`UseShellExecute = false`, `CreateNoWindow = true`, mirroring `InterfaceMetadataService.RunProcessAsync`.
`store` is null in tests, so guard every save with `store is not null`.

Members:

- `bool IsDisabled(string interfaceName)` → `settings.IsDisabled(interfaceName)`.
- `WifiControlStatus GetStatus()` → `IsSupported = OperatingSystem.IsLinux()`, plus one
  `WifiControlInterfaceStatus` per interface in a `ConcurrentDictionary<string, InterfaceState>` merged
  with every key in `settings.DisabledInterfaces`.
- `Task SetDisabledAsync(string interfaceName, bool disabled, CancellationToken ct)`:
  set `settings.DisabledInterfaces[interfaceName] = disabled`, persist via `store`, then
  `ApplyAsync(interfaceName, disabled, ct)`.
- `Task ApplyAsync(...)`: run `BuildAutoconnectArgs(iface, allowAutoconnect: !disabled)`; when disabling,
  then run `BuildDisconnectArgs(iface)`. Record `LastAppliedUtc` and clear or set `LastError`.
- `Task EnforceAsync(CancellationToken ct)`: skip when not Linux or no interface is disabled. Read
  `["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"]`, parse with
  `WifiService.ParseWifiInterfaces`, and for each disabled interface call `Evaluate`. On `Disconnect`,
  log a warning with the observed state, increment `ReassertCount`, and call `ApplyAsync(..., disabled: true, ...)`.
- `RunAsync` helper: run with `nmcli`; if `ShouldRetryWithSudo`, retry once with `sudo` and args
  `["-n", "nmcli", ...]`. The test asserts the recorded call string is
  `"sudo -n nmcli device set wlan0 autoconnect no"`, so pass file `"sudo"` and prepend `"-n"`, `"nmcli"`.
- `ExecuteAsync`: `await EnforceAsync` immediately, then loop `Task.Delay(settings.EnforcementInterval)`
  and enforce, swallowing `OperationCanceledException` and logging other exceptions at Debug.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName~WifiControlServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/WifiControlService.cs XNetwork.Tests/WifiControlServiceTests.cs
git commit -m "feat: apply and enforce per-adapter wifi disable"
```

---

### Task 5: Guards in WifiService and NetworkMonitorService (TDD)

**Files:** Modify `XNetwork/Services/WifiService.cs`, `XNetwork/Services/NetworkMonitorService.cs`, `XNetwork.Tests/WifiControlServiceTests.cs`

- [ ] **Step 1: Add the failing test**

```csharp
    [Fact]
    public async Task ConnectIsRefusedWhileTheAdapterIsDisabled()
    {
        var settings = new WifiControlSettings();
        settings.DisabledInterfaces["wlan0"] = true;
        var control = CreateService(settings, (_, _, _) => Task.FromResult((0, "", "")));
        var wifi = new WifiService(NullLogger<WifiService>.Instance, control);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wifi.ConnectAsync("wlan0", "Some SSID", "password", CancellationToken.None));

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName~ConnectIsRefused"`
Expected: build error — `WifiService` has no such constructor.

- [ ] **Step 3: Add the guards**

`WifiService`: change the primary constructor to
`WifiService(ILogger<WifiService> logger, WifiControlService? wifiControlService = null)` so existing
construction keeps working. At the top of `ConnectAsync`, after the SSID validation and interface
defaulting:

```csharp
if (wifiControlService?.IsDisabled(interfaceName) == true)
{
    throw new InvalidOperationException(
        $"Wi-Fi is disabled for {interfaceName} in Settings. Enable it under Settings > Router Wi-Fi first.");
}
```

Note the guard must sit *after* `interfaceName` is defaulted to `wlan0`, so a blank interface still
matches a disabled `wlan0`.

`NetworkMonitorService`: add a trailing optional constructor parameter
`WifiControlService? wifiControlService = null`, store it, and at the top of `RestartLink`:

```csharp
if (_wifiControlService?.IsDisabled(interfaceName) == true)
{
    _logger.LogInformation(
        "Skipping restart of {Link} because Wi-Fi is disabled for it in Settings", interfaceName);
    return;
}
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName!~BrowserSmokeTests"`
Expected: all pass, including the pre-existing `NetworkMonitorService` construction in
`XBondStatsServiceNamingTests` (which relies on the new parameter being optional).

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/WifiService.cs XNetwork/Services/NetworkMonitorService.cs XNetwork.Tests/WifiControlServiceTests.cs
git commit -m "feat: stop wifi connect and link restart fighting the disable switch"
```

---

### Task 6: DI and configuration

**Files:** Modify `XNetwork/Program.cs`, `XNetwork/appsettings.json`

- [ ] **Step 1: Register the service**

Immediately before `builder.Services.AddSingleton<WifiService>();`:

```csharp
builder.Services.AddSingleton<WifiControlSettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("WifiControl").Get<WifiControlSettings>() ?? new WifiControlSettings();
    sp.GetRequiredService<WifiControlSettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<WifiControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WifiControlService>());
```

`WifiService` and `NetworkMonitorService` pick the dependency up automatically because their new
parameters are optional and registered.

- [ ] **Step 2: Add the `WifiControl` section to `appsettings.json`**

Sibling of `AdapterIdentity`:

```json
"WifiControl": {
  "DisabledInterfaces": {},
  "EnforcementIntervalSeconds": 30
},
```

- [ ] **Step 3: Build**

Run: `dotnet build SpeedifyUi.sln -c Release`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add XNetwork/Program.cs XNetwork/appsettings.json
git commit -m "feat: register wifi control service"
```

---

### Task 7: Settings UI

**Files:** Modify `XNetwork/Components/Pages/Settings.razor`

- [ ] **Step 1: Inject and add state**

Add `@inject WifiControlService WifiControlService`. Add fields:

```csharp
private WifiControlStatus? _wifiControlStatus;
private string? _wifiControlError;
private bool _wifiControlBusy;
```

Refresh `_wifiControlStatus = WifiControlService.GetStatus();` wherever `_wifiStatus` is refreshed
(`RefreshWifiStatusAsync` and the initial load).

- [ ] **Step 2: Add the per-adapter list**

Inside the Router Wi-Fi accordion, directly after the closing `</div>` of the status card near
`XNetwork/Components/Pages/Settings.razor:244`, insert:

```razor
<div class="overflow-hidden rounded-lg border border-slate-800 bg-slate-950/50">
    <div class="border-b border-slate-800 px-4 py-3">
        <h4 class="text-sm font-semibold text-white">Adapter Wi-Fi access</h4>
        <p class="text-xs text-slate-500">
            Turn an adapter off to stop the router joining any Wi-Fi network with it. Useful when a wired uplink
            already reaches the same network.
        </p>
    </div>
    @if (_wifiInterfaces.Count == 0)
    {
        <div class="p-4 text-sm text-slate-400">No Wi-Fi adapters were detected.</div>
    }
    else
    {
        @foreach (var adapter in _wifiInterfaces)
        {
            var control = GetWifiControlFor(adapter.InterfaceName);
            <div class="flex items-start justify-between gap-4 border-b border-slate-800/70 px-4 py-3 last:border-b-0">
                <div class="min-w-0">
                    <p class="text-sm font-semibold text-white">@adapter.DisplayName</p>
                    <p class="mt-1 text-xs @(control?.LastError is { Length: > 0 } ? "text-rose-200" : "text-slate-500")">
                        @WifiControlStateText(adapter.InterfaceName)
                    </p>
                </div>
                <label class="flex flex-shrink-0 items-center gap-2 text-xs text-slate-400">
                    <span>Allow</span>
                    <input type="checkbox"
                           class="h-5 w-5 accent-cyan-400"
                           disabled="@_wifiControlBusy"
                           checked="@(!WifiControlService.IsDisabled(adapter.InterfaceName))"
                           @onchange="args => ToggleWifiAdapterAsync(adapter.InterfaceName, args.Value is true)" />
                </label>
            </div>
        }
    }
</div>
```

- [ ] **Step 3: Add the handlers**

```csharp
private WifiControlInterfaceStatus? GetWifiControlFor(string interfaceName) =>
    _wifiControlStatus?.Interfaces.FirstOrDefault(item =>
        string.Equals(item.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase));

private string WifiControlStateText(string interfaceName)
{
    var control = GetWifiControlFor(interfaceName);
    if (control?.LastError is { Length: > 0 } error)
    {
        return error;
    }

    if (!WifiControlService.IsDisabled(interfaceName))
    {
        return "Connecting allowed";
    }

    return control?.LastAppliedUtc is null
        ? "Disabled"
        : $"Disabled - enforced {TelemetryFormatter.FormatLastUpdated(control.LastAppliedUtc)}";
}

private async Task ToggleWifiAdapterAsync(string interfaceName, bool allow)
{
    _wifiControlBusy = true;
    _wifiControlError = null;
    try
    {
        await WifiControlService.SetDisabledAsync(interfaceName, !allow, _refreshCts.Token);
        _wifiControlStatus = WifiControlService.GetStatus();
        _wifiMessage = allow
            ? $"Wi-Fi enabled for {interfaceName}."
            : $"Wi-Fi disabled for {interfaceName}. uLink will keep it disconnected.";
    }
    catch (Exception ex)
    {
        _wifiControlError = ex.Message;
        _wifiMessage = $"Could not change Wi-Fi for {interfaceName}: {ex.Message}";
    }
    finally
    {
        _wifiControlBusy = false;
        await RefreshWifiStatusAsync();
    }
}
```

Add `@using XNetwork.Utils` if the file does not already have it (needed for `TelemetryFormatter`).

- [ ] **Step 4: Disable Scan and Connect for a disabled adapter**

On the Scan button (`Settings.razor:290` area) and the Connect button (`Settings.razor:343` area),
extend the existing `disabled` expression with
`|| WifiControlService.IsDisabled(_wifiInterface)`.

- [ ] **Step 5: Build and run the suite**

Run: `dotnet build SpeedifyUi.sln -c Release`
Expected: Build succeeded.

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName!~BrowserSmokeTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add XNetwork/Components/Pages/Settings.razor
git commit -m "feat: add per-adapter wifi switch to settings"
```

---

### Task 8: Release and deploy

**Files:** Modify `XNetwork/Models/AppChangelog.cs`, `XNetwork.Tests/AppChangelogTests.cs`, `XNetwork.Tests/BuildInfoTests.cs`, `AGENTS.md`

- [ ] **Step 1: Bump the version**

Set `AppChangelog.CurrentVersion` to `ulink-2026.06.126`, add a matching top entry summarising the
per-adapter Wi-Fi switch and its enforcement, and update the two version-pinning assertions in
`AppChangelogTests` (`CurrentVersion_UsesDateBasedMonthlyRevision` and the entry-specific `Contains`
assertions) plus `BuildInfoTests` (`2026.06.126`).

- [ ] **Step 2: Journal**

Add a dated `AGENTS.md` bullet under `## XBond-Only Branch` recording: the new `WifiControlService`,
the `autoconnect no` + `disconnect` mechanism, the 30s enforcement pass, the reboot-window limitation,
the `WifiService.ConnectAsync` and `NetworkMonitorService.RestartLink` guards, and that saved wireless
profiles are intentionally left untouched.

- [ ] **Step 3: Verify and deploy**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj -c Release --filter "FullyQualifiedName!~BrowserSmokeTests"`
Expected: all pass.

```bash
git add -A XNetwork XNetwork.Tests AGENTS.md
git commit -m "docs: release ulink-2026.06.126"
git push origin feature/xband-only-runtime
```

```bash
ssh -i "C:\Users\Xeon\.ssh\speedifyui_cli_probe" xeon-network@xeon-network "cd /home/xeon-network/xnetwork; ./deploy.sh"
```

- [ ] **Step 4: Live verification**

Confirm the deployed `build-info.json` reports `ulink-2026.06.126`, `systemctl is-active xnetwork.service`
is `active`, and `/`, `/details`, `/xbond`, `/settings`, `/xrouter` return 200.

Then, with the operator's consent, toggle `wlan0` off in Settings > Router Wi-Fi and confirm on the router:

```bash
nmcli -t -f DEVICE,TYPE,STATE device status | grep wlan0
nmcli -t -f GENERAL.DEVICE,GENERAL.STATE,GENERAL.AUTOCONNECT device show wlan0
```

Expected: `disconnected`, `AUTOCONNECT: no`. Wait through one enforcement pass (35s) and re-check that
it is still disconnected, then restore the operator's preferred state.

- [ ] **Step 5: Record the outcome**

Add the verification result to `AGENTS.md`, commit, and push.
