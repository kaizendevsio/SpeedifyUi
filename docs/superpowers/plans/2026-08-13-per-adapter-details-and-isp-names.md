# Per-Adapter Details Sheet And Dynamic ISP Names Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every uLink dashboard adapter card open a detail action sheet (not just Starlink), and name each adapter from its live upstream ISP discovered through a free keyless HTTP API.

**Architecture:** A new `AdapterIdentityService` performs one interface-bound (`SO_BINDTODEVICE`) HTTP GET per WAN adapter so each answer reflects that adapter's own egress, caches results with TTL/backoff, and feeds a pure `AdapterNameResolver` used by `XBondStatsService`. A new `AdapterTelemetryHistoryService` keeps a bounded per-adapter metric ring so detail charts are populated on open. The Starlink sheet body is extracted into its own component and a generic `AdapterDetailsSheet` serves all adapters.

**Tech Stack:** .NET 9, Blazor Server (`@rendermode InteractiveServer`), `System.Text.Json`, xUnit, Tailwind CSS, Chart.js via `wwwroot/js/statisticsCharts.js`.

**Dependency direction (must not be violated):** `AdapterIdentityService` → `InterfaceMetadataService`. `XBondStatsService` → `AdapterIdentityService` (cached reads only, never awaits a lookup). Nothing in the identity path may read `XBondSnapshotCache`, because the cache wraps `XBondStatsService` and that would be a cycle.

**Spec:** `docs/superpowers/specs/2026-08-13-per-adapter-details-and-isp-names-design.md`

---

## File Structure

**Create:**
- `XNetwork/Models/AdapterIdentity.cs` — one adapter's upstream identity (public IP, ISP, org, AS, geo, source, error).
- `XNetwork/Models/AdapterIdentitySettings.cs` — config: enabled, endpoints, intervals, timeout.
- `XNetwork/Models/AdapterTelemetrySample.cs` — one timestamped per-path metric sample.
- `XNetwork/Services/InterfaceBoundHttpClientFactory.cs` — shared `SO_BINDTODEVICE` handler/client.
- `XNetwork/Services/AdapterIdentityResponseParser.cs` — parses ipwho.is + ip-api.com JSON; normalizes ISP names.
- `XNetwork/Services/AdapterNameResolver.cs` — pure name precedence resolution.
- `XNetwork/Services/AdapterIdentityService.cs` — hosted lookup loop + cache + manual refresh.
- `XNetwork/Services/AdapterIdentitySettingsStore.cs` — persists the user on/off toggle.
- `XNetwork/Services/AdapterTelemetryHistory.cs` — bounded per-interface ring (pure).
- `XNetwork/Services/AdapterTelemetryHistoryService.cs` — 1 s sampler feeding the ring.
- `XNetwork/Utils/TelemetryFormatter.cs` — shared display formatters.
- `XNetwork/Components/Custom/AdapterTelemetryChart.razor` — generic single-series chart.
- `XNetwork/Components/Custom/StarlinkDetailsSection.razor` — today's Starlink sheet body, extracted.
- `XNetwork/Components/Custom/AdapterDetailsSheet.razor` — generic sheet for all adapters.
- `XNetwork.Tests/AdapterIdentityResponseParserTests.cs`
- `XNetwork.Tests/AdapterNameResolverTests.cs`
- `XNetwork.Tests/AdapterIdentityServiceTests.cs`
- `XNetwork.Tests/AdapterTelemetryHistoryTests.cs`

**Modify:**
- `XNetwork/Services/StarlinkBoundHttpClientFactory.cs` — delegate socket binding to the shared factory.
- `XNetwork/Services/XBondStatsService.cs` — thread aliases + ISP names through `AdapterNameResolver`.
- `XNetwork/Components/Pages/Home.razor` — every card clickable; use the new sheet; drop moved code.
- `XNetwork/Components/Pages/Settings.razor` — ISP lookup toggle.
- `XNetwork/Program.cs` — DI registrations.
- `XNetwork/appsettings.json` — `AdapterIdentity` section.
- `XNetwork/Models/AppChangelog.cs` — version bump + changelog entry.
- `AGENTS.md` — journal entry for this change.

---

### Task 1: Extract the shared interface-bound HTTP factory

Pure refactor. No behavior change for Starlink.

**Files:**
- Create: `XNetwork/Services/InterfaceBoundHttpClientFactory.cs`
- Modify: `XNetwork/Services/StarlinkBoundHttpClientFactory.cs`

- [ ] **Step 1: Create the shared factory**

```csharp
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace XNetwork.Services;

/// <summary>
/// Creates HTTP clients whose TCP sockets are bound to a specific Linux network interface with
/// SO_BINDTODEVICE, so requests egress through that adapter instead of the default route.
/// </summary>
public static class InterfaceBoundHttpClientFactory
{
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    public static HttpClient CreateClient(string interfaceName, TimeSpan timeout)
    {
        return new HttpClient(CreateHandler(interfaceName, timeout), disposeHandler: true)
        {
            Timeout = timeout
        };
    }

    public static SocketsHttpHandler CreateHandler(string interfaceName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);

        return new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            EnableMultipleHttp2Connections = true,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!OperatingSystem.IsLinux())
                {
                    throw new InvalidOperationException("Interface-bound HTTP access requires Linux SO_BINDTODEVICE.");
                }

                var socket = CreateSocket(context.DnsEndPoint);
                try
                {
                    BindSocketToDevice(socket, interfaceName);
                    await ConnectAsync(socket, context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private static Socket CreateSocket(DnsEndPoint endpoint)
    {
        var family = IPAddress.TryParse(endpoint.Host, out var address)
            ? address.AddressFamily
            : AddressFamily.InterNetwork;

        return new Socket(family, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
    }

    private static Task ConnectAsync(Socket socket, DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        return IPAddress.TryParse(endpoint.Host, out var address)
            ? socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).AsTask()
            : socket.ConnectAsync(endpoint, cancellationToken).AsTask();
    }

    private static void BindSocketToDevice(Socket socket, string interfaceName)
    {
        var value = Encoding.ASCII.GetBytes(interfaceName + '\0');
        var result = setsockopt(
            socket.Handle,
            SolSocket,
            SoBindToDevice,
            value,
            (uint)value.Length);

        if (result != 0)
        {
            throw new SocketException(Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(
        IntPtr socket,
        int level,
        int optionName,
        byte[] optionValue,
        uint optionLength);
}
```

- [ ] **Step 2: Reduce `StarlinkBoundHttpClientFactory` to a delegation**

Replace everything in the file from `public static HttpClient CreateHttpClient` to the end of the class with:

```csharp
    public static HttpClient CreateHttpClient(StarlinkTelemetrySettings settings, string interfaceName)
    {
        return InterfaceBoundHttpClientFactory.CreateClient(interfaceName, settings.RequestTimeout);
    }

    public static SocketsHttpHandler CreateHandler(StarlinkTelemetrySettings settings, string interfaceName)
    {
        return InterfaceBoundHttpClientFactory.CreateHandler(interfaceName, settings.RequestTimeout);
    }
}
```

Delete the now-unused `SolSocket`/`SoBindToDevice` constants, `CreateSocket`, `ConnectAsync`, `BindSocketToDevice`, and the `setsockopt` P/Invoke from that file, plus the `System.Net`, `System.Net.Sockets`, `System.Runtime.InteropServices`, and `System.Text` usings if nothing else needs them. Keep `StarlinkHttpClientLease`, `IStarlinkHttpClientFactory`, and the `CreateAsync` method exactly as they are.

- [ ] **Step 3: Build and run existing tests to prove no regression**

Run: `dotnet build uLink.sln`
Expected: Build succeeded.

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName!~BrowserSmokeTests"`
Expected: all tests pass (Starlink interface/telemetry tests included).

- [ ] **Step 4: Commit**

```bash
git add XNetwork/Services/InterfaceBoundHttpClientFactory.cs XNetwork/Services/StarlinkBoundHttpClientFactory.cs
git commit -m "refactor: share interface-bound http client factory"
```

---

### Task 2: Models

**Files:**
- Create: `XNetwork/Models/AdapterIdentity.cs`, `XNetwork/Models/AdapterIdentitySettings.cs`, `XNetwork/Models/AdapterTelemetrySample.cs`

- [ ] **Step 1: Create `AdapterIdentity`**

