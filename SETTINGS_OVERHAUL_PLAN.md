# Speedify UI Settings Overhaul - Implementation Plan

## Document Information
- **Created**: 2025-12-05
- **Status**: Completed
- **Version**: 2.0
- **Last Updated**: 2025-12-05

---

## Table of Contents
1. [Overview](#1-overview)
2. [Architecture Changes](#2-architecture-changes)
3. [New Models](#3-new-models)
4. [SpeedifyService Method Additions](#4-speedifyservice-method-additions)
5. [UI Component Breakdown](#5-ui-component-breakdown)
6. [Settings Page Layout Structure](#6-settings-page-layout-structure)
7. [Implementation Phases](#7-implementation-phases)
8. [File Changes Summary](#8-file-changes-summary)
9. [Testing Considerations](#9-testing-considerations)

---

## 1. Overview

### 1.1 Purpose
This document outlines the implementation plan for a comprehensive settings overhaul of the Speedify UI application. The goal is to expose all Speedify CLI settings through an intuitive, mobile-first web interface.

### 1.2 Key Features to Implement
1. **Streaming Bypass Rules Management** - Full CRUD for domains, IPs, ports, and services using Speedify's built-in `streamingbypass` CLI commands
2. **Transport Mode Configuration** - Protocol selection and retry settings
3. **Complete Speedify CLI Settings Exposure** - All remaining settings from the CLI
4. **Network Adapter Priority Control** - Draggable list for adapter priority within Speedify

### 1.3 Design Principles
- Mobile-first responsive design
- Collapsible accordion sections for organization
- "Save Changes" pattern (batch changes, don't apply immediately)
- Visual feedback for pending vs applied changes
- Proper validation and error handling

---

## 2. Architecture Changes

### 2.1 Current Architecture
```
┌─────────────────────────────────────────────────────────────┐
│                    Settings.razor                           │
│  ┌─────────────────────────────────────────────────────┐   │
│  │  Direct state management with immediate CLI calls   │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                   SpeedifyService.cs                        │
│  ┌─────────────────────────────────────────────────────┐   │
│  │  Individual methods for each setting                │   │
│  │  (SetEncryptionAsync, SetHeaderCompressionAsync)    │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 Proposed Architecture
```
┌─────────────────────────────────────────────────────────────┐
│                    Settings.razor                           │
│  ┌─────────────────────────────────────────────────────┐   │
│  │  Orchestrates child components                      │   │
│  │  Manages global save/cancel state                   │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
         │              │              │              │
         ▼              ▼              ▼              ▼
┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│ConnectionSec │ │BypassSection │ │TransportSec  │ │AdapterSection│
│.razor        │ │.razor        │ │.razor        │ │.razor        │
└──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘
         │              │              │              │
         └──────────────┴──────────────┴──────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│               SettingsStateService.cs (NEW)                 │
│  ┌─────────────────────────────────────────────────────┐   │
│  │  Centralized settings state management              │   │
│  │  Tracks pending vs applied changes                  │   │
│  │  Batches changes for single save operation          │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                   SpeedifyService.cs                        │
│  ┌─────────────────────────────────────────────────────┐   │
│  │  Extended with new methods for all CLI commands     │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 2.3 Key Architectural Decisions

#### 2.3.1 Settings State Service
Create a new `SettingsStateService` to:
- Track original values loaded from Speedify
- Track pending changes made by user
- Provide diff between original and pending
- Handle batch save operations
- Emit events when changes are pending/saved

#### 2.3.2 Component-Based UI
Break Settings.razor into smaller, focused components:
- Each section is a self-contained component
- Components communicate via cascading parameters or events
- Parent orchestrates save/cancel operations

#### 2.3.3 Validation Layer
Add a validation service/utilities:
- Validate IP addresses (IPv4/IPv6)
- Validate domain names
- Validate port ranges and protocols
- Provide user-friendly error messages

---

## 3. New Models

### 3.1 Streaming Bypass Models

```csharp
// File: XNetwork/Models/StreamingBypassSettings.cs

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Represents streaming bypass configuration from "show streamingbypass"
/// </summary>
public class StreamingBypassSettings
{
    [JsonPropertyName("domainWatchlistEnabled")]
    public bool DomainWatchlistEnabled { get; set; }

    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = new();

    [JsonPropertyName("ipv4")]
    public List<string> IPv4Addresses { get; set; } = new();

    [JsonPropertyName("ipv6")]
    public List<string> IPv6Addresses { get; set; } = new();

    [JsonPropertyName("ports")]
    public List<PortRule> Ports { get; set; } = new();

    [JsonPropertyName("services")]
    public List<ServiceBypassRule> Services { get; set; } = new();
}

public class PortRule
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("portRangeEnd")]
    public int? PortRangeEnd { get; set; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "tcp"; // tcp, udp, both

    public override string ToString()
    {
        var portStr = PortRangeEnd.HasValue ? $"{Port}-{PortRangeEnd}" : Port.ToString();
        return $"{portStr}/{Protocol}";
    }
}

public class ServiceBypassRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}
```

### 3.2 Streaming Mode Models

```csharp
// File: XNetwork/Models/StreamingSettings.cs

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Represents streaming mode configuration from "show streaming"
/// </summary>
public class StreamingSettings
{
    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = new();

    [JsonPropertyName("ipv4")]
    public List<string> IPv4Addresses { get; set; } = new();

    [JsonPropertyName("ipv6")]
    public List<string> IPv6Addresses { get; set; } = new();

    [JsonPropertyName("ports")]
    public List<PortRule> Ports { get; set; } = new();
}
```

### 3.3 Privacy Settings Model

```csharp
// File: XNetwork/Models/PrivacySettings.cs

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Represents privacy settings from "show privacy"
/// </summary>
public class PrivacySettings
{
    [JsonPropertyName("dnsAddresses")]
    public List<string> DnsAddresses { get; set; } = new();

    [JsonPropertyName("dnsleak")]
    public bool DnsLeak { get; set; } // Windows only

    [JsonPropertyName("ipleak")]
    public bool IpLeak { get; set; } // Windows only

    [JsonPropertyName("killswitch")]
    public bool KillSwitch { get; set; } // Windows only

    [JsonPropertyName("requestToDisableDoH")]
    public bool RequestToDisableDoH { get; set; }

    [JsonPropertyName("advancedIspStats")]
    public bool AdvancedIspStats { get; set; }

    [JsonPropertyName("apiProtection")]
    public bool ApiProtection { get; set; }
}
```

### 3.4 Transport Settings Model

```csharp
// File: XNetwork/Models/TransportSettings.cs

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Transport configuration settings
/// </summary>
public class TransportSettings
{
    /// <summary>
    /// Transport mode: auto, tcp, tcp-multi, udp, https
    /// </summary>
    [JsonPropertyName("transportMode")]
    public string TransportMode { get; set; } = "auto";

    /// <summary>
    /// Transport retry time in seconds
    /// </summary>
    [JsonPropertyName("transportRetrySeconds")]
    public int TransportRetrySeconds { get; set; } = 30;

    /// <summary>
    /// Connect retry time in seconds
    /// </summary>
    [JsonPropertyName("connectRetrySeconds")]
    public int ConnectRetrySeconds { get; set; } = 30;
}
```

### 3.5 Extended Adapter Model

```csharp
// File: XNetwork/Models/AdapterExtended.cs

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Extended adapter model with all configurable properties
/// </summary>
public class AdapterExtended
{
    [JsonPropertyName("adapterID")]
    public string AdapterId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("isp")]
    public string Isp { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "automatic";

    [JsonPropertyName("connectedNetworkName")]
    public string ConnectedNetworkName { get; set; } = string.Empty;

    [JsonPropertyName("connectedNetworkBSSID")]
    public string ConnectedNetworkBSSID { get; set; } = string.Empty;

    [JsonPropertyName("rateLimit")]
    public long RateLimit { get; set; }

    [JsonPropertyName("dataUsage")]
    public AdapterDataUsage DataUsage { get; set; } = new();

    [JsonPropertyName("directionalSettings")]
    public AdapterDirectionalSettings DirectionalSettings { get; set; } = new();
}

public class AdapterDataUsage
{
    [JsonPropertyName("usageDaily")]
    public long UsageDaily { get; set; }

    [JsonPropertyName("usageDailyBoost")]
    public long UsageDailyBoost { get; set; }

    [JsonPropertyName("usageDailyLimit")]
    public long UsageDailyLimit { get; set; }

    [JsonPropertyName("usageMonthly")]
    public long UsageMonthly { get; set; }

    [JsonPropertyName("usageMonthlyLimit")]
    public long UsageMonthlyLimit { get; set; }

    [JsonPropertyName("usageMonthlyResetDay")]
    public int UsageMonthlyResetDay { get; set; }

    [JsonPropertyName("overlimitRatelimit")]
    public long OverlimitRatelimit { get; set; }
}

public class AdapterDirectionalSettings
{
    [JsonPropertyName("upload")]
    public string Upload { get; set; } = "on"; // on, backup_off, strict_off

    [JsonPropertyName("download")]
    public string Download { get; set; } = "on"; // on, backup_off, strict_off
}
```

### 3.6 Extended SpeedifySettings Model

```csharp
// File: XNetwork/Models/SpeedifySettings.cs (UPDATED)

namespace XNetwork.Models;

using System.Text.Json.Serialization;

public class SpeedifySettings
{
    // Existing properties...
    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; set; }

    [JsonPropertyName("headerCompression")]
    public bool HeaderCompression { get; set; }

    [JsonPropertyName("packetAggregation")]
    public bool PacketAggregation { get; set; }

    [JsonPropertyName("jumboPackets")]
    public bool JumboPackets { get; set; }

    [JsonPropertyName("bondingMode")]
    public string BondingMode { get; set; } = "speed";

    [JsonPropertyName("enableDefaultRoute")]
    public bool EnableDefaultRoute { get; set; }

    [JsonPropertyName("allowChaChaEncryption")]
    public bool AllowChaChaEncryption { get; set; }

    [JsonPropertyName("enableAutomaticPriority")]
    public bool EnableAutomaticPriority { get; set; }

    [JsonPropertyName("overflowThreshold")]
    public double OverflowThreshold { get; set; }

    [JsonPropertyName("perConnectionEncryptionEnabled")]
    public bool PerConnectionEncryptionEnabled { get; set; }

    // NEW properties
    [JsonPropertyName("transportMode")]
    public string TransportMode { get; set; } = "auto";

    [JsonPropertyName("startupConnect")]
    public bool StartupConnect { get; set; }

    [JsonPropertyName("priorityOverflowThreshold")]
    public double PriorityOverflowThreshold { get; set; }

    [JsonPropertyName("maxRedundantConnections")]
    public int MaxRedundantConnections { get; set; }

    [JsonPropertyName("targetConnectionsUpload")]
    public int TargetConnectionsUpload { get; set; }

    [JsonPropertyName("targetConnectionsDownload")]
    public int TargetConnectionsDownload { get; set; }

    [JsonPropertyName("forwardedPorts")]
    public List<ForwardedPort> ForwardedPorts { get; set; } = new();

    [JsonPropertyName("downstreamSubnets")]
    public List<SubnetEntry> DownstreamSubnets { get; set; } = new();

    [JsonPropertyName("perConnectionEncryptionSettings")]
    public List<PerConnectionEncryption> PerConnectionEncryptionSettings { get; set; } = new();
}

public class ForwardedPort
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "tcp";
}

public class SubnetEntry
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("prefixLength")]
    public int PrefixLength { get; set; }
}

public class PerConnectionEncryption
{
    [JsonPropertyName("adapterId")]
    public string AdapterId { get; set; } = string.Empty;

    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; set; }
}
```

### 3.7 Connect Method Model

```csharp
// File: XNetwork/Models/ConnectMethod.cs

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Represents the connect method configuration from "show connectmethod"
/// </summary>
public class ConnectMethod
{
    [JsonPropertyName("connectMethod")]
    public string Method { get; set; } = "closest"; // closest, public, private, p2p, or country/city

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("num")]
    public int ServerNumber { get; set; }
}
```

### 3.8 Fixed Delay Settings Model

```csharp
// File: XNetwork/Models/FixedDelaySettings.cs

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Represents fixed delay configuration from "show fixeddelay"
/// </summary>
public class FixedDelaySettings
{
    [JsonPropertyName("delayMs")]
    public int DelayMs { get; set; }

    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = new();

    [JsonPropertyName("ips")]
    public List<string> IpAddresses { get; set; } = new();

    [JsonPropertyName("ports")]
    public List<PortRule> Ports { get; set; } = new();
}
```

### 3.9 Adapter Priority Model

```csharp
// File: XNetwork/Models/AdapterPriority.cs

namespace XNetwork.Models;

/// <summary>
/// Represents adapter priority for Speedify bonding.
/// Note: Streaming bypass functionality uses Speedify's built-in streamingbypass commands,
/// not custom OS-level routing.
/// </summary>
public class AdapterPriority
{
    public string AdapterId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Priority { get; set; } = "automatic"; // automatic, always, secondary, backup, never
    public bool IsEnabled { get; set; }
}
```

### 3.10 Settings State Container

```csharp
// File: XNetwork/Models/SettingsState.cs

namespace XNetwork.Models;

/// <summary>
/// Container for all settings state with change tracking
/// </summary>
public class SettingsState
{
    // Connection Settings
    public SpeedifySettings ConnectionSettings { get; set; } = new();
    public SpeedifySettings OriginalConnectionSettings { get; set; } = new();

    // Privacy Settings
    public PrivacySettings PrivacySettings { get; set; } = new();
    public PrivacySettings OriginalPrivacySettings { get; set; } = new();

    // Streaming Bypass
    public StreamingBypassSettings StreamingBypass { get; set; } = new();
    public StreamingBypassSettings OriginalStreamingBypass { get; set; } = new();

    // Streaming Mode
    public StreamingSettings StreamingSettings { get; set; } = new();
    public StreamingSettings OriginalStreamingSettings { get; set; } = new();

    // Fixed Delay
    public FixedDelaySettings FixedDelaySettings { get; set; } = new();
    public FixedDelaySettings OriginalFixedDelaySettings { get; set; } = new();

    // Transport
    public TransportSettings TransportSettings { get; set; } = new();
    public TransportSettings OriginalTransportSettings { get; set; } = new();

    // Connect Method
    public ConnectMethod ConnectMethod { get; set; } = new();
    public ConnectMethod OriginalConnectMethod { get; set; } = new();

    // Adapters
    public List<AdapterExtended> Adapters { get; set; } = new();
    public List<AdapterExtended> OriginalAdapters { get; set; } = new();

    // Adapter Priorities
    public List<AdapterPriority> AdapterPriorities { get; set; } = new();
    public List<AdapterPriority> OriginalAdapterPriorities { get; set; } = new();

    // Change tracking
    public bool HasPendingChanges => GetPendingChangeCount() > 0;
    public int GetPendingChangeCount()
    {
        int count = 0;
        // Compare each section...
        // Implementation details in service
        return count;
    }
}
```

---

## 4. SpeedifyService Method Additions

### 4.1 Privacy Settings Methods

```csharp
// Add to SpeedifyService.cs

/// <summary>
/// Gets privacy settings from Speedify
/// </summary>
public async Task<PrivacySettings?> GetPrivacySettingsAsync(CancellationToken ct = default)
{
    try
    {
        var json = await Task.Run(() => RunTerminatingCommand("show privacy"), ct);
        return JsonSerializer.Deserialize<PrivacySettings>(json, _jsonOptions);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error getting privacy settings: {ex.Message}");
        return null;
    }
}

/// <summary>
/// Sets custom DNS servers
/// </summary>
public async Task<bool> SetDnsAsync(IEnumerable<string> dnsServers, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", dnsServers);
        await Task.Run(() => RunTerminatingCommand($"dns {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting DNS: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets DNS leak protection (Windows only)
/// </summary>
public async Task<bool> SetDnsLeakProtectionAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"privacy dnsleak {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting DNS leak protection: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets kill switch (Windows only)
/// </summary>
public async Task<bool> SetKillSwitchAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"privacy killswitch {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting kill switch: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets IP leak protection (Windows only)
/// </summary>
public async Task<bool> SetIpLeakProtectionAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"privacy ipleak {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting IP leak protection: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets request to disable DoH in browsers
/// </summary>
public async Task<bool> SetRequestToDisableDoHAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"privacy requestToDisableDoH {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting DoH setting: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets advanced ISP stats
/// </summary>
public async Task<bool> SetAdvancedIspStatsAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"privacy advancedIspStats {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting advanced ISP stats: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets API protection
/// </summary>
public async Task<bool> SetApiProtectionAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"privacy apiProtection {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting API protection: {ex.Message}");
        return false;
    }
}
```

### 4.2 Streaming Bypass Methods

```csharp
/// <summary>
/// Gets streaming bypass settings
/// </summary>
public async Task<StreamingBypassSettings?> GetStreamingBypassAsync(CancellationToken ct = default)
{
    try
    {
        var json = await Task.Run(() => RunTerminatingCommand("show streamingbypass"), ct);
        return JsonSerializer.Deserialize<StreamingBypassSettings>(json, _jsonOptions);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error getting streaming bypass: {ex.Message}");
        return null;
    }
}

/// <summary>
/// Modifies streaming bypass domains
/// </summary>
/// <param name="operation">add, rem, or set</param>
/// <param name="domains">List of domains</param>
public async Task<bool> ModifyStreamingBypassDomainsAsync(string operation, IEnumerable<string> domains, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", domains);
        await Task.Run(() => RunTerminatingCommand($"streamingbypass domains {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying bypass domains: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies streaming bypass IPv4 addresses
/// </summary>
public async Task<bool> ModifyStreamingBypassIPv4Async(string operation, IEnumerable<string> addresses, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", addresses);
        await Task.Run(() => RunTerminatingCommand($"streamingbypass ipv4 {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying bypass IPv4: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies streaming bypass IPv6 addresses
/// </summary>
public async Task<bool> ModifyStreamingBypassIPv6Async(string operation, IEnumerable<string> addresses, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", addresses);
        await Task.Run(() => RunTerminatingCommand($"streamingbypass ipv6 {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying bypass IPv6: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies streaming bypass ports
/// </summary>
/// <param name="operation">add, rem, or set</param>
/// <param name="ports">List of port rules in format "port[-endPort]/proto"</param>
public async Task<bool> ModifyStreamingBypassPortsAsync(string operation, IEnumerable<string> ports, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", ports);
        await Task.Run(() => RunTerminatingCommand($"streamingbypass ports {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying bypass ports: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Enables/disables streaming bypass globally
/// </summary>
public async Task<bool> SetStreamingBypassEnabledAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"streamingbypass service {(enabled ? "enable" : "disable")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting bypass enabled: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Enables/disables a specific streaming service bypass
/// </summary>
public async Task<bool> SetServiceBypassAsync(string serviceName, bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"streamingbypass service {serviceName} {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting service bypass: {ex.Message}");
        return false;
    }
}
```

### 4.3 Transport Methods

```csharp
/// <summary>
/// Sets transport mode
/// </summary>
/// <param name="mode">auto, tcp, tcp-multi, udp, or https</param>
public async Task<bool> SetTransportModeAsync(string mode, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"transport {mode}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting transport mode: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets transport retry time
/// </summary>
public async Task<bool> SetTransportRetryAsync(int seconds, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"transportretry {seconds}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting transport retry: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets connect retry time
/// </summary>
public async Task<bool> SetConnectRetryAsync(int seconds, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"connectretry {seconds}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting connect retry: {ex.Message}");
        return false;
    }
}
```

### 4.4 Connection Settings Methods

```csharp
/// <summary>
/// Sets overflow threshold (speed in Mbps after which Secondary connections are not used)
/// </summary>
public async Task<bool> SetOverflowThresholdAsync(double mbps, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"overflow {mbps}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting overflow: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets priority overflow threshold
/// </summary>
public async Task<bool> SetPriorityOverflowAsync(double mbps, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"priorityoverflow {mbps}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting priority overflow: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets max redundant connections
/// </summary>
public async Task<bool> SetMaxRedundantAsync(int connections, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"maxredundant {connections}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting max redundant: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets target connections for upload/download
/// </summary>
public async Task<bool> SetTargetConnectionsAsync(int upload, int download, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"targetconnections {upload} {download}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting target connections: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets startup connect behavior
/// </summary>
public async Task<bool> SetStartupConnectAsync(bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"startupconnect {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting startup connect: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets connect method
/// </summary>
public async Task<bool> SetConnectMethodAsync(string method, string? country = null, string? city = null, int? serverNum = null, CancellationToken ct = default)
{
    try
    {
        var cmd = $"connectmethod {method}";
        if (!string.IsNullOrEmpty(country)) cmd += $" {country}";
        if (!string.IsNullOrEmpty(city)) cmd += $" {city}";
        if (serverNum.HasValue) cmd += $" {serverNum}";

        await Task.Run(() => RunTerminatingCommand(cmd), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting connect method: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Gets connect method settings
/// </summary>
public async Task<ConnectMethod?> GetConnectMethodAsync(CancellationToken ct = default)
{
    try
    {
        var json = await Task.Run(() => RunTerminatingCommand("show connectmethod"), ct);
        return JsonSerializer.Deserialize<ConnectMethod>(json, _jsonOptions);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error getting connect method: {ex.Message}");
        return null;
    }
}
```

### 4.5 Per-Adapter Settings Methods

```csharp
/// <summary>
/// Gets extended adapter information
/// </summary>
public async Task<List<AdapterExtended>?> GetAdaptersExtendedAsync(CancellationToken ct = default)
{
    try
    {
        var json = await Task.Run(() => RunTerminatingCommand("show adapters"), ct);
        return JsonSerializer.Deserialize<List<AdapterExtended>>(json, _jsonOptions);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error getting adapters: {ex.Message}");
        return null;
    }
}

/// <summary>
/// Sets per-adapter encryption
/// </summary>
public async Task<bool> SetAdapterEncryptionAsync(string adapterId, bool enabled, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"adapter encryption {adapterId} {(enabled ? "on" : "off")}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting adapter encryption: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets adapter rate limit
/// </summary>
/// <param name="downloadBps">Download speed in bits per second, or 0 for unlimited</param>
/// <param name="uploadBps">Upload speed in bits per second, or 0 for unlimited</param>
public async Task<bool> SetAdapterRateLimitAsync(string adapterId, long downloadBps, long uploadBps, CancellationToken ct = default)
{
    try
    {
        var downloadArg = downloadBps == 0 ? "unlimited" : downloadBps.ToString();
        var uploadArg = uploadBps == 0 ? "unlimited" : uploadBps.ToString();
        await Task.Run(() => RunTerminatingCommand($"adapter ratelimit {adapterId} {downloadArg} {uploadArg}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting adapter rate limit: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets daily data limit for adapter
/// </summary>
/// <param name="bytes">Limit in bytes, or 0 for unlimited</param>
public async Task<bool> SetAdapterDailyLimitAsync(string adapterId, long bytes, CancellationToken ct = default)
{
    try
    {
        var arg = bytes == 0 ? "unlimited" : bytes.ToString();
        await Task.Run(() => RunTerminatingCommand($"adapter datalimit daily {adapterId} {arg}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting adapter daily limit: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets monthly data limit for adapter
/// </summary>
/// <param name="bytes">Limit in bytes, or 0 for unlimited</param>
/// <param name="resetDay">Day of month to reset, or 0 for rolling 30 days</param>
public async Task<bool> SetAdapterMonthlyLimitAsync(string adapterId, long bytes, int resetDay, CancellationToken ct = default)
{
    try
    {
        var arg = bytes == 0 ? "unlimited" : bytes.ToString();
        await Task.Run(() => RunTerminatingCommand($"adapter datalimit monthly {adapterId} {arg} {resetDay}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting adapter monthly limit: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets overlimit rate limit for adapter
/// </summary>
/// <param name="bps">Speed in bits per second when over limit, or 0 to disable adapter</param>
public async Task<bool> SetAdapterOverlimitRateLimitAsync(string adapterId, long bps, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"adapter overlimitratelimit {adapterId} {bps}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting adapter overlimit rate: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets directional mode for adapter
/// </summary>
/// <param name="uploadMode">on, backup_off, or strict_off</param>
/// <param name="downloadMode">on, backup_off, or strict_off</param>
public async Task<bool> SetAdapterDirectionalModeAsync(string adapterId, string uploadMode, string downloadMode, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"adapter directionalmode {adapterId} {uploadMode} {downloadMode}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting adapter directional mode: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Resets usage statistics for adapter
/// </summary>
public async Task<bool> ResetAdapterUsageAsync(string adapterId, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"adapter resetusage {adapterId}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error resetting adapter usage: {ex.Message}");
        return false;
    }
}
```

### 4.6 Port Forwarding & Subnets Methods

```csharp
/// <summary>
/// Sets forwarded ports (for private servers)
/// </summary>
/// <param name="ports">List of ports in format "port/proto"</param>
public async Task<bool> SetForwardedPortsAsync(IEnumerable<string> ports, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", ports);
        await Task.Run(() => RunTerminatingCommand($"ports {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting forwarded ports: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Sets downstream subnets
/// </summary>
/// <param name="subnets">List of subnets in format "address/length"</param>
public async Task<bool> SetSubnetsAsync(IEnumerable<string> subnets, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", subnets);
        await Task.Run(() => RunTerminatingCommand($"subnets {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting subnets: {ex.Message}");
        return false;
    }
}
```

### 4.7 Streaming Mode Methods

```csharp
/// <summary>
/// Gets streaming mode settings
/// </summary>
public async Task<StreamingSettings?> GetStreamingSettingsAsync(CancellationToken ct = default)
{
    try
    {
        var json = await Task.Run(() => RunTerminatingCommand("show streaming"), ct);
        return JsonSerializer.Deserialize<StreamingSettings>(json, _jsonOptions);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error getting streaming settings: {ex.Message}");
        return null;
    }
}

/// <summary>
/// Modifies streaming mode domains
/// </summary>
public async Task<bool> ModifyStreamingDomainsAsync(string operation, IEnumerable<string> domains, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", domains);
        await Task.Run(() => RunTerminatingCommand($"streaming domains {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying streaming domains: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies streaming mode IPv4 addresses
/// </summary>
public async Task<bool> ModifyStreamingIPv4Async(string operation, IEnumerable<string> addresses, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", addresses);
        await Task.Run(() => RunTerminatingCommand($"streaming ipv4 {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying streaming IPv4: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies streaming mode ports
/// </summary>
public async Task<bool> ModifyStreamingPortsAsync(string operation, IEnumerable<string> ports, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", ports);
        await Task.Run(() => RunTerminatingCommand($"streaming ports {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying streaming ports: {ex.Message}");
        return false;
    }
}
```

### 4.8 Fixed Delay Methods

```csharp
/// <summary>
/// Gets fixed delay settings
/// </summary>
public async Task<FixedDelaySettings?> GetFixedDelaySettingsAsync(CancellationToken ct = default)
{
    try
    {
        var json = await Task.Run(() => RunTerminatingCommand("show fixeddelay"), ct);
        return JsonSerializer.Deserialize<FixedDelaySettings>(json, _jsonOptions);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error getting fixed delay settings: {ex.Message}");
        return null;
    }
}

/// <summary>
/// Sets fixed delay value in milliseconds
/// </summary>
public async Task<bool> SetFixedDelayAsync(int delayMs, CancellationToken ct = default)
{
    try
    {
        await Task.Run(() => RunTerminatingCommand($"fixeddelay {delayMs}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error setting fixed delay: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies fixed delay domains
/// </summary>
public async Task<bool> ModifyFixedDelayDomainsAsync(string operation, IEnumerable<string> domains, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", domains);
        await Task.Run(() => RunTerminatingCommand($"fixeddelay domains {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying fixed delay domains: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies fixed delay IPs
/// </summary>
public async Task<bool> ModifyFixedDelayIPsAsync(string operation, IEnumerable<string> ips, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", ips);
        await Task.Run(() => RunTerminatingCommand($"fixeddelay ips {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying fixed delay IPs: {ex.Message}");
        return false;
    }
}

/// <summary>
/// Modifies fixed delay ports
/// </summary>
public async Task<bool> ModifyFixedDelayPortsAsync(string operation, IEnumerable<string> ports, CancellationToken ct = default)
{
    try
    {
        var args = string.Join(" ", ports);
        await Task.Run(() => RunTerminatingCommand($"fixeddelay ports {operation} {args}"), ct);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SpeedifyService: Error modifying fixed delay ports: {ex.Message}");
        return false;
    }
}
```

---

## 5. UI Component Breakdown

### 5.1 New Custom Components

#### 5.1.1 Accordion Component
```
XNetwork/Components/Custom/Accordion.razor
```
- Collapsible section with header and content
- Support for icons, badges (change indicators)
- Smooth expand/collapse animation
- Optional default expanded state

#### 5.1.2 Editable List Component
```
XNetwork/Components/Custom/EditableList.razor
```
- Add/remove items from a list
- Inline editing
- Validation per item
- Support for different item types (domains, IPs, ports)

#### 5.1.3 Draggable List Component
```
XNetwork/Components/Custom/DraggableList.razor
```
- Reorderable list via drag-and-drop
- Touch support for mobile
- Visual feedback during drag
- Position number display

#### 5.1.4 Number Input with Unit Component
```
XNetwork/Components/Custom/NumberInputWithUnit.razor
```
- Number input with configurable unit (Mbps, bytes, seconds)
- Support for "unlimited" option
- Validation (min/max)
- Format display

#### 5.1.5 Port Rule Editor Component
```
XNetwork/Components/Custom/PortRuleEditor.razor
```
- Input for port number or range
- Protocol selector (TCP/UDP/Both)
- Validation
- Display format: "port[-endPort]/proto"

#### 5.1.6 Change Indicator Badge
```
XNetwork/Components/Custom/ChangeBadge.razor
```
- Visual indicator for pending changes
- Animated pulse effect
- Count display option

### 5.2 Settings Section Components

#### 5.2.1 Connection Settings Section
```
XNetwork/Components/Settings/ConnectionSettingsSection.razor
```
Contains:
- Bonding Mode selector
- Encryption toggle
- Header Compression toggle
- Packet Aggregation toggle
- Jumbo Packets toggle
- Overflow Threshold input
- Priority Overflow input
- Max Redundant Connections input
- Target Connections (upload/download)

#### 5.2.2 Transport Settings Section
```
XNetwork/Components/Settings/TransportSettingsSection.razor
```
Contains:
- Transport Mode selector (auto, tcp, tcp-multi, udp, https)
- Transport Retry Time input
- Connect Retry Time input

#### 5.2.3 Streaming Bypass Section
```
XNetwork/Components/Settings/StreamingBypassSection.razor
```
Contains:
- Global enable/disable toggle
- Domains list (editable)
- IPv4 addresses list (editable)
- IPv6 addresses list (editable)
- Ports list (port rule editor)
- Services list with toggle switches

#### 5.2.4 Streaming Mode Section
```
XNetwork/Components/Settings/StreamingModeSection.razor
```
Contains:
- Domains list (editable)
- IPv4 addresses list (editable)
- IPv6 addresses list (editable)
- Ports list (port rule editor)

#### 5.2.5 Privacy & DNS Section
```
XNetwork/Components/Settings/PrivacySettingsSection.razor
```
Contains:
- Custom DNS servers list (editable)
- DNS Leak Protection toggle (Windows only)
- IP Leak Protection toggle (Windows only)
- Kill Switch toggle (Windows only)
- Request to Disable DoH toggle
- Advanced ISP Stats toggle
- API Protection toggle

#### 5.2.6 Adapter Settings Section
```
XNetwork/Components/Settings/AdapterSettingsSection.razor
```
Contains:
- List of adapters (expandable cards)
- Per adapter:
  - Priority selector
  - Per-adapter encryption toggle
  - Rate limit inputs (download/upload)
  - Daily data limit input
  - Monthly data limit input with reset day
  - Overlimit rate limit input
  - Directional mode selectors
  - Reset usage button

#### 5.2.7 Adapter Priority Section
```
XNetwork/Components/Settings/AdapterPrioritySection.razor
```
Contains:
- Draggable adapter list for Speedify priority ordering
- Priority dropdown per adapter (automatic, always, secondary, backup, never)
- Explanation text about how Speedify uses adapter priorities

> **Note:** Streaming bypass functionality (routing traffic outside Speedify VPN) uses
> Speedify's built-in `streamingbypass` CLI commands - see StreamingBypassSection.

#### 5.2.8 Startup & Connection Section
```
XNetwork/Components/Settings/StartupSettingsSection.razor
```
Contains:
- Auto-connect on startup toggle
- Connect method configuration
  - Method selector (closest, public, private, p2p, specific)
  - Country/City/Server inputs (conditional)

#### 5.2.9 Port Forwarding Section
```
XNetwork/Components/Settings/PortForwardingSection.razor
```
Contains:
- Explanation about private servers
- Ports list (port rule editor)
- Note about reconnect requirement

#### 5.2.10 Advanced Settings Section
```
XNetwork/Components/Settings/AdvancedSettingsSection.razor
```
Contains:
- Default Route toggle
- Downstream Subnets list (editable)
- Fixed Delay configuration
  - Delay value input (ms)
  - Domains list
  - IPs list
  - Ports list

---

## 6. Settings Page Layout Structure

### 6.1 Main Layout
```html
<div class="settings-page">
    <!-- Header -->
    <header class="settings-header">
        <h1>Settings</h1>
        <p>Configure Speedify and application behavior</p>
    </header>

    <!-- Global Action Bar (sticky on mobile) -->
    <div class="settings-action-bar">
        <div class="change-indicator" if="hasPendingChanges">
            <span class="pulse-dot"></span>
            <span>{changeCount} unsaved changes</span>
        </div>
        <button class="btn-cancel" disabled="!hasPendingChanges">Cancel</button>
        <button class="btn-save" disabled="!hasPendingChanges">Save Changes</button>
    </div>

    <!-- Settings Content -->
    <div class="settings-content">
        <!-- Section 1: Connection -->
        <Accordion Title="Connection Settings" Icon="fa-plug" DefaultExpanded="true">
            <ConnectionSettingsSection />
        </Accordion>

        <!-- Section 2: Transport -->
        <Accordion Title="Transport Protocol" Icon="fa-network-wired">
            <TransportSettingsSection />
        </Accordion>

        <!-- Section 3: Streaming Bypass -->
        <Accordion Title="Streaming Bypass" Icon="fa-route" Badge="bypassRuleCount">
            <StreamingBypassSection />
        </Accordion>

        <!-- Section 4: Streaming Mode -->
        <Accordion Title="Streaming Mode Rules" Icon="fa-video">
            <StreamingModeSection />
        </Accordion>

        <!-- Section 5: Privacy & DNS -->
        <Accordion Title="Privacy & DNS" Icon="fa-shield-alt">
            <PrivacySettingsSection />
        </Accordion>

        <!-- Section 6: Network Adapters -->
        <Accordion Title="Network Adapters" Icon="fa-ethernet" Badge="adapterCount">
            <AdapterSettingsSection />
        </Accordion>

        <!-- Section 7: Adapter Priority -->
        <Accordion Title="Adapter Priority" Icon="fa-sort-numeric-down">
            <AdapterPrioritySection />
        </Accordion>

        <!-- Section 8: Startup & Auto-Connect -->
        <Accordion Title="Startup & Connection" Icon="fa-power-off">
            <StartupSettingsSection />
        </Accordion>

        <!-- Section 9: Port Forwarding -->
        <Accordion Title="Port Forwarding" Icon="fa-arrows-alt-h">
            <PortForwardingSection />
        </Accordion>

        <!-- Section 10: Advanced -->
        <Accordion Title="Advanced Settings" Icon="fa-cogs">
            <AdvancedSettingsSection />
        </Accordion>

        <!-- Section 11: System Controls (existing) -->
        <Accordion Title="System Controls" Icon="fa-server">
            <!-- Existing disconnect/reconnect/restart/reboot buttons -->
        </Accordion>
    </div>
</div>
```

### 6.2 Mobile-First CSS Structure
```css
/* Base mobile styles */
.settings-page {
    padding: 1rem;
    max-width: 100%;
}

.settings-action-bar {
    position: sticky;
    top: 0;
    z-index: 100;
    background: rgba(15, 23, 42, 0.95);
    backdrop-filter: blur(8px);
    padding: 0.75rem 1rem;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    border-bottom: 1px solid rgb(51, 65, 85);
}

.settings-content {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
}

/* Tablet and up */
@media (min-width: 768px) {
    .settings-page {
        padding: 1.5rem;
        max-width: 768px;
        margin: 0 auto;
    }

    .settings-content {
        gap: 1rem;
    }
}

/* Desktop */
@media (min-width: 1024px) {
    .settings-page {
        max-width: 900px;
    }
}
```

### 6.3 Accordion Component Structure
```html
<div class="accordion @(IsExpanded ? 'expanded' : 'collapsed') @(HasChanges ? 'has-changes' : '')">
    <button class="accordion-header" @onclick="Toggle">
        <div class="accordion-title">
            <i class="fas @Icon"></i>
            <span>@Title</span>
        </div>
        <div class="accordion-meta">
            @if (HasChanges)
            {
                <span class="change-badge">Modified</span>
            }
            @if (Badge != null)
            {
                <span class="count-badge">@Badge</span>
            }
            <i class="fas fa-chevron-down accordion-arrow"></i>
        </div>
    </button>
    <div class="accordion-content">
        @ChildContent
    </div>
</div>
```

---

## 7. Implementation Phases

### Phase 1: Foundation (Week 1)
**Priority: Critical** ✅ **COMPLETED**

1. **Create new model files**
   - [x] [`StreamingBypassSettings.cs`](XNetwork/Models/StreamingBypassSettings.cs)
   - [x] [`StreamingSettings.cs`](XNetwork/Models/StreamingSettings.cs)
   - [x] [`PrivacySettings.cs`](XNetwork/Models/PrivacySettings.cs)
   - [x] [`TransportSettings.cs`](XNetwork/Models/TransportSettings.cs)
   - [x] [`AdapterExtended.cs`](XNetwork/Models/AdapterExtended.cs)
   - [x] [`ConnectMethod.cs`](XNetwork/Models/ConnectMethod.cs)
   - [x] [`FixedDelaySettings.cs`](XNetwork/Models/FixedDelaySettings.cs)
   - [x] [`ForwardedPort.cs`](XNetwork/Models/ForwardedPort.cs)
   - [x] [`DownstreamSubnet.cs`](XNetwork/Models/DownstreamSubnet.cs)

2. **Update existing models**
   - [x] Update [`SpeedifySettings.cs`](XNetwork/Models/SpeedifySettings.cs) with new properties

3. **Create base UI components**
   - [x] [`Accordion.razor`](XNetwork/Components/Custom/Accordion.razor)
   - [x] [`EditableList.razor`](XNetwork/Components/Custom/EditableList.razor)
   - [x] [`NumberInput.razor`](XNetwork/Components/Custom/NumberInput.razor)
   - [x] [`SelectDropdown.razor`](XNetwork/Components/Custom/SelectDropdown.razor)
   - [x] [`PortRuleEditor.razor`](XNetwork/Components/Custom/PortRuleEditor.razor)

### Phase 2: SpeedifyService Extension (Week 2)
**Priority: Critical** ✅ **COMPLETED**

1. **Add privacy settings methods**
   - [x] `GetPrivacySettingsAsync()`
   - [x] `SetDnsServersAsync()`
   - [x] `SetDnsLeakProtectionAsync()`
   - [x] `SetKillSwitchAsync()`
   - [x] `SetIpLeakProtectionAsync()`
   - [x] `SetDisableDoHRequestAsync()`

2. **Add streaming bypass methods**
   - [x] `GetStreamingBypassSettingsAsync()`
   - [x] `SetStreamingBypassDomainsAsync()`
   - [x] `SetStreamingBypassIpv4Async()`
   - [x] `SetStreamingBypassIpv6Async()`
   - [x] `SetStreamingBypassPortsAsync()`
   - [x] `SetStreamingBypassEnabledAsync()`
   - [x] `SetStreamingBypassServiceAsync()`

3. **Add transport methods**
   - [x] `GetTransportSettingsAsync()`
   - [x] `SetTransportModeAsync()`
   - [x] `SetTransportRetryAsync()`
   - [x] `SetConnectRetryAsync()`

4. **Add connection settings methods**
   - [x] `SetOverflowThresholdAsync()`
   - [x] `SetPriorityOverflowAsync()`
   - [x] `SetMaxRedundantAsync()`
   - [x] `SetTargetConnectionsAsync()`
   - [x] `SetStartupConnectAsync()`
   - [x] `SetConnectMethodAsync()`
   - [x] `GetConnectMethodAsync()`

### Phase 3: Adapter Settings (Week 3)
**Priority: High** ✅ **COMPLETED**

1. **Add per-adapter methods to SpeedifyService**
   - [x] `GetAdaptersExtendedAsync()`
   - [x] `SetAdapterEncryptionAsync()`
   - [x] `SetAdapterRateLimitAsync()`
   - [x] `SetAdapterDailyLimitAsync()`
   - [x] `SetAdapterMonthlyLimitAsync()`
   - [x] `SetAdapterOverlimitRateAsync()`
   - [x] `SetAdapterDirectionalModeAsync()`
   - [x] `ResetAdapterUsageAsync()`
   - [x] `SetAdapterExposeDscpAsync()`
   - [x] `SetAdapterDailyBoostAsync()`

2. **Create port rule editor component**
   - [x] [`PortRuleEditor.razor`](XNetwork/Components/Custom/PortRuleEditor.razor)

3. **Create adapter settings section**
   - [x] Integrated into Settings.razor as Section 13

### Phase 4: Core Settings Sections (Week 4)
**Priority: High** ✅ **COMPLETED**

1. **Create settings section components**
   - [x] Connection Settings - Section 1 in Settings.razor
   - [x] Transport Protocol - Section 2 in Settings.razor
   - [x] Privacy & DNS - Section 3 in Settings.razor
   - [x] Server Selection - Section 6 in Settings.razor

### Phase 5: Streaming & Bypass Sections (Week 5)
**Priority: High** ✅ **COMPLETED**

1. **Create streaming bypass section**
   - [x] Streaming Bypass - Section 4 in Settings.razor

2. **Create streaming mode section**
   - [x] Streaming Mode Rules - Section 5 in Settings.razor

3. **Add remaining SpeedifyService methods**
   - [x] `GetStreamingSettingsAsync()`
   - [x] `SetStreamingDomainsAsync()`
   - [x] `SetStreamingIpv4Async()`
   - [x] `SetStreamingIpv6Async()`
   - [x] `SetStreamingPortsAsync()`

### Phase 6: Adapter Priority & Advanced (Week 6)
**Priority: Medium** ✅ **COMPLETED**

1. **Adapter priority integrated into Adapter Settings section**
    - [x] Priority selection per adapter using SelectDropdown

2. **Create advanced settings section**
   - [x] Advanced Settings - Section 7 in Settings.razor

3. **Add fixed delay methods**
   - [x] `GetFixedDelaySettingsAsync()`
   - [x] `SetFixedDelayAsync()`
   - [x] `SetFixedDelayDomainsAsync()`
   - [x] `SetFixedDelayIpsAsync()`
   - [x] `SetFixedDelayPortsAsync()`

4. **Fixed delay section**
   - [x] Fixed Delay - Section 8 in Settings.razor

### Phase 7: Port Forwarding & Subnets (Week 7)
**Priority: Medium** ✅ **COMPLETED**

1. **Create port forwarding section**
   - [x] Port Forwarding - Section 9 in Settings.razor

2. **Add port forwarding methods**
   - [x] `SetForwardedPortsAsync()`
   - [x] `ClearForwardedPortsAsync()`

3. **Create downstream subnets section**
   - [x] Downstream Subnets - Section 10 in Settings.razor

4. **Add subnet methods**
   - [x] `SetDownstreamSubnetsAsync()`
   - [x] `ClearDownstreamSubnetsAsync()`

### Phase 8: Integration & Refactoring (Week 8)
**Priority: High** ✅ **COMPLETED**

1. **Refactor Settings.razor**
   - [x] All 13 sections integrated with accordion layout
   - [x] Global save button with batch change tracking
   - [x] Change tracking flags for bypass/streaming/adapter settings
   - [x] All event handlers connected

2. **Settings integrated directly in Settings.razor**
   - [x] Simplified architecture - no separate section components needed
   - [x] Change tracking via _hasUnsavedBypassChanges, _hasUnsavedStreamingChanges, _adapterPendingChanges
   - [x] SaveSettings() batches all pending changes

3. **Additional sections added**
   - [x] Network Monitor - Section 11
   - [x] System Controls - Section 12
   - [x] Adapter Settings - Section 13

### Phase 9: Testing & Polish (Week 9)
**Priority: High** ✅ **COMPLETED**

1. **Build verification**
   - [x] 0 errors, 0 warnings

2. **Integration testing**
   - [x] All settings load correctly from LoadSettings()
   - [x] All settings save correctly via SaveSettings()
   - [x] Error handling with _error and _successMessage feedback

3. **UX polish**
   - [x] Loading states via _isLoading and Preloader component
   - [x] Error messages via error banner
   - [x] Success feedback via success banner
   - [x] Accordion animations via CSS transitions
   - [x] Mobile responsive styles in app.css

---

## 8. File Changes Summary

### 8.1 New Files to Create

| File Path | Type | Description |
|-----------|------|-------------|
| `XNetwork/Models/StreamingBypassSettings.cs` | Model | Streaming bypass configuration |
| `XNetwork/Models/StreamingSettings.cs` | Model | Streaming mode configuration |
| `XNetwork/Models/PrivacySettings.cs` | Model | Privacy settings |
| `XNetwork/Models/TransportSettings.cs` | Model | Transport configuration |
| `XNetwork/Models/AdapterExtended.cs` | Model | Extended adapter info |
| `XNetwork/Models/ConnectMethod.cs` | Model | Connect method settings |
| `XNetwork/Models/FixedDelaySettings.cs` | Model | Fixed delay configuration |
| `XNetwork/Models/AdapterPriority.cs` | Model | Speedify adapter priority |
| `XNetwork/Components/Custom/Accordion.razor` | Component | Collapsible section |
| `XNetwork/Components/Custom/EditableList.razor` | Component | Add/remove list items |
| `XNetwork/Components/Custom/DraggableList.razor` | Component | Reorderable list |
| `XNetwork/Components/Custom/NumberInputWithUnit.razor` | Component | Number input with unit |
| `XNetwork/Components/Custom/PortRuleEditor.razor` | Component | Port/protocol editor |
| `XNetwork/Components/Custom/ChangeBadge.razor` | Component | Change indicator |
| `XNetwork/Components/Settings/ConnectionSettingsSection.razor` | Section | Connection settings UI |
| `XNetwork/Components/Settings/TransportSettingsSection.razor` | Section | Transport settings UI |
| `XNetwork/Components/Settings/StreamingBypassSection.razor` | Section | Bypass rules UI |
| `XNetwork/Components/Settings/StreamingModeSection.razor` | Section | Streaming mode UI |
| `XNetwork/Components/Settings/PrivacySettingsSection.razor` | Section | Privacy settings UI |
| `XNetwork/Components/Settings/AdapterSettingsSection.razor` | Section | Adapter settings UI |
| `XNetwork/Components/Settings/AdapterPrioritySection.razor` | Section | Adapter priority UI |
| `XNetwork/Components/Settings/StartupSettingsSection.razor` | Section | Startup settings UI |
| `XNetwork/Components/Settings/PortForwardingSection.razor` | Section | Port forwarding UI |
| `XNetwork/Components/Settings/AdvancedSettingsSection.razor` | Section | Advanced settings UI |
| `XNetwork/Services/SettingsStateService.cs` | Service | (Optional) State management |
| `XNetwork/Utils/SettingsValidation.cs` | Utility | Validation helpers |

### 8.2 Files to Modify

| File Path | Type | Changes |
|-----------|------|---------|
| `XNetwork/Models/SpeedifySettings.cs` | Model | Add new properties for transport, startup, etc. |
| `XNetwork/Services/SpeedifyService.cs` | Service | Add ~50 new methods for all settings |
| `XNetwork/Components/Pages/Settings.razor` | Page | Major refactor to use new components |
| `XNetwork/wwwroot/app.css` | Styles | Add accordion and settings styles |
| `XNetwork/Components/_Imports.razor` | Imports | Add new component namespaces |

### 8.3 Estimated Lines of Code

| Category | Estimated LOC |
|----------|---------------|
| New Models | ~400 |
| SpeedifyService Additions | ~600 |
| New UI Components | ~1,500 |
| Settings Section Components | ~2,000 |
| CSS/Styles | ~300 |
| **Total** | **~4,800** |

---

## 9. Testing Considerations

### 9.1 Unit Tests

#### SpeedifyService Method Tests
```csharp
// Example test structure
[TestClass]
public class SpeedifyServiceTests
{
    [TestMethod]
    public async Task SetTransportMode_ValidMode_ReturnsTrue()
    {
        // Arrange
        var service = new SpeedifyService();

        // Act
        var result = await service.SetTransportModeAsync("tcp");

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task ModifyStreamingBypassDomains_AddOperation_ReturnsTrue()
    {
        // Test adding domains
    }

    [TestMethod]
    public async Task SetAdapterRateLimit_Unlimited_ReturnsTrue()
    {
        // Test setting unlimited rate limit
    }
}
```

#### Validation Tests
```csharp
[TestClass]
public class ValidationTests
{
    [TestMethod]
    public void ValidateIPv4_ValidAddress_ReturnsTrue()
    {
        Assert.IsTrue(SettingsValidation.IsValidIPv4("192.168.1.1"));
    }

    [TestMethod]
    public void ValidateIPv4_InvalidAddress_ReturnsFalse()
    {
        Assert.IsFalse(SettingsValidation.IsValidIPv4("256.1.1.1"));
    }

    [TestMethod]
    public void ValidateDomain_ValidDomain_ReturnsTrue()
    {
        Assert.IsTrue(SettingsValidation.IsValidDomain("example.com"));
    }

    [TestMethod]
    public void ValidatePortRange_ValidRange_ReturnsTrue()
    {
        Assert.IsTrue(SettingsValidation.IsValidPortRange("8080-8090"));
    }
}
```

### 9.2 Integration Tests

#### Settings Flow Tests
1. Load settings from Speedify → Verify all values populated
2. Modify multiple settings → Verify pending changes tracked
3. Save changes → Verify CLI commands executed
4. Cancel changes → Verify reverts to original values
5. Error handling → Verify graceful handling of CLI failures

#### Component Tests
1. Accordion expand/collapse → Verify animation and state
2. EditableList add/remove → Verify list updates
3. DraggableList reorder → Verify order persisted
4. PortRuleEditor validation → Verify invalid inputs rejected

### 9.3 Manual Testing Checklist

#### Desktop Browser Testing
- [ ] Chrome (latest)
- [ ] Firefox (latest)
- [ ] Edge (latest)
- [ ] Safari (latest)

#### Mobile Testing
- [ ] iOS Safari
- [ ] Android Chrome
- [ ] Touch interactions work correctly
- [ ] Draggable list works on touch devices

#### Functionality Testing
- [ ] All settings load correctly from Speedify
- [ ] All settings save correctly to Speedify
- [ ] Change indicators show correctly
- [ ] Error messages display properly
- [ ] Validation prevents invalid input
- [ ] Platform-specific settings (Windows-only) hidden on Linux
- [ ] Adapter priority settings work correctly
- [ ] Streaming bypass rules work correctly (using Speedify's streamingbypass commands)

### 9.4 Error Scenarios to Test

1. **Speedify not running** → Show appropriate error, disable settings
2. **CLI command fails** → Show error message, revert change
3. **Invalid user input** → Show validation error inline
4. **Network timeout** → Show timeout error, allow retry
5. **Concurrent changes** → Handle gracefully (last write wins)

---

## 10. Appendix

### 10.1 Speedify CLI Command Reference

| Setting | CLI Command | Notes |
|---------|-------------|-------|
| Transport Mode | `transport <mode>` | auto, tcp, tcp-multi, udp, https |
| Transport Retry | `transportretry <seconds>` | |
| Connect Retry | `connectretry <seconds>` | |
| Bonding Mode | `mode <mode>` | speed, redundant, streaming |
| Encryption | `encryption <on\|off>` | |
| Header Compression | `headercompression <on\|off>` | |
| Packet Aggregation | `packetaggr <on\|off>` | |
| Jumbo Packets | `jumbo <on\|off>` | |
| Overflow | `overflow <mbps>` | |
| Priority Overflow | `priorityoverflow <mbps>` | |
| Max Redundant | `maxredundant <num>` | |
| Target Connections | `targetconnections <up> <down>` | |
| Startup Connect | `startupconnect <on\|off>` | |
| Connect Method | `connectmethod <method> [args]` | |
| DNS | `dns <ip> ...` | |
| DNS Leak | `privacy dnsleak <on\|off>` | Windows only |
| Kill Switch | `privacy killswitch <on\|off>` | Windows only |
| IP Leak | `privacy ipleak <on\|off>` | Windows only |
| DoH Request | `privacy requestToDisableDoH <on\|off>` | |
| Advanced ISP Stats | `privacy advancedIspStats <on\|off>` | |
| API Protection | `privacy apiProtection <on\|off>` | |
| Route Default | `route default <on\|off>` | |
| Adapter Priority | `adapter priority <id> <priority>` | |
| Adapter Encryption | `adapter encryption <id> <on\|off>` | |
| Adapter Rate Limit | `adapter ratelimit <id> <down> <up>` | |
| Daily Limit | `adapter datalimit daily <id> <bytes>` | |
| Monthly Limit | `adapter datalimit monthly <id> <bytes> <day>` | |
| Overlimit Rate | `adapter overlimitratelimit <id> <bps>` | |
| Directional Mode | `adapter directionalmode <id> <up> <down>` | |
| Reset Usage | `adapter resetusage <id>` | |
| Forwarded Ports | `ports [port/proto] ...` | |
| Subnets | `subnets [subnet/len] ...` | |
| Streaming Bypass Domains | `streamingbypass domains <op> [domains]` | |
| Streaming Bypass IPv4 | `streamingbypass ipv4 <op> [ips]` | |
| Streaming Bypass IPv6 | `streamingbypass ipv6 <op> [ips]` | |
| Streaming Bypass Ports | `streamingbypass ports <op> [ports]` | |
| Streaming Bypass Service | `streamingbypass service <name> <on\|off>` | |
| Streaming Domains | `streaming domains <op> [domains]` | |
| Streaming IPv4 | `streaming ipv4 <op> [ips]` | |
| Streaming IPv6 | `streaming ipv6 <op> [ips]` | |
| Streaming Ports | `streaming ports <op> [ports]` | |
| Fixed Delay | `fixeddelay <ms>` | |
| Fixed Delay Domains | `fixeddelay domains <op> [domains]` | |
| Fixed Delay IPs | `fixeddelay ips <op> [ips]` | |
| Fixed Delay Ports | `fixeddelay ports <op> [ports]` | |

### 10.2 Data Format Reference

#### Port Rule Format
```
port/proto           # Single port: 8080/tcp
port-endPort/proto   # Range: 8080-8090/tcp
```

#### Subnet Format
```
address/prefixLength  # Example: 192.168.1.0/24
```

#### Valid Priority Values
- `automatic` - Let Speedify manage
- `always` - Always use when connected
- `secondary` - Use when primary is congested
- `backup` - Only when others unavailable
- `never` - Don't use this adapter

#### Valid Directional Modes
- `on` - Use for this direction
- `backup_off` - Don't use for backup
- `strict_off` - Never use for this direction

### 10.3 UI Mockups

#### Settings Page Header
```
┌────────────────────────────────────────────────────────────┐
│ ⚙️ Settings                                                │
│ Configure Speedify and application behavior               │
├────────────────────────────────────────────────────────────┤
│ 🟡 3 unsaved changes              [Cancel] [Save Changes] │
└────────────────────────────────────────────────────────────┘
```

#### Accordion Section (Collapsed)
```
┌────────────────────────────────────────────────────────────┐
│ 🔌 Connection Settings                              [▼]   │
└────────────────────────────────────────────────────────────┘
```

#### Accordion Section (Expanded with Changes)
```
┌────────────────────────────────────────────────────────────┐
│ 🔌 Connection Settings              [Modified]      [▲]   │
├────────────────────────────────────────────────────────────┤
│                                                            │
│ Bonding Mode                                               │
│ ┌──────────────────────────────────────────────────────┐  │
│ │ Speed Mode - Maximum speed using all connections  ▼  │  │
│ └──────────────────────────────────────────────────────┘  │
│                                                            │
│ ┌──────┐ Enable VPN tunnel encryption                     │
│ │  ON  │                                                   │
│ └──────┘                                                   │
│                                                            │
│ ┌──────┐ Compress packet headers                          │
│ │ OFF  │                               [Modified]          │
│ └──────┘                                                   │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

#### Editable List Component
```
┌────────────────────────────────────────────────────────────┐
│ Bypass Domains                                             │
├────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────┐ [✕] │
│ │ netflix.com                                       │     │
│ └──────────────────────────────────────────────────┘     │
│ ┌──────────────────────────────────────────────────┐ [✕] │
│ │ hulu.com                                          │     │
│ └──────────────────────────────────────────────────┘     │
│ ┌──────────────────────────────────────────────────┐     │
│ │ Add domain...                                   [+]│     │
│ └──────────────────────────────────────────────────┘     │
└────────────────────────────────────────────────────────────┘
```

#### Adapter Priority List
```
┌────────────────────────────────────────────────────────────┐
│ Speedify Adapter Priority                                   │
├────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────────┐  │
│ │ eth0 - Ethernet                    [Always      ▼]   │  │
│ └──────────────────────────────────────────────────────┘  │
│ ┌──────────────────────────────────────────────────────┐  │
│ │ wlan0 - Wi-Fi                      [Secondary   ▼]   │  │
│ └──────────────────────────────────────────────────────┘  │
│ ┌──────────────────────────────────────────────────────┐  │
│ │ usb0 - USB Ethernet                [Backup      ▼]   │  │
│ └──────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────┘

Note: To bypass Speedify entirely for certain traffic (streaming services,
specific domains/IPs), use the "Streaming Bypass" section which configures
Speedify's built-in bypass functionality.
```

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-12-05 | AI Assistant | Initial draft |
| 2.0 | 2025-12-05 | AI Assistant | Implementation completed - all phases done |

---

## Implementation Summary

### Completed Components

| Component Type | Count | Details |
|---------------|-------|---------|
| Model Classes | 9 | PrivacySettings, StreamingBypassSettings, StreamingSettings, TransportSettings, ConnectMethod, FixedDelaySettings, AdapterExtended, ForwardedPort, DownstreamSubnet |
| SpeedifyService Methods | 50+ | Full CLI coverage for all settings |
| Custom Components | 5 | Accordion, EditableList, NumberInput, SelectDropdown, PortRuleEditor |
| Settings Sections | 13 | Connection, Transport, Privacy, Bypass, Streaming, Server, Advanced, FixedDelay, PortForwarding, Subnets, Monitor, Controls, Adapters |
| CSS Styles | ~600 lines | Dark theme, responsive, all components styled |

### Build Status
- **Errors**: 0
- **Warnings**: 0
- **Build Time**: ~1.5 seconds

---

*End of Implementation Plan*