```csharp
namespace XNetwork.Models;

/// <summary>
/// Upstream identity observed by probing an ISP lookup endpoint through one specific adapter.
/// </summary>
public sealed class AdapterIdentity
{
    public string InterfaceName { get; init; } = "";

    /// <summary>Public IP as seen by the lookup endpoint through this adapter.</summary>
    public string? PublicIp { get; init; }

    /// <summary>Raw ISP string reported by the endpoint.</summary>
    public string? Isp { get; init; }

    /// <summary>Raw organization string reported by the endpoint.</summary>
    public string? Organization { get; init; }

    /// <summary>Autonomous system label, for example "AS10139" or "AS10139 Smart Communications".</summary>
    public string? AsLabel { get; init; }

    public string? City { get; init; }

    public string? Region { get; init; }

    public string? Country { get; init; }

    public string? CountryCode { get; init; }

    /// <summary>Normalized, display-ready provider name derived from <see cref="Isp"/> or <see cref="Organization"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Endpoint that answered, for example "https://ipwho.is/".</summary>
    public string? Source { get; init; }

    /// <summary>Gateway the adapter used when this identity was captured; used to invalidate on modem swaps.</summary>
    public string? Gateway { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public string? Error { get; init; }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(DisplayName) || !string.IsNullOrWhiteSpace(PublicIp);

    public static AdapterIdentity Unavailable(string interfaceName, string error, DateTimeOffset updatedAtUtc, string? gateway = null) => new()
    {
        InterfaceName = interfaceName,
        Gateway = gateway,
        Error = error,
        UpdatedAtUtc = updatedAtUtc
    };
}
```

- [ ] **Step 2: Create `AdapterIdentitySettings`**

```csharp
namespace XNetwork.Models;

/// <summary>
/// Configuration for third-party ISP identity lookups performed per network adapter.
/// </summary>
public sealed class AdapterIdentitySettings
{
    /// <summary>When false, no lookups are performed and cached identities are dropped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lookup endpoints, tried in order until one returns a parsable answer.</summary>
    public List<string> Endpoints { get; set; } =
    [
        "https://ipwho.is/",
        "http://ip-api.com/json/"
    ];

    public int RefreshIntervalSeconds { get; set; } = 60;

    public int SuccessTtlMinutes { get; set; } = 15;

    public int FailureBackoffSeconds { get; set; } = 120;

    public int RequestTimeoutSeconds { get; set; } = 5;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(RefreshIntervalSeconds, 10, 3600));

    public TimeSpan SuccessTtl => TimeSpan.FromMinutes(Math.Clamp(SuccessTtlMinutes, 1, 1440));

    public TimeSpan FailureBackoff => TimeSpan.FromSeconds(Math.Clamp(FailureBackoffSeconds, 15, 3600));

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 1, 30));
}
```

- [ ] **Step 3: Create `AdapterTelemetrySample`**

```csharp
namespace XNetwork.Models;

/// <summary>One timestamped per-adapter metric sample used by the adapter details charts.</summary>
public sealed class AdapterTelemetrySample
{
    public DateTimeOffset TimestampUtc { get; init; }

    public double? RttMs { get; init; }

    public double? LossPercent { get; init; }

    public double? JitterMs { get; init; }

    public double DownloadMbps { get; init; }

    public double UploadMbps { get; init; }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build uLink.sln`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Models/AdapterIdentity.cs XNetwork/Models/AdapterIdentitySettings.cs XNetwork/Models/AdapterTelemetrySample.cs
git commit -m "feat: add adapter identity and telemetry sample models"
```

---

### Task 3: Response parsing and ISP name normalization (TDD)

**Files:**
- Create: `XNetwork/Services/AdapterIdentityResponseParser.cs`
- Test: `XNetwork.Tests/AdapterIdentityResponseParserTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterIdentityResponseParserTests
{
    [Fact]
    public void ParsesIpWhoIsSuccessResponse()
    {
        const string json = """
        {
          "ip": "103.235.93.167",
          "success": true,
          "city": "Cebu City",
          "region": "Central Visayas",
          "country": "Philippines",
          "country_code": "PH",
          "connection": { "asn": 10139, "org": "Smart Communications", "isp": "Smart Communications Inc." }
        }
        """;

        var parsed = AdapterIdentityResponseParser.TryParse(json, out var result);

        Assert.True(parsed);
        Assert.Equal("103.235.93.167", result!.PublicIp);
        Assert.Equal("Smart Communications Inc.", result.Isp);
        Assert.Equal("Smart Communications", result.Organization);
        Assert.Equal("AS10139", result.AsLabel);
        Assert.Equal("Cebu City", result.City);
        Assert.Equal("Central Visayas", result.Region);
        Assert.Equal("Philippines", result.Country);
        Assert.Equal("PH", result.CountryCode);
        Assert.Equal("Smart Communications", result.DisplayName);
    }

    [Fact]
    public void RejectsIpWhoIsFailureResponse()
    {
        const string json = """{"ip":null,"success":false,"message":"Reserved range"}""";

        Assert.False(AdapterIdentityResponseParser.TryParse(json, out _));
    }

    [Fact]
    public void ParsesIpApiSuccessResponse()
    {
        const string json = """
        {
          "status": "success",
          "country": "Philippines",
          "countryCode": "PH",
          "regionName": "Cebu",
          "city": "Cebu City",
          "isp": "Starlink",
          "org": "SpaceX Starlink",
          "as": "AS14593 Space Exploration Technologies Corporation",
          "query": "146.75.1.1"
        }
        """;

        var parsed = AdapterIdentityResponseParser.TryParse(json, out var result);

        Assert.True(parsed);
        Assert.Equal("146.75.1.1", result!.PublicIp);
        Assert.Equal("Starlink", result.Isp);
        Assert.Equal("SpaceX Starlink", result.Organization);
        Assert.Equal("AS14593 Space Exploration Technologies Corporation", result.AsLabel);
        Assert.Equal("Cebu", result.Region);
        Assert.Equal("Starlink", result.DisplayName);
    }

    [Fact]
    public void RejectsIpApiFailureResponse()
    {
        const string json = """{"status":"fail","message":"private range","query":"192.168.1.1"}""";

        Assert.False(AdapterIdentityResponseParser.TryParse(json, out _));
    }

    [Fact]
    public void RejectsResponseWithoutUsefulFields()
    {
        Assert.False(AdapterIdentityResponseParser.TryParse("""{"foo":"bar"}""", out _));
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.False(AdapterIdentityResponseParser.TryParse("not json", out _));
    }

    [Fact]
    public void FallsBackToOrganizationWhenIspMissing()
    {
        const string json = """{"ip":"1.2.3.4","success":true,"connection":{"org":"Converge ICT Solutions"}}""";

        Assert.True(AdapterIdentityResponseParser.TryParse(json, out var result));
        Assert.Equal("Converge ICT Solutions", result!.DisplayName);
    }

    [Theory]
    [InlineData("AS10139 Smart Communications Inc.", "Smart Communications")]
    [InlineData("Dito Telecommunity Corp.", "Dito Telecommunity")]
    [InlineData("  Globe   Telecom , Inc  ", "Globe Telecom")]
    [InlineData("Starlink", "Starlink")]
    [InlineData("AS14593", "AS14593")]
    [InlineData("LLC", "LLC")]
    public void NormalizesProviderNames(string input, string expected)
    {
        Assert.Equal(expected, AdapterIdentityResponseParser.NormalizeProviderName(input));
    }

    [Fact]
    public void NormalizeReturnsNullForBlankInput()
    {
        Assert.Null(AdapterIdentityResponseParser.NormalizeProviderName("   "));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterIdentityResponseParserTests"`
Expected: build error — `AdapterIdentityResponseParser` does not exist.

- [ ] **Step 3: Implement the parser**

```csharp
using System.Globalization;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Parses keyless ISP lookup responses. Handles both the ipwho.is shape (nested "connection"
/// object, "success" flag) and the ip-api.com shape (flat fields, "status" string).
/// </summary>
public static class AdapterIdentityResponseParser
{
    private static readonly string[] CorporateSuffixes =
    [
        "inc.", "inc", "incorporated", "ltd.", "ltd", "limited", "llc", "l.l.c.",
        "corp.", "corp", "corporation", "co.", "co", "company", "s.a.", "sa", "plc", "pte", "pvt"
    ];

    public static bool TryParse(string? json, out AdapterIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (ReadString(root, "status") is { } status &&
                !status.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var connection = root.TryGetProperty("connection", out var connectionElement) &&
                             connectionElement.ValueKind == JsonValueKind.Object
                ? connectionElement
                : default;

            var publicIp = ReadString(root, "ip") ?? ReadString(root, "query");
            var isp = ReadString(connection, "isp") ?? ReadString(root, "isp");
            var organization = ReadString(connection, "org") ?? ReadString(root, "org");
            var asLabel = ReadAsLabel(connection, root);

            if (string.IsNullOrWhiteSpace(publicIp) &&
                string.IsNullOrWhiteSpace(isp) &&
                string.IsNullOrWhiteSpace(organization))
            {
                return false;
            }

            identity = new AdapterIdentity
            {
                PublicIp = publicIp,
                Isp = isp,
                Organization = organization,
                AsLabel = asLabel,
                City = ReadString(root, "city"),
                Region = ReadString(root, "region") ?? ReadString(root, "regionName"),
                Country = ReadString(root, "country"),
                CountryCode = ReadString(root, "country_code") ?? ReadString(root, "countryCode"),
                DisplayName = NormalizeProviderName(isp) ?? NormalizeProviderName(organization)
            };

            return true;
        }
    }

    /// <summary>
    /// Turns a raw provider string into a short display name: drops a leading AS number token and
    /// trailing corporate suffixes, and collapses whitespace. Never returns an empty string.
    /// </summary>
    public static string? NormalizeProviderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var tokens = value
            .Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (tokens.Count == 0)
        {
            return null;
        }

        if (tokens.Count > 1 && IsAsToken(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 1 && CorporateSuffixes.Contains(tokens[^1], StringComparer.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        var normalized = string.Join(' ', tokens).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? value.Trim() : normalized;
    }

    private static bool IsAsToken(string token)
    {
        return token.Length > 2 &&
               token.StartsWith("AS", StringComparison.OrdinalIgnoreCase) &&
               token[2..].All(char.IsDigit);
    }

    private static string? ReadAsLabel(JsonElement connection, JsonElement root)
    {
        if (connection.ValueKind == JsonValueKind.Object &&
            connection.TryGetProperty("asn", out var asn))
        {
            if (asn.ValueKind == JsonValueKind.Number && asn.TryGetInt64(out var asnNumber))
            {
                return $"AS{asnNumber.ToString(CultureInfo.InvariantCulture)}";
            }

            if (asn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(asn.GetString()))
            {
                var raw = asn.GetString()!.Trim();
                return raw.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? raw : $"AS{raw}";
            }
        }

        return ReadString(root, "as");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterIdentityResponseParserTests"`
Expected: PASS (all 13 cases).

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/AdapterIdentityResponseParser.cs XNetwork.Tests/AdapterIdentityResponseParserTests.cs
git commit -m "feat: parse keyless isp lookup responses"
```

---

### Task 4: Name precedence resolver (TDD)

**Files:**
- Create: `XNetwork/Services/AdapterNameResolver.cs`
- Test: `XNetwork.Tests/AdapterNameResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterNameResolverTests
{
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Upstairs modem" };

    private static readonly Dictionary<string, string> IspNames =
        new(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Smart Communications", ["enx1"] = "Starlink" };

    private static readonly Dictionary<string, string> MetadataNames =
        new(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Smart", ["enx1"] = "Wired connection 2", ["enx2"] = "Globe" };

    [Fact]
    public void AliasWinsOverIspName()
    {
        Assert.Equal(
            "Upstairs modem",
            AdapterNameResolver.Resolve("enx0", Aliases, IspNames, MetadataNames, "Path 1"));
    }

    [Fact]
    public void IspNameWinsOverMetadataName()
    {
        Assert.Equal(
            "Starlink",
            AdapterNameResolver.Resolve("enx1", Aliases, IspNames, MetadataNames, "Path 2"));
    }

    [Fact]
    public void FallsBackToMetadataNameWhenNoIspName()
    {
        Assert.Equal(
            "Globe",
            AdapterNameResolver.Resolve("enx2", Aliases, IspNames, MetadataNames, "Path 3"));
    }

    [Fact]
    public void FallsBackToRuntimeNameWhenNothingElseKnown()
    {
        Assert.Equal(
            "Path 4",
            AdapterNameResolver.Resolve("enx9", Aliases, IspNames, MetadataNames, "Path 4"));
    }

    [Fact]
    public void FallsBackToInterfaceNameWhenRuntimeNameBlank()
    {
        Assert.Equal(
            "enx9",
            AdapterNameResolver.Resolve("enx9", Aliases, IspNames, MetadataNames, "   "));
    }

    [Fact]
    public void ReturnsEmptyStringWhenNothingIsKnownAtAll()
    {
        Assert.Equal(
            "",
            AdapterNameResolver.Resolve("", Aliases, IspNames, MetadataNames, null));
    }

    [Fact]
    public void IgnoresBlankDictionaryValues()
    {
        var blankIsp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["enx2"] = "  " };

        Assert.Equal(
            "Globe",
            AdapterNameResolver.Resolve("enx2", Aliases, blankIsp, MetadataNames, "Path 3"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterNameResolverTests"`
Expected: build error — `AdapterNameResolver` does not exist.

- [ ] **Step 3: Implement the resolver**

```csharp
namespace XNetwork.Services;

/// <summary>
/// Resolves the display name for one network adapter. Precedence is manual alias, then the ISP
/// discovered by <see cref="AdapterIdentityService"/>, then NetworkManager/modem metadata, then the
/// runtime path name, then the raw interface name.
/// </summary>
public static class AdapterNameResolver
{
    public static string Resolve(
        string interfaceName,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> ispNames,
        IReadOnlyDictionary<string, string> metadataNames,
        string? runtimeName)
    {
        return Lookup(aliases, interfaceName) ??
               Lookup(ispNames, interfaceName) ??
               Lookup(metadataNames, interfaceName) ??
               Trimmed(runtimeName) ??
               Trimmed(interfaceName) ??
               "";
    }

    private static string? Lookup(IReadOnlyDictionary<string, string>? source, string interfaceName)
    {
        if (source is null || string.IsNullOrWhiteSpace(interfaceName))
        {
            return null;
        }

        return source.TryGetValue(interfaceName, out var value) ? Trimmed(value) : null;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterNameResolverTests"`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/AdapterNameResolver.cs XNetwork.Tests/AdapterNameResolverTests.cs
git commit -m "feat: add adapter name precedence resolver"
```

---

### Task 5: `AdapterIdentityService` (TDD)

The service takes an injectable fetcher delegate `Func<string, string, CancellationToken, Task<string?>>`
(`interfaceName`, `endpoint`) so tests never touch a socket, mirroring
`InterfaceMetadataService`'s `modemProviderFetcher` seam.

**Files:**
- Create: `XNetwork/Services/AdapterIdentityService.cs`
- Test: `XNetwork.Tests/AdapterIdentityServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterIdentityServiceTests
{
    private const string IpWhoIsBody = """
    {"ip":"103.235.93.167","success":true,"city":"Cebu City","country":"Philippines",
     "connection":{"asn":10139,"org":"Smart Communications","isp":"Smart Communications Inc."}}
    """;

    private static AdapterIdentitySettings Settings() => new()
    {
        Enabled = true,
        Endpoints = ["https://ipwho.is/", "http://ip-api.com/json/"],
        SuccessTtlMinutes = 15,
        FailureBackoffSeconds = 120
    };

    [Fact]
    public async Task ResolvesAndCachesIdentityPerInterface()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        var first = await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        var second = await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("Smart Communications", first!.DisplayName);
        Assert.Equal("Smart Communications", second!.DisplayName);
        Assert.Equal("https://ipwho.is/", first.Source);
        Assert.Equal("Smart Communications", service.GetDisplayNames()["enx0"]);
    }

    [Fact]
    public async Task RefetchesAfterSuccessTtlExpires()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(16));
        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RefetchesWhenGatewayChanges()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        await service.RefreshAsync("enx0", "192.168.9.1", CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FallsBackToSecondEndpointWhenFirstFails()
    {
        var endpoints = new List<string>();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, endpoint, _) =>
        {
            endpoints.Add(endpoint);
            return Task.FromResult<string?>(endpoint.Contains("ipwho.is")
                ? null
                : """{"status":"success","query":"1.2.3.4","isp":"Dito Telecommunity Corp."}""");
        });

        var identity = await service.RefreshAsync("enx1", "192.168.4.1", CancellationToken.None);

        Assert.Equal(["https://ipwho.is/", "http://ip-api.com/json/"], endpoints);
        Assert.Equal("Dito Telecommunity", identity!.DisplayName);
        Assert.Equal("http://ip-api.com/json/", identity.Source);
    }

    [Fact]
    public async Task CachesFailureForBackoffWindow()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(null);
        });

        var failed = await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.NotNull(failed!.Error);
        Assert.False(failed.IsAvailable);
        Assert.Empty(service.GetDisplayNames());

        time.Advance(TimeSpan.FromSeconds(121));
        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task SkipsTunnelAndLoopbackInterfaces()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        Assert.Null(await service.RefreshAsync("xbond0", "10.250.0.1", CancellationToken.None));
        Assert.Null(await service.RefreshAsync("tailscale0", "100.64.0.1", CancellationToken.None));
        Assert.Null(await service.RefreshAsync("lo", null, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PerformsNoLookupWhenDisabled()
    {
        var calls = 0;
        var settings = Settings();
        settings.Enabled = false;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(settings, time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        Assert.Null(await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.Empty(service.GetDisplayNames());
    }

    [Fact]
    public async Task ClearingCacheDropsDisplayNames()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) => Task.FromResult<string?>(IpWhoIsBody));

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        Assert.Single(service.GetDisplayNames());

        service.ClearCache();
        Assert.Empty(service.GetDisplayNames());
    }

    [Fact]
    public async Task EvictsInterfacesThatDisappeared()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) => Task.FromResult<string?>(IpWhoIsBody));

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        await service.RefreshAsync("enx1", "192.168.4.1", CancellationToken.None);

        service.EvictMissing(["enx1"]);

        Assert.Null(service.Get("enx0"));
        Assert.NotNull(service.Get("enx1"));
    }

    private static AdapterIdentityService CreateService(
        AdapterIdentitySettings settings,
        TimeProvider timeProvider,
        Func<string, string, CancellationToken, Task<string?>> fetcher) =>
        new(
            NullLogger<AdapterIdentityService>.Instance,
            settings,
            new InterfaceMetadataService(NullLogger<InterfaceMetadataService>.Instance),
            timeProvider,
            fetcher);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterIdentityServiceTests"`
Expected: build error — `AdapterIdentityService` does not exist.

- [ ] **Step 3: Implement the service**

```csharp
using System.Collections.Concurrent;
using System.Net;
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Discovers each adapter's upstream ISP by issuing one interface-bound HTTP request per adapter to
/// a keyless lookup endpoint, so the answer describes that WAN rather than the uLink tunnel.
/// Results are cached with a success TTL and a failure backoff.
/// </summary>
public sealed class AdapterIdentityService : BackgroundService
{
    private readonly ILogger<AdapterIdentityService> _logger;
    private readonly AdapterIdentitySettings _settings;
    private readonly InterfaceMetadataService _interfaceMetadataService;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string, CancellationToken, Task<string?>> _fetcher;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    public AdapterIdentityService(
        ILogger<AdapterIdentityService> logger,
        AdapterIdentitySettings settings,
        InterfaceMetadataService interfaceMetadataService)
        : this(logger, settings, interfaceMetadataService, null, null)
    {
    }

    public AdapterIdentityService(
        ILogger<AdapterIdentityService> logger,
        AdapterIdentitySettings settings,
        InterfaceMetadataService interfaceMetadataService,
        TimeProvider? timeProvider,
        Func<string, string, CancellationToken, Task<string?>>? fetcher)
    {
        _logger = logger;
        _settings = settings;
        _interfaceMetadataService = interfaceMetadataService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fetcher = fetcher ?? FetchBoundAsync;
    }

    public AdapterIdentity? Get(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return null;
        }

        return _cache.TryGetValue(interfaceName, out var entry) ? entry.Identity : null;
    }

    public IReadOnlyDictionary<string, AdapterIdentity> GetAll()
    {
        return _cache.ToDictionary(item => item.Key, item => item.Value.Identity, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Interface to normalized ISP name, for <see cref="AdapterNameResolver"/>.</summary>
    public IReadOnlyDictionary<string, string> GetDisplayNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (device, entry) in _cache)
        {
            if (!string.IsNullOrWhiteSpace(entry.Identity.DisplayName))
            {
                names[device] = entry.Identity.DisplayName!;
            }
        }

        return names;
    }

    public void ClearCache() => _cache.Clear();

    public void EvictMissing(IEnumerable<string> presentInterfaces)
    {
        var present = presentInterfaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _cache.Keys.Where(device => !present.Contains(device)).ToArray())
        {
            _cache.TryRemove(device, out _);
        }
    }

    /// <summary>
    /// Returns the cached identity when it is still fresh, otherwise probes the endpoints in order.
    /// Returns null when the interface must not be probed or lookups are disabled.
    /// </summary>
    public async Task<AdapterIdentity?> RefreshAsync(
        string interfaceName,
        string? gateway,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || ShouldSkip(interfaceName, gateway))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(interfaceName, out var cached) &&
            cached.ExpiresAtUtc > now &&
            string.Equals(cached.Identity.Gateway, gateway, StringComparison.OrdinalIgnoreCase))
        {
            return cached.Identity;
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProbeAsync(interfaceName, gateway, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _probeLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AdapterIdentityService starting (enabled: {Enabled})", _settings.Enabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.Enabled)
                {
                    await RefreshAllAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (!_cache.IsEmpty)
                {
                    ClearCache();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Adapter identity refresh loop error");
            }

            try
            {
                await Task.Delay(_settings.RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var interfaces = await _interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
        var routes = await _interfaceMetadataService.GetDefaultGatewayRoutesAsync(cancellationToken).ConfigureAwait(false);
        var gatewayByDevice = routes
            .GroupBy(route => route.Device, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Gateway, StringComparer.OrdinalIgnoreCase);

        var candidates = interfaces
            .Where(item => item.IsDashboardCandidate)
            .Select(item => item.Device)
            .ToArray();

        EvictMissing(candidates);

        foreach (var device in candidates)
        {
            gatewayByDevice.TryGetValue(device, out var gateway);
            await RefreshAsync(device, gateway, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AdapterIdentity?> ProbeAsync(
        string interfaceName,
        string? gateway,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var errors = new List<string>();

        foreach (var endpoint in _settings.Endpoints.Where(endpoint => !string.IsNullOrWhiteSpace(endpoint)))
        {
            string? body;
            try
            {
                body = await _fetcher(interfaceName, endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{endpoint}: timed out");
                continue;
            }
            catch (Exception ex)
            {
                errors.Add($"{endpoint}: {ex.Message}");
                continue;
            }

            if (!AdapterIdentityResponseParser.TryParse(body, out var parsed) || parsed is null)
            {
                errors.Add($"{endpoint}: no usable answer");
                continue;
            }

            var identity = new AdapterIdentity
            {
                InterfaceName = interfaceName,
                PublicIp = parsed.PublicIp,
                Isp = parsed.Isp,
                Organization = parsed.Organization,
                AsLabel = parsed.AsLabel,
                City = parsed.City,
                Region = parsed.Region,
                Country = parsed.Country,
                CountryCode = parsed.CountryCode,
                DisplayName = parsed.DisplayName,
                Source = endpoint,
                Gateway = gateway,
                UpdatedAtUtc = now
            };

            _cache[interfaceName] = new CacheEntry(identity, now + _settings.SuccessTtl);
            return identity;
        }

        var failure = AdapterIdentity.Unavailable(
            interfaceName,
            errors.Count > 0 ? string.Join("; ", errors) : "No ISP lookup endpoints configured",
            now,
            gateway);
        _cache[interfaceName] = new CacheEntry(failure, now + _settings.FailureBackoff);
        return failure;
    }

    private async Task<string?> FetchBoundAsync(string interfaceName, string endpoint, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_settings.RequestTimeout);

        using var client = InterfaceBoundHttpClientFactory.CreateClient(interfaceName, _settings.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "uLink-adapter-identity/1.0");

        using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private static bool ShouldSkip(string interfaceName, string? gateway)
    {
        if (string.IsNullOrWhiteSpace(interfaceName) ||
            interfaceName.StartsWith("xbond", StringComparison.OrdinalIgnoreCase) ||
            interfaceName.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) ||
            interfaceName.StartsWith("p2p-", StringComparison.OrdinalIgnoreCase) ||
            interfaceName.Equals("lo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(gateway);
    }

    private sealed record CacheEntry(AdapterIdentity Identity, DateTimeOffset ExpiresAtUtc);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterIdentityServiceTests"`
Expected: PASS (9 cases).

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/AdapterIdentityService.cs XNetwork.Tests/AdapterIdentityServiceTests.cs
git commit -m "feat: add per-adapter isp identity lookup service"
```

---

### Task 6: Settings store, DI registration, appsettings

**Files:**
- Create: `XNetwork/Services/AdapterIdentitySettingsStore.cs`
- Modify: `XNetwork/Program.cs`, `XNetwork/appsettings.json`

- [ ] **Step 1: Create the store (mirrors `UiDisplayPreferencesStore`)**

Read `XNetwork/Services/UiDisplayPreferencesStore.cs` first and follow its exact structure: same
`GetAppDataDirectory` helper, same `Load`/`SaveAsync` shape, same logging style. File name
`adapter-identity-settings.json`. Persist only the `Enabled` flag onto the injected settings
instance (endpoints and timings stay host config in `appsettings.json`):

```csharp
public void Load(AdapterIdentitySettings settings)
{
    // read json, if a persisted "Enabled" value exists, assign settings.Enabled
}

public async Task SaveAsync(AdapterIdentitySettings settings, CancellationToken cancellationToken = default)
{
    // write { "Enabled": settings.Enabled } with WriteIndented
}
```

- [ ] **Step 2: Register in `Program.cs`**

Insert immediately after the `InterfaceMetadataService` registration (currently line 63), *before*
`XBondStatsService`:

```csharp
builder.Services.AddSingleton<AdapterIdentitySettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("AdapterIdentity").Get<AdapterIdentitySettings>() ?? new AdapterIdentitySettings();
    sp.GetRequiredService<AdapterIdentitySettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<AdapterIdentityService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdapterIdentityService>());
```

- [ ] **Step 3: Add the `AdapterIdentity` section to `XNetwork/appsettings.json`**

Add as a sibling of `"StarlinkTelemetry"`:

```json
"AdapterIdentity": {
  "Enabled": true,
  "Endpoints": [ "https://ipwho.is/", "http://ip-api.com/json/" ],
  "RefreshIntervalSeconds": 60,
  "SuccessTtlMinutes": 15,
  "FailureBackoffSeconds": 120,
  "RequestTimeoutSeconds": 5
}
```

- [ ] **Step 4: Build**

Run: `dotnet build uLink.sln`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/AdapterIdentitySettingsStore.cs XNetwork/Program.cs XNetwork/appsettings.json
git commit -m "feat: register adapter identity service and settings"
```

---

### Task 7: Use ISP names in `XBondStatsService`

**Files:**
- Modify: `XNetwork/Services/XBondStatsService.cs:10-217`

- [ ] **Step 1: Add the identity dependency and pass names through**

Change the primary constructor to add `AdapterIdentityService adapterIdentityService` as the last
parameter. In `GetSnapshotAsync`, stop pre-folding aliases into `DisplayName` for the name path and
instead build three dictionaries, then pass them to `FromStatus`:

```csharp
public async Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
{
    var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    var interfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
    var gatewayRoutes = await interfaceMetadataService.GetDefaultGatewayRoutesAsync(cancellationToken).ConfigureAwait(false);
    var modemTelemetry = await f50TelemetryService.GetTelemetryByInterfaceAsync(cancellationToken).ConfigureAwait(false);
    var aliases = BuildAliases(interfaces, networkMonitorSettings);
    var ispNames = adapterIdentityService.GetDisplayNames();
    return FromStatus(status, interfaces, modemTelemetry, gatewayRoutes, aliases, ispNames);
}

private static IReadOnlyDictionary<string, string> BuildAliases(
    IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
    NetworkMonitorSettings settings)
{
    var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in interfaces)
    {
        if (NetworkMonitorSettingsStore.GetAdapterAlias(settings, item.Device) is { } alias &&
            !string.IsNullOrWhiteSpace(alias))
        {
            aliases[item.Device] = alias;
        }
    }

    return aliases;
}
```

Keep `ApplyAdapterAliases` in place unchanged — other callers and tests use it.

- [ ] **Step 2: Add the new `FromStatus` overload and thread names into both path branches**

Add a new overload that takes `aliases` and `ispNames`, and have every existing overload delegate to
it with empty dictionaries so current call sites and tests keep compiling:

```csharp
public static XBondStatsSnapshot FromStatus(
    XBondStatus status,
    IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
    IReadOnlyDictionary<string, F50ModemTelemetry> modemTelemetry,
    IReadOnlyList<InterfaceMetadataService.GatewayRoute> gatewayRoutes,
    IReadOnlyDictionary<string, string> aliases,
    IReadOnlyDictionary<string, string> ispNames)
```

Inside, build `metadataNames` from `interfaces` exactly as `interfaceDisplayNames` is built today,
then:

- Configured paths: replace `Name = ResolvePathName(path, interfaceDisplayNames)` with
  `Name = AdapterNameResolver.Resolve(interfaceName, aliases, ispNames, metadataNames, path.Name)`.
- Synthetic local interfaces: replace
  `Name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Device : item.DisplayName` with
  `Name = AdapterNameResolver.Resolve(item.Device, aliases, ispNames, metadataNames, item.DisplayName)`.

Delete `ResolvePathName` once nothing references it. Note `configuredPaths` is a lazy
`IEnumerable`; leave the existing `.ToHashSet()`/`.Concat()` materialization order untouched.

- [ ] **Step 3: Add an ISP-name test to the existing stats tests**

There is no `XBondStatsServiceTests.cs` today; create one:

```csharp
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondStatsServiceTests
{
    [Fact]
    public void UsesIspNameOverMetadataNameAndKeepsAliasOnTop()
    {
        var status = new XBondStatus
        {
            Paths =
            [
                new XBondPathStatus { PathId = 1, InterfaceName = "enx0", Role = "anchor", InterfaceUp = true },
                new XBondPathStatus { PathId = 2, InterfaceName = "enx1", Role = "probe", InterfaceUp = true }
            ]
        };
        var interfaces = new[]
        {
            new InterfaceMetadataService.InterfaceMetadata("enx0", "ethernet", "connected", "Smart", "Smart"),
            new InterfaceMetadataService.InterfaceMetadata("enx1", "ethernet", "connected", "Wired connection 2", "")
        };
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Upstairs modem" };
        var ispNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enx0"] = "Smart Communications",
            ["enx1"] = "Starlink"
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            interfaces,
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            aliases,
            ispNames);

        Assert.Equal("Upstairs modem", snapshot.Paths.Single(path => path.InterfaceName == "enx0").Name);
        Assert.Equal("Starlink", snapshot.Paths.Single(path => path.InterfaceName == "enx1").Name);
    }
}
```

If `XBondStatus`/`XBondPathStatus` property names differ from the above, read
`XNetwork/Models/XBondStatus.cs` and adjust the literal construction — do not change production code
to fit the test.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName!~BrowserSmokeTests"`
Expected: all tests pass, including the new one.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/XBondStatsService.cs XNetwork.Tests/XBondStatsServiceTests.cs
git commit -m "feat: name adapters from discovered isp"
```

---

### Task 8: Bounded per-adapter telemetry history (TDD)

**Files:**
- Create: `XNetwork/Services/AdapterTelemetryHistory.cs`, `XNetwork/Services/AdapterTelemetryHistoryService.cs`
- Test: `XNetwork.Tests/AdapterTelemetryHistoryTests.cs`
- Modify: `XNetwork/Program.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterTelemetryHistoryTests
{
    [Fact]
    public void KeepsSamplesPerInterface()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 10);

        history.Add("enx0", Sample(1, 30));
        history.Add("enx1", Sample(2, 40));
        history.Add("enx0", Sample(3, 50));

        Assert.Equal(2, history.GetSamples("enx0").Count);
        Assert.Single(history.GetSamples("enx1"));
        Assert.Equal(50, history.GetSamples("enx0")[^1].RttMs);
    }

    [Fact]
    public void PrunesOldestSamplesBeyondCapacity()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 3);

        for (var i = 1; i <= 5; i++)
        {
            history.Add("enx0", Sample(i, i * 10));
        }

        var samples = history.GetSamples("enx0");
        Assert.Equal(3, samples.Count);
        Assert.Equal(30, samples[0].RttMs);
        Assert.Equal(50, samples[^1].RttMs);
    }

    [Fact]
    public void ReturnsEmptyForUnknownInterface()
    {
        Assert.Empty(new AdapterTelemetryHistory(maxSamples: 5).GetSamples("nope"));
    }

    [Fact]
    public void IgnoresBlankInterfaceNames()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 5);

        history.Add("  ", Sample(1, 10));

        Assert.Empty(history.GetSamples("  "));
    }

    [Fact]
    public void EvictsInterfacesThatDisappeared()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 5);
        history.Add("enx0", Sample(1, 10));
        history.Add("enx1", Sample(1, 10));

        history.EvictMissing(["enx1"]);

        Assert.Empty(history.GetSamples("enx0"));
        Assert.Single(history.GetSamples("enx1"));
    }

    private static AdapterTelemetrySample Sample(int second, double rttMs) => new()
    {
        TimestampUtc = new DateTimeOffset(2026, 8, 13, 0, 0, second, TimeSpan.Zero),
        RttMs = rttMs,
        LossPercent = 0,
        JitterMs = 1,
        DownloadMbps = 10,
        UploadMbps = 5
    };
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterTelemetryHistoryTests"`
Expected: build error — `AdapterTelemetryHistory` does not exist.

- [ ] **Step 3: Implement the ring**

```csharp
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>Bounded per-interface metric history backing the adapter details charts.</summary>
public sealed class AdapterTelemetryHistory(int maxSamples = 300)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<AdapterTelemetrySample>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSamples = Math.Max(1, maxSamples);

    public void Add(string interfaceName, AdapterTelemetrySample sample)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return;
        }

        lock (_lock)
        {
            if (!_samples.TryGetValue(interfaceName, out var queue))
            {
                queue = new Queue<AdapterTelemetrySample>();
                _samples[interfaceName] = queue;
            }

            queue.Enqueue(sample);
            while (queue.Count > _maxSamples)
            {
                queue.Dequeue();
            }
        }
    }

    public IReadOnlyList<AdapterTelemetrySample> GetSamples(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return [];
        }

        lock (_lock)
        {
            return _samples.TryGetValue(interfaceName, out var queue) ? queue.ToList() : [];
        }
    }

    public void EvictMissing(IEnumerable<string> presentInterfaces)
    {
        var present = presentInterfaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var device in _samples.Keys.Where(device => !present.Contains(device)).ToArray())
            {
                _samples.Remove(device);
            }
        }
    }
}
```

- [ ] **Step 4: Implement the sampler**

```csharp
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Samples uLink path metrics once per second into a bounded per-adapter history so the adapter
/// details sheet can render a populated chart the moment it opens.
/// </summary>
public sealed class AdapterTelemetryHistoryService(
    ILogger<AdapterTelemetryHistoryService> logger,
    XBondSnapshotCache snapshotCache,
    AdapterTelemetryHistory history,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<AdapterTelemetrySample> GetSamples(string interfaceName) => history.GetSamples(interfaceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await snapshotCache.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                var now = _timeProvider.GetUtcNow();

                foreach (var path in snapshot.Paths)
                {
                    history.Add(path.InterfaceName, new AdapterTelemetrySample
                    {
                        TimestampUtc = now,
                        RttMs = path.RttMs,
                        LossPercent = path.DisplayLossPercent,
                        JitterMs = path.JitterMs,
                        DownloadMbps = path.DownloadMbps,
                        UploadMbps = path.UploadMbps
                    });
                }

                history.EvictMissing(snapshot.Paths.Select(path => path.InterfaceName));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Adapter telemetry history sampling error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
```

- [ ] **Step 5: Register in `Program.cs`**

Add after the `XBondSnapshotCache` registration (currently line 66):

```csharp
builder.Services.AddSingleton<AdapterTelemetryHistory>();
builder.Services.AddSingleton<AdapterTelemetryHistoryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdapterTelemetryHistoryService>());
```

- [ ] **Step 6: Run tests**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AdapterTelemetryHistoryTests"`
Expected: PASS (5 cases).

- [ ] **Step 7: Commit**

```bash
git add XNetwork/Services/AdapterTelemetryHistory.cs XNetwork/Services/AdapterTelemetryHistoryService.cs XNetwork.Tests/AdapterTelemetryHistoryTests.cs XNetwork/Program.cs
git commit -m "feat: track bounded per-adapter telemetry history"
```

---

### Task 9: Shared formatters

**Files:**
- Create: `XNetwork/Utils/TelemetryFormatter.cs`
- Modify: `XNetwork/Components/Pages/Home.razor`

- [ ] **Step 1: Create the formatter with the exact bodies currently in `Home.razor`**

Move these verbatim from `Home.razor`'s `@code` block into a `public static class TelemetryFormatter`
in namespace `XNetwork.Utils`, changing `private static` to `public static`:
`FormatLatency`, `FormatPercent`, `FormatNullableMs`, `FormatNullableSpeed`, `FormatNullablePercent`,
`FormatNullableDegrees`, `FormatDuration`, `FormatDropRate`, `FormatObstructionState`,
`FormatGpsState`, `FormatGpsSatellites`, `FormatLastUpdated`, `FormatShortDeviceId`, `FormatUnknown`,
`GetPercentDecimals`.

Do not move `FormatMode`, `FormatDegrees`, or `DownloadTitle` — `FormatMode` is dashboard-specific
and `FormatDegrees` is unused; delete `FormatDegrees` if the compiler reports it unused.

`Home.razor` already has `@using XNetwork.Utils`, so call sites become `TelemetryFormatter.X(...)`.
Delete the moved private copies from `Home.razor` and update every call site in that file.

- [ ] **Step 2: Build**

Run: `dotnet build uLink.sln`
Expected: Build succeeded, no CS0103 (name does not exist) errors.

- [ ] **Step 3: Commit**

```bash
git add XNetwork/Utils/TelemetryFormatter.cs XNetwork/Components/Pages/Home.razor
git commit -m "refactor: share telemetry display formatters"
```

---

### Task 10: Generic telemetry chart component

**Files:**
- Create: `XNetwork/Components/Custom/AdapterTelemetryChart.razor`

- [ ] **Step 1: Create the component**

Copy `XNetwork/Components/Custom/StarlinkTelemetryChart.razor` and change only these points:
- `_chartId` prefix becomes `adapter-chart-`.
- `Samples` is `IReadOnlyList<AdapterTelemetrySample>`.
- `BuildLabels()` uses `sample.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)`.
- `GetMetricValue` maps:

```csharp
private double? GetMetricValue(AdapterTelemetrySample sample)
{
    return Metric switch
    {
        "latency" => sample.RttMs,
        "loss" => sample.LossPercent,
        "jitter" => sample.JitterMs,
        "download" => sample.DownloadMbps,
        "upload" => sample.UploadMbps,
        _ => null
    };
}
```

- `ValueClass` maps `"download" => "text-cyan-200"`, `"upload" => "text-pink-200"`,
  `"loss" => "text-amber-200"`, `_ => "text-white"`.

Everything else (JS module import of `./js/statisticsCharts.js`, `initializeOrUpdateSingleSeriesChart`,
`setSingleSeriesChartData`, `disposeChart`, the render-hash guard, `JSDisconnectedException` handling,
`IAsyncDisposable`) stays identical.

- [ ] **Step 2: Build**

Run: `dotnet build uLink.sln`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add XNetwork/Components/Custom/AdapterTelemetryChart.razor
git commit -m "feat: add generic adapter telemetry chart"
```

---

### Task 11: Extract the Starlink sheet body into a component

Pure move. The rendered Starlink sheet must look identical afterwards.

**Files:**
- Create: `XNetwork/Components/Custom/StarlinkDetailsSection.razor`
- Modify: `XNetwork/Components/Pages/Home.razor`

- [ ] **Step 1: Create the component with the moved markup and helpers**

Move the entire `<div class="space-y-5">…</div>` body of the current `ActionSheet`
(`Home.razor:157-332`, including the `@{ … }` capability block) into the new component. Parameters:

```csharp
[Parameter] public StarlinkTelemetrySnapshot Snapshot { get; set; } = StarlinkTelemetrySnapshot.Unavailable();
[Parameter] public IReadOnlyList<StarlinkTelemetrySnapshot> History { get; set; } = Array.Empty<StarlinkTelemetrySnapshot>();
[Parameter] public StarlinkCapabilitySnapshot Capabilities { get; set; } = new();
[Parameter] public string ManagementHost { get; set; } = "";
[Parameter] public StarlinkCommandResult? LastCommandResult { get; set; }
[Parameter] public bool CommandInProgress { get; set; }
[Parameter] public StarlinkCapability? PendingCommand { get; set; }
[Parameter] public EventCallback<StarlinkCapability> OnExecuteCommand { get; set; }
```

Move these private statics from `Home.razor` into this component unchanged:
`IsStarlinkCommandVisible`, `GetUniqueStarlinkOpenCapabilities`, `GetStarlinkOpenActionLabel`,
`GetStarlinkCommandShortDescription`, `GetStarlinkCommandSlideLabel`, `GetStarlinkCommandBusyLabel`,
`GetStarlinkCommandTone`, `GetStarlinkCommandIconClass`, `GetStarlinkActionCardClass`,
`GetStarlinkActionIconClass`, `GetStarlinkCommandResultClass`, `GetStarlinkAlertLabel`,
`GetStarlinkAlertDescription`, and `IsStarlinkCommandBusy` (rewritten to use the parameters:
`CommandInProgress && PendingCommand?.DirectCommand == capability.DirectCommand`).

Replace direct service reads with the parameters: `Snapshot`, `History`, `Capabilities`,
`ManagementHost` instead of `StarlinkTelemetryService.GetSnapshot()` / `GetHistory()` /
`GetCapabilities()` / `StarlinkTelemetrySettings.Host`. Replace
`OnConfirmed="() => ExecuteStarlinkCommandAsync(capability)"` with
`OnConfirmed="() => OnExecuteCommand.InvokeAsync(capability)"`. Formatter calls become
`TelemetryFormatter.X(...)`; add `@using XNetwork.Utils`, `@using XNetwork.Models`.

Keep `GetStarlinkAlertLabel`/`GetStarlinkAlertDescription` in `Home.razor` too — the card's inline
telemetry strip still uses them. Duplication of two small switch expressions is acceptable here; if
the compiler flags them unused in `Home.razor`, delete them there instead.

- [ ] **Step 2: Delete the moved markup and helpers from `Home.razor`**

Remove the old `ActionSheet` block entirely (it is replaced in Task 12) and every helper listed in
Step 1 that is no longer referenced by `Home.razor`.

- [ ] **Step 3: Build**

Run: `dotnet build uLink.sln`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add XNetwork/Components/Custom/StarlinkDetailsSection.razor XNetwork/Components/Pages/Home.razor
git commit -m "refactor: extract starlink details section component"
```

---

### Task 12: Generic `AdapterDetailsSheet` and Home rewiring

**Files:**
- Create: `XNetwork/Components/Custom/AdapterDetailsSheet.razor`
- Modify: `XNetwork/Components/Pages/Home.razor`

- [ ] **Step 1: Create `AdapterDetailsSheet.razor`**

Parameters:

```csharp
[Parameter] public bool IsOpen { get; set; }
[Parameter] public XBondPathStatsSnapshot? Path { get; set; }
[Parameter] public AdapterIdentity? Identity { get; set; }
[Parameter] public IReadOnlyList<AdapterTelemetrySample> Samples { get; set; } = Array.Empty<AdapterTelemetrySample>();
[Parameter] public string? AdminUrl { get; set; }
[Parameter] public bool IdentityLookupEnabled { get; set; } = true;
[Parameter] public bool IsRefreshingIdentity { get; set; }
[Parameter] public bool ShowTechnicalDetails { get; set; }
[Parameter] public EventCallback OnClose { get; set; }
[Parameter] public EventCallback OnRefreshIdentity { get; set; }
[Parameter] public RenderFragment? ExtraSection { get; set; }
```

Body: an `<ActionSheet>` with `IsOpen="IsOpen"`, `Title="@(Path?.Name)"`,
`Description="@BuildDescription()"` (`"{Path.InterfaceName} - {RoleLabel} path"`),
`CloseLabel="Close adapter details"`, `MaxWidthClass="md:max-w-5xl"`, `OnClose="OnClose"`, containing
in order:

1. **Live metric tiles** — `grid grid-cols-2 gap-3 md:grid-cols-4`, reusing the existing tile classes
   `rounded-xl border border-slate-700 bg-slate-950/50 p-3`: Latency
   (`TelemetryFormatter.FormatLatency(Path.RttMs)`), Loss
   (`TelemetryFormatter.FormatPercent(Path.DisplayLossPercent)`), Download and Upload
   (`SpeedFormatter.FormatSpeed(...)`), each with a `text-xs text-slate-500` subline
   (`Jitter`, `Late`, `Useful`, `Role`).
2. **Charts** — `grid grid-cols-1 gap-3 md:grid-cols-2` of four `AdapterTelemetryChart`:
   `Metric="latency" Unit="ms" Stroke="#d8d8d8"`, `Metric="loss" Unit="%" Stroke="#f59e0b"`,
   `Metric="download" Unit="Mbps" Stroke="#38bdf8"`, `Metric="upload" Unit="Mbps" Stroke="#f472b6"`,
   each with `Samples="Samples"`.
3. **Identity section** — bordered card titled `Upstream`, with a `grid grid-cols-1 gap-3 md:grid-cols-3`
   of label/value pairs: Provider (`Identity.DisplayName`), Public IP, ISP (raw), Organization,
   AS (`Identity.AsLabel`), Location (`City`, `Region`, `Country` joined with `", "`, blanks dropped),
   Gateway (`Path.Gateway`), Source (`Identity.Source`), Updated
   (`TelemetryFormatter.FormatLastUpdated(Identity.UpdatedAtUtc)`). Use
   `TelemetryFormatter.FormatUnknown(...)` for every value.
   - When `!IdentityLookupEnabled`: show
     `ISP lookups are disabled in Settings.` in an amber note box
     (`rounded-xl border border-amber-500/25 bg-amber-500/10 p-3 text-sm text-amber-100`).
   - When `Identity is null || !Identity.IsAvailable`: show `ISP unavailable` plus
     `@Identity?.Error` in the same amber note box.
4. **Cellular section** — rendered only `@if (Path.HasCellularTelemetry)`: generation pill
   (`rounded-full border border-cyan-400/25 bg-cyan-400/10 px-1.5 py-0.5 text-cyan-100`) and
   `@Path.CellularSignalBars / 5 bars`.
5. **Actions** — a flex row: `<a>` to `AdminUrl` when non-empty, styled like the existing card admin
   link (`border-slate-600/50 bg-slate-700/50`, `fas fa-arrow-up-right-from-square`, label
   `Open admin page`), and a `<button type="button">` bound to `OnRefreshIdentity` labelled
   `Refresh identity` / `Refreshing...` when `IsRefreshingIdentity`, `disabled="@(IsRefreshingIdentity || !IdentityLookupEnabled)"`.
6. **Technical details** — rendered only `@if (ShowTechnicalDetails)`: `Role`, `Score`
   (`Math.Round(Path.Score)`), `Late` (`TelemetryFormatter.FormatPercent(Path.LatePercent)`),
   `Queue` (`Path.QueueDepth`), `Heartbeat samples` (`Path.HeartbeatSampleCount`),
   `Bind` (`Path.BindAddress`), `Socket generation` (`Path.SocketGeneration`),
   `Last rebind` (`TelemetryFormatter.FormatUnknown(Path.LastRebindReason)`),
   `State` (`Path.StateText`).
7. `@ExtraSection` last, so the Starlink dish section appears below the generic content.

Guard the whole body with `@if (Path is not null)`.

- [ ] **Step 2: Rewire `Home.razor`**

- Add `@inject AdapterIdentityService AdapterIdentityService` and
  `@inject AdapterTelemetryHistoryService AdapterTelemetryHistoryService` and
  `@inject AdapterIdentitySettings AdapterIdentitySettings`.
- Rename `_selectedStarlinkPath` to `_selectedPath` and `_showStarlinkDetails` to `_showAdapterDetails`.
- `OnPathCardClick` drops the Starlink guard:

```csharp
private void OnPathCardClick(XBondPathStatsSnapshot path)
{
    _selectedPath = path;
    _showAdapterDetails = true;
}
```

- `GetPathCardClass`: replace the conditional `interactionClass` with the unconditional
  `const string interactionClass = " cursor-pointer hover:bg-slate-700/30 focus-within:ring-2 focus-within:ring-slate-400/40";`
- Refresh the selected path each tick so the open sheet stays live. At the end of `RefreshAsync`, before
  `StateHasChanged()`:

```csharp
if (_selectedPath is not null)
{
    _selectedPath = _snapshot.Paths.FirstOrDefault(path => path.PathId == _selectedPath.PathId) ?? _selectedPath;
}
```

- Replace the removed `ActionSheet` with:

```razor
<AdapterDetailsSheet IsOpen="_showAdapterDetails"
                     Path="_selectedPath"
                     Identity="@(_selectedPath is null ? null : AdapterIdentityService.Get(_selectedPath.InterfaceName))"
                     Samples="@(_selectedPath is null ? Array.Empty<AdapterTelemetrySample>() : AdapterTelemetryHistoryService.GetSamples(_selectedPath.InterfaceName))"
                     AdminUrl="@(_selectedPath is null ? null : GetAdapterAdminUrl(_selectedPath))"
                     IdentityLookupEnabled="AdapterIdentitySettings.Enabled"
                     IsRefreshingIdentity="_identityRefreshInProgress"
                     ShowTechnicalDetails="UiDisplayPreferences.AdapterTechnicalDetails"
                     OnClose="CloseAdapterDetailsAsync"
                     OnRefreshIdentity="RefreshSelectedIdentityAsync">
    @if (_selectedPath is not null && IsStarlinkPath(_selectedPath))
    {
        <StarlinkDetailsSection Snapshot="StarlinkTelemetryService.GetSnapshot()"
                                History="StarlinkTelemetryService.GetHistory()"
                                Capabilities="StarlinkTelemetryService.GetCapabilities()"
                                ManagementHost="@StarlinkTelemetrySettings.Host"
                                LastCommandResult="_lastStarlinkCommandResult"
                                CommandInProgress="_starlinkCommandInProgress"
                                PendingCommand="_pendingStarlinkCommand"
                                OnExecuteCommand="ExecuteStarlinkCommandAsync" />
    }
</AdapterDetailsSheet>
```

- `GetAdapterAdminUrl` currently returns null for Starlink paths; keep that behavior.
- Add the close and refresh handlers:

```csharp
private Task CloseAdapterDetailsAsync()
{
    _showAdapterDetails = false;
    _pendingStarlinkCommand = null;
    return Task.CompletedTask;
}

private async Task RefreshSelectedIdentityAsync()
{
    if (_selectedPath is null || _identityRefreshInProgress)
    {
        return;
    }

    _identityRefreshInProgress = true;
    await InvokeAsync(StateHasChanged);

    try
    {
        AdapterIdentityService.Invalidate(_selectedPath.InterfaceName);
        await AdapterIdentityService.RefreshAsync(_selectedPath.InterfaceName, _selectedPath.Gateway, _refreshCts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        _identityRefreshInProgress = false;
        await InvokeAsync(StateHasChanged);
    }
}
```

- Add the field `private bool _identityRefreshInProgress;`.
- Delete `GetStarlinkSheetTitle` and `GetStarlinkSheetDescription`.

- [ ] **Step 3: Add `Invalidate` to `AdapterIdentityService`**

`RefreshSelectedIdentityAsync` must bypass the TTL, so add:

```csharp
public void Invalidate(string interfaceName)
{
    if (!string.IsNullOrWhiteSpace(interfaceName))
    {
        _cache.TryRemove(interfaceName, out _);
    }
}
```

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build uLink.sln`
Expected: Build succeeded.

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName!~BrowserSmokeTests"`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Components/Custom/AdapterDetailsSheet.razor XNetwork/Components/Pages/Home.razor XNetwork/Services/AdapterIdentityService.cs
git commit -m "feat: open a details sheet for every adapter"
```

---

### Task 13: Settings toggle

**Files:**
- Modify: `XNetwork/Components/Pages/Settings.razor`

- [ ] **Step 1: Add the toggle inside the Link Watchdog accordion's adapter area**

The adapter alias table lives at `XNetwork/Components/Pages/Settings.razor:995-1005`. Immediately
above the `Save Link Watchdog` button (line 1037), add:

```razor
<div class="flex items-start justify-between gap-4 rounded-lg border border-slate-700 bg-slate-900/40 p-3">
    <div>
        <p class="text-sm font-semibold text-white">Name adapters from their ISP</p>
        <p class="mt-1 text-xs text-slate-400">
            Looks up each adapter's provider through its own connection. Manual aliases above always win.
        </p>
    </div>
    <input type="checkbox" class="h-5 w-5 accent-cyan-400" @bind="_adapterIdentityEnabled" />
</div>
```

- [ ] **Step 2: Wire it into the existing save path**

Add `@inject AdapterIdentitySettings AdapterIdentitySettings` and
`@inject AdapterIdentitySettingsStore AdapterIdentitySettingsStore` and
`@inject AdapterIdentityService AdapterIdentityService`.

Add the field `private bool _adapterIdentityEnabled;`, initialize it from
`AdapterIdentitySettings.Enabled` wherever the Link Watchdog draft is loaded, and in
`SaveLinkWatchdogSettingsAsync` (line 2027 area), before the success message:

```csharp
if (AdapterIdentitySettings.Enabled != _adapterIdentityEnabled)
{
    AdapterIdentitySettings.Enabled = _adapterIdentityEnabled;
    await AdapterIdentitySettingsStore.SaveAsync(AdapterIdentitySettings, _refreshCts.Token);
    if (!_adapterIdentityEnabled)
    {
        AdapterIdentityService.ClearCache();
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build uLink.sln`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add XNetwork/Components/Pages/Settings.razor
git commit -m "feat: add isp naming toggle to settings"
```

---

### Task 14: Version bump, changelog, journal

**Files:**
- Modify: `XNetwork/Models/AppChangelog.cs`, `AGENTS.md`

- [ ] **Step 1: Bump the version and add the changelog entry**

`AppChangelog.CurrentVersion` is `ulink-2026.06.122`. Set it to `ulink-2026.06.123` and add a matching
entry at the top of the changelog list, following the shape of the existing neighbouring entries, with
text covering: every adapter card now opens a details sheet with live latency/loss/throughput charts
and upstream details, and adapter names now follow the detected ISP unless a manual alias is set.

- [ ] **Step 2: Verify the changelog test still passes**

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName~AppChangelogTests"`
Expected: PASS.

- [ ] **Step 3: Add an `AGENTS.md` journal entry**

Add a dated bullet under `## XBond-Only Branch` recording: the new `AdapterIdentityService`
interface-bound ISP lookups (ipwho.is then ip-api.com, 15-minute TTL, 2-minute failure backoff),
the name precedence (alias > ISP > NetworkManager/modem > runtime name > interface), the generic
`AdapterDetailsSheet` replacing the Starlink-only sheet, and the Settings toggle.

- [ ] **Step 4: Commit**

```bash
git add XNetwork/Models/AppChangelog.cs AGENTS.md
git commit -m "docs: release ulink-2026.06.123"
```

---

### Task 15: Full verification and deploy

- [ ] **Step 1: Full local verification**

Run: `dotnet build uLink.sln`
Expected: Build succeeded, 0 errors.

Run: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj --filter "FullyQualifiedName!~BrowserSmokeTests"`
Expected: all tests pass. Record the count.

If `obj` file collisions appear, run `dotnet build-server shutdown` and rerun sequentially.

- [ ] **Step 2: Push the branch**

```bash
git push origin feature/xband-only-runtime
```

- [ ] **Step 3: Deploy (app-only change, so the router-side script)**

```bash
ssh -i "C:\Users\Xeon\.ssh\speedifyui_cli_probe" xeon-network@xeon-network "cd /home/xeon-network/xnetwork; ./deploy.sh"
```

Expected: `Publishing XNetwork...` then `Deployment complete.` with `xnetwork.service` active.

- [ ] **Step 4: Verify on the router**

```bash
ssh -i "C:\Users\Xeon\.ssh\speedifyui_cli_probe" xeon-network@xeon-network "systemctl is-active xnetwork.service; cat /home/xeon-network/xnetwork/XNetwork/bin/Release/net9.0/publish/build-info.json; for p in / /details /xbond /settings /xrouter; do curl -sS -o /dev/null -w \"\$p %{http_code}\n\" http://127.0.0.1:8080\$p; done"
```

Expected: `active`, `build-info.json` reporting `ulink-2026.06.123` and the new commit, and `200` for
every route.

- [ ] **Step 5: Verify ISP names resolved live**

```bash
ssh -i "C:\Users\Xeon\.ssh\speedifyui_cli_probe" xeon-network@xeon-network "journalctl -u xnetwork.service --since '-3 min' --no-pager | grep -i 'AdapterIdentityService' | tail -5"
```

Then load `http://100.112.183.104:8080/` in a browser, confirm each adapter card shows a provider name
and opens its sheet, and confirm the sheet shows charts plus a public IP and ISP for a non-Starlink
adapter.

- [ ] **Step 6: Record the outcome in `AGENTS.md` and commit**

Add the deploy verification result (version, commit, route status, observed adapter names) as a dated
bullet, then:

```bash
git add AGENTS.md
git commit -m "docs: record ulink-2026.06.123 deploy verification"
git push origin feature/xband-only-runtime
```
