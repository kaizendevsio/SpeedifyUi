# Ping-Based Connection Health Check System - Design Document

## Document Information
- **Date:** October 28, 2025
- **Purpose:** Design specification for replacing packet loss-based health checks with latency-based ping monitoring
- **Target Platform:** Windows 11
- **Target:** 1.1.1.1 (Cloudflare DNS)

---

## 1. Current Implementation Overview

### 1.1 Architecture
The current health check system (`ConnectionHealthService`) is a `BackgroundService` that:
- Streams real-time connection statistics from Speedify CLI
- Monitors multiple network adapters simultaneously
- Calculates health metrics using a rolling window of samples
- Provides thread-safe access to health data

### 1.2 Data Flow
```
SpeedifyService.GetStatsAsync()
    → ConnectionItem (from speedify_cli stats)
        → ConnectionHealthService.ProcessConnectionSnapshot()
            → HealthSnapshot (stored in CircularBuffer)
                → CalculateMetrics()
                    → HealthMetrics
                        → UpdateOverallHealth()
                            → ConnectionHealth
```

### 1.3 Packet Loss Calculation
**Location:** [`ConnectionHealthService.cs:186`](XNetwork/Services/ConnectionHealthService.cs:186)

```csharp
// Calculate average packet loss from send and receive loss
var packetLoss = (connection.LossSend + connection.LossReceive) / 2.0;
```

**Source Data:**
- `connection.LossSend`: Packet loss percentage on send path (0-1 scale)
- `connection.LossReceive`: Packet loss percentage on receive path (0-1 scale)
- Averaged to get overall packet loss percentage

### 1.4 Current Data Structures

#### CircularBuffer<T>
- **Purpose:** Thread-safe fixed-size rolling window storage
- **Capacity:** 10 samples per adapter
- **Operations:** Add(), GetItems(), Clear(), GetStats()
- **Thread Safety:** Uses internal lock for all operations
- **Location:** [`XNetwork/Utils/CircularBuffer.cs`](XNetwork/Utils/CircularBuffer.cs)

#### HealthSnapshot
- **Purpose:** Point-in-time measurement record
- **Fields:**
  - `Timestamp` (DateTime)
  - `Latency` (double, ms)
  - `PacketLoss` (double, percentage)
  - `Speed` (double, Mbps)
- **Location:** [`XNetwork/Models/HealthSnapshot.cs`](XNetwork/Models/HealthSnapshot.cs)

#### HealthMetrics
- **Purpose:** Aggregated health metrics over rolling window
- **Fields:**
  - `AverageLatency`, `MinLatency`, `MaxLatency`
  - `AveragePacketLoss`
  - `AverageSpeed`
  - `LatencyStdDev` (standard deviation)
  - `StabilityScore` (0-1, inverse of coefficient of variation)
  - `SampleCount`
  - `Status` (ConnectionStatus enum)
- **Location:** [`XNetwork/Models/HealthMetrics.cs`](XNetwork/Models/HealthMetrics.cs)

#### ConnectionHealth
- **Purpose:** Overall health assessment across all adapters
- **Thread Safety:** Uses internal lock for all property access
- **Fields:** Similar to HealthMetrics but aggregated across adapters
- **Location:** [`XNetwork/Models/ConnectionHealth.cs`](XNetwork/Models/ConnectionHealth.cs)

### 1.5 Health Status Determination

**Location:** [`ConnectionHealthService.cs:309-348`](XNetwork/Services/ConnectionHealthService.cs:309-348)

**Current Thresholds:**
```csharp
// Excellent
Latency < 150ms, PacketLoss < 3%, Speed > 40 Mbps

// Good
Latency < 250ms, PacketLoss < 7%, Speed > 15 Mbps

// Fair
Latency < 400ms, PacketLoss < 12%, Speed > 5 Mbps

// Poor
Latency < 600ms, PacketLoss < 20%, Speed > 1 Mbps

// Critical
Above poor thresholds
```

**Logic:**
1. Critical conditions checked first (latency > 600ms OR packet loss > 20%)
2. Low throughput scenarios (<5 Mbps) prioritize latency/packet loss over speed
3. Returns worst status when multiple adapters are active

### 1.6 Configuration Constants
```csharp
BUFFER_SIZE = 10                        // Samples per adapter
MIN_SAMPLES_FOR_HEALTH = 3              // Minimum before reporting
STALE_ADAPTER_TIMEOUT_MINUTES = 5       // Cleanup timeout
CLEANUP_INTERVAL_SECONDS = 60           // Cleanup frequency
```

---

## 2. Requirements for New Ping-Based System

### 2.1 Functional Requirements
1. **Ping Target:** Continuously ping 1.1.1.1 (Cloudflare DNS)
2. **Time Window:** Track latency measurements over a 5-second rolling window
3. **Health Metric:** Determine connection health based ONLY on latency (remove packet loss dependency)
4. **Platform:** Must work reliably on Windows 11
5. **Performance:** Maintain similar characteristics to current implementation
6. **Compatibility:** Preserve existing public interface (`IConnectionHealthService`)

### 2.2 Non-Functional Requirements
1. **Responsiveness:** Health status should update within 1 second of significant latency changes
2. **Reliability:** Handle timeout scenarios gracefully (treat as failed pings)
3. **Resource Efficiency:** Minimize CPU and network overhead
4. **Thread Safety:** All public methods must be thread-safe
5. **Initialization:** Clear indication when service has enough data to report

---

## 3. Proposed Architecture

### 3.1 New Components

#### 3.1.1 PingHealthService (replaces ConnectionHealthService)
```
PingHealthService : BackgroundService, IConnectionHealthService
├── Private Fields
│   ├── _logger : ILogger<PingHealthService>
│   ├── _pingBuffer : CircularBuffer<PingSnapshot>
│   ├── _overallHealth : ConnectionHealth
│   └── _initializationLock : SemaphoreSlim
├── Configuration
│   ├── PING_TARGET = "1.1.1.1"
│   ├── PING_INTERVAL_MS = 500          // 2 pings/sec
│   ├── PING_TIMEOUT_MS = 3000          // 3 second timeout
│   ├── BUFFER_SIZE = 10                // 10 pings in 5 seconds
│   ├── MIN_SAMPLES_FOR_HEALTH = 3
│   └── FAILED_PING_LATENCY = 9999.0    // Sentinel for timeouts
└── Methods
    ├── ExecuteAsync(CancellationToken)
    ├── RunPingLoopAsync(CancellationToken)
    ├── SendPingAsync() : Task<PingSnapshot>
    ├── ProcessPingSnapshot(PingSnapshot)
    ├── CalculateMetrics() : HealthMetrics
    └── DetermineConnectionStatus(HealthMetrics) : ConnectionStatus
```

#### 3.1.2 PingSnapshot (new model)
```csharp
public class PingSnapshot
{
    public DateTime Timestamp { get; init; }
    public double Latency { get; init; }      // ms (or 9999 for timeout)
    public bool IsSuccessful { get; init; }   // false if timeout
    public string Status { get; init; }       // "Success" or "Timeout"
}
```

#### 3.1.3 PingHealthMetrics (extends HealthMetrics)
```csharp
public class PingHealthMetrics : HealthMetrics
{
    public int SuccessfulPings { get; init; }
    public int FailedPings { get; init; }
    public double SuccessRate { get; init; }  // percentage
    public double Jitter { get; init; }       // latency variation (stddev)
}
```

### 3.2 Architecture Diagram

```
┌─────────────────────────────────────────────────────────┐
│                  PingHealthService                       │
│                  (BackgroundService)                     │
├─────────────────────────────────────────────────────────┤
│  ExecuteAsync()                                          │
│    └─> RunPingLoopAsync()                               │
│         ├─> SendPingAsync()                             │
│         │    └─> System.Net.NetworkInformation.Ping     │
│         │         └─> 1.1.1.1                           │
│         ├─> ProcessPingSnapshot()                       │
│         │    └─> CircularBuffer<PingSnapshot>.Add()    │
│         └─> CalculateMetrics()                          │
│              └─> DetermineConnectionStatus()            │
│                   └─> UpdateOverallHealth()             │
└─────────────────────────────────────────────────────────┘
         │                          │
         ↓                          ↓
┌──────────────────┐    ┌─────────────────────────┐
│ CircularBuffer   │    │  ConnectionHealth       │
│ <PingSnapshot>   │    │  (thread-safe)          │
│  - Size: 10      │    │  - Status               │
│  - 5s window     │    │  - AverageLatency       │
└──────────────────┘    │  - Jitter               │
                        │  - SuccessRate          │
                        └─────────────────────────┘
                                 │
                                 ↓
                        ┌─────────────────────┐
                        │ IConnectionHealth   │
                        │ Service (public)    │
                        │  - GetOverallHealth │
                        │  - IsInitialized    │
                        └─────────────────────┘
```

---

## 4. Latency-Based Health Thresholds

### 4.1 Proposed Thresholds

Based on analysis of the current system and typical network latency expectations:

```csharp
private static class PingThresholds
{
    // Excellent - Gaming/VoIP quality
    public const double EXCELLENT_LATENCY = 30;       // < 30ms
    public const double EXCELLENT_JITTER = 5;         // < 5ms variation
    public const double EXCELLENT_SUCCESS_RATE = 98;  // > 98% success

    // Good - Normal browsing/streaming
    public const double GOOD_LATENCY = 80;            // < 80ms
    public const double GOOD_JITTER = 15;             // < 15ms variation
    public const double GOOD_SUCCESS_RATE = 95;       // > 95% success

    // Fair - Acceptable for most uses
    public const double FAIR_LATENCY = 150;           // < 150ms
    public const double FAIR_JITTER = 30;             // < 30ms variation
    public const double FAIR_SUCCESS_RATE = 90;       // > 90% success

    // Poor - Degraded experience
    public const double POOR_LATENCY = 300;           // < 300ms
    public const double POOR_JITTER = 60;             // < 60ms variation
    public const double POOR_SUCCESS_RATE = 80;       // > 80% success

    // Critical - Severe issues
    // Anything above poor thresholds
}
```

### 4.2 Threshold Rationale

**Latency Ranges:**
- **< 30ms:** Excellent - Suitable for gaming, real-time communication
- **30-80ms:** Good - Normal for most internet activities
- **80-150ms:** Fair - Noticeable but acceptable delay
- **150-300ms:** Poor - Sluggish response, video call issues
- **> 300ms:** Critical - Severe degradation

**Jitter (Latency Variation):**
- Low jitter = stable connection
- High jitter = unpredictable response times
- Calculated as standard deviation of latency samples

**Success Rate:**
- Accounts for ping timeouts
- More important metric than current packet loss (which measures data loss)
- Failed pings indicate connectivity problems

### 4.3 Health Determination Logic

```csharp
private ConnectionStatus DetermineConnectionStatus(PingHealthMetrics metrics)
{
    // Critical: Multiple severe indicators
    if (metrics.AverageLatency > PingThresholds.POOR_LATENCY ||
        metrics.Jitter > PingThresholds.POOR_JITTER ||
        metrics.SuccessRate < PingThresholds.POOR_SUCCESS_RATE)
    {
        return ConnectionStatus.Critical;
    }

    // Poor: One or more indicators in poor range
    if (metrics.AverageLatency > PingThresholds.FAIR_LATENCY ||
        metrics.Jitter > PingThresholds.FAIR_JITTER ||
        metrics.SuccessRate < PingThresholds.FAIR_SUCCESS_RATE)
    {
        return ConnectionStatus.Poor;
    }

    // Fair: Average performance
    if (metrics.AverageLatency > PingThresholds.GOOD_LATENCY ||
        metrics.Jitter > PingThresholds.GOOD_JITTER ||
        metrics.SuccessRate < PingThresholds.GOOD_SUCCESS_RATE)
    {
        return ConnectionStatus.Fair;
    }

    // Good: Better than average
    if (metrics.AverageLatency > PingThresholds.EXCELLENT_LATENCY ||
        metrics.Jitter > PingThresholds.EXCELLENT_JITTER ||
        metrics.SuccessRate < PingThresholds.EXCELLENT_SUCCESS_RATE)
    {
        return ConnectionStatus.Good;
    }

    // Excellent: Optimal performance
    return ConnectionStatus.Excellent;
}
```

---

## 5. Implementation Approach

### 5.1 Phase 1: Data Models (No Breaking Changes)

**Step 1.1: Create PingSnapshot Model**
```csharp
// XNetwork/Models/PingSnapshot.cs
namespace XNetwork.Models;

public class PingSnapshot
{
    public DateTime Timestamp { get; init; }
    public double Latency { get; init; }
    public bool IsSuccessful { get; init; }
    public string Status { get; init; }

    public PingSnapshot(double latency, bool isSuccessful)
    {
        Timestamp = DateTime.UtcNow;
        Latency = latency;
        IsSuccessful = isSuccessful;
        Status = isSuccessful ? "Success" : "Timeout";
    }
}
```

**Step 1.2: Extend ConnectionHealth for Ping Metrics**
```csharp
// Add to XNetwork/Models/ConnectionHealth.cs
public double Jitter { get; set; }
public double SuccessRate { get; set; }
public int SuccessfulPings { get; set; }
public int FailedPings { get; set; }
```

### 5.2 Phase 2: PingHealthService Implementation

**Step 2.1: Create Service Skeleton**
```csharp
// XNetwork/Services/PingHealthService.cs
public class PingHealthService : BackgroundService, IConnectionHealthService
{
    private const string PING_TARGET = "1.1.1.1";
    private const int PING_INTERVAL_MS = 500;      // 2 pings/sec
    private const int PING_TIMEOUT_MS = 3000;      // 3s timeout
    private const int BUFFER_SIZE = 10;            // 10 samples = 5s window
    private const double FAILED_PING_LATENCY = 9999.0;

    private readonly ILogger<PingHealthService> _logger;
    private readonly CircularBuffer<PingSnapshot> _pingBuffer;
    private readonly ConnectionHealth _overallHealth;
    private readonly System.Net.NetworkInformation.Ping _ping;

    // ... constructor, methods
}
```

**Step 2.2: Core Ping Loop**
```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _logger.LogInformation("PingHealthService starting");

    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            var snapshot = await SendPingAsync(stoppingToken);
            ProcessPingSnapshot(snapshot);

            await Task.Delay(PING_INTERVAL_MS, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ping loop");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    _logger.LogInformation("PingHealthService stopped");
}
```

**Step 2.3: Windows Ping Implementation**
```csharp
private async Task<PingSnapshot> SendPingAsync(CancellationToken cancellationToken)
{
    try
    {
        var reply = await _ping.SendPingAsync(
            PING_TARGET,
            PING_TIMEOUT_MS,
            buffer: new byte[32], // Standard 32-byte buffer
            options: new PingOptions(ttl: 128, dontFragment: true)
        );

        if (reply.Status == IPStatus.Success)
        {
            return new PingSnapshot(
                latency: reply.RoundtripTime,
                isSuccessful: true
            );
        }
        else
        {
            _logger.LogWarning("Ping to {Target} failed: {Status}",
                PING_TARGET, reply.Status);
            return new PingSnapshot(
                latency: FAILED_PING_LATENCY,
                isSuccessful: false
            );
        }
    }
    catch (PingException ex)
    {
        _logger.LogWarning(ex, "Ping exception for {Target}", PING_TARGET);
        return new PingSnapshot(
            latency: FAILED_PING_LATENCY,
            isSuccessful: false
        );
    }
}
```

**Step 2.4: Metrics Calculation**
```csharp
private void ProcessPingSnapshot(PingSnapshot snapshot)
{
    _pingBuffer.Add(snapshot);

    var snapshots = _pingBuffer.GetItems();
    if (snapshots.Length < MIN_SAMPLES_FOR_HEALTH)
        return;

    // Calculate success rate
    var successfulPings = snapshots.Count(s => s.IsSuccessful);
    var failedPings = snapshots.Length - successfulPings;
    var successRate = (double)successfulPings / snapshots.Length * 100.0;

    // Calculate latency stats (only for successful pings)
    var successfulLatencies = snapshots
        .Where(s => s.IsSuccessful)
        .Select(s => s.Latency)
        .ToArray();

    if (successfulLatencies.Length == 0)
    {
        // All pings failed
        _overallHealth.UpdateMetrics(
            ConnectionStatus.Critical,
            latency: FAILED_PING_LATENCY,
            jitter: 0,
            successRate: 0,
            successfulPings: 0,
            failedPings: snapshots.Length,
            samples: snapshots.Length
        );
        return;
    }

    var avgLatency = successfulLatencies.Average();
    var minLatency = successfulLatencies.Min();
    var maxLatency = successfulLatencies.Max();

    // Calculate jitter (standard deviation)
    var variance = successfulLatencies.Average(l => Math.Pow(l - avgLatency, 2));
    var jitter = Math.Sqrt(variance);

    // Determine status
    var status = DetermineConnectionStatus(
        avgLatency, jitter, successRate);

    _overallHealth.UpdateMetrics(
        status,
        avgLatency,
        jitter,
        successRate,
        successfulPings,
        failedPings,
        snapshots.Length
    );
}
```

### 5.3 Phase 3: Service Registration

**Step 3.1: Update Program.cs**
```csharp
// Replace ConnectionHealthService with PingHealthService
// builder.Services.AddHostedService<ConnectionHealthService>();
builder.Services.AddHostedService<PingHealthService>();

// Singleton for IConnectionHealthService
builder.Services.AddSingleton<IConnectionHealthService>(sp =>
    sp.GetRequiredService<PingHealthService>());
```

### 5.4 Phase 4: Testing & Validation

**Test Scenarios:**
1. **Normal Operation:** Verify health updates every 500ms
2. **Timeout Handling:** Disconnect network, ensure graceful degradation
3. **Reconnection:** Reconnect network, verify recovery
4. **Sustained High Latency:** Throttle connection, verify status changes
5. **Intermittent Failures:** Random packet drops, check success rate calculation
6. **Long Running:** 24-hour test for memory leaks/stability

---

## 6. Windows-Specific Considerations

### 6.1 System.Net.NetworkInformation.Ping

**Advantages:**
- Cross-platform (.NET implementation)
- Async/await support
- Handles ICMP permissions automatically
- Built-in timeout management
- Buffer and options configurability

**Windows-Specific Behavior:**
- Requires no special permissions (uses OS-level ICMP)
- TTL defaults to 128 on Windows (vs 64 on Linux)
- Firewall considerations: Windows Firewall allows ICMP Echo by default

### 6.2 Performance Characteristics

**Resource Usage (Estimated):**
- CPU: < 1% (2 pings/sec is very lightweight)
- Memory: ~50KB (CircularBuffer + objects)
- Network: ~64 bytes/ping × 2/sec = 128 bytes/sec (~1 Kbit/sec)

**Comparison to Current:**
- Current: Streams from speedify_cli continuously (higher overhead)
- New: Independent ping process (lower overhead)

### 6.3 Error Handling

**Common Windows Ping Failures:**
1. **NetworkUnreachable:** No internet connection
2. **TimedOut:** High latency or packet loss
3. **DestinationHostUnreachable:** Routing issue
4. **TtlExpired:** Network path too long (unlikely for 1.1.1.1)

All treated as failed pings (latency = 9999ms, isSuccessful = false)

---

## 7. Migration Strategy

### 7.1 Backward Compatibility

**Interface Preservation:**
- Keep `IConnectionHealthService` interface unchanged
- `GetOverallHealth()` returns `ConnectionHealth` (update fields internally)
- `IsInitialized()` works identically

**Breaking Changes:**
- Remove `GetAdapterHealth()` and `GetAllAdapterHealth()` methods (no longer per-adapter)
- Or: Return null/empty since new service doesn't track individual adapters

### 7.2 Configuration Updates

**appsettings.json additions:**
```json
"PingHealthService": {
  "PingTarget": "1.1.1.1",
  "PingIntervalMs": 500,
  "PingTimeoutMs": 3000,
  "BufferSize": 10,
  "MinSamplesForHealth": 3,
  "Thresholds": {
    "ExcellentLatency": 30,
    "GoodLatency": 80,
    "FairLatency": 150,
    "PoorLatency": 300
  }
}
```

### 7.3 Rollback Plan

If issues arise:
1. Revert service registration in Program.cs
2. Remove PingHealthService files
3. Restore ConnectionHealthService
4. No data migration needed (services are independent)

---

## 8. Future Enhancements

### 8.1 Short-term (Post-MVP)
1. **Multiple Targets:** Ping backup targets (8.8.8.8, 8.8.4.4) for redundancy
2. **Configurable Intervals:** Allow users to adjust ping frequency
3. **Historical Data:** Store longer-term trends (beyond 5-second window)
4. **Alerts:** Notify when health degrades below threshold

### 8.2 Long-term
1. **Hybrid Monitoring:** Combine ping-based health with Speedify stats
2. **Smart Routing:** Use latency data to prefer low-latency adapters
3. **Predictive Health:** Machine learning to predict connection issues
4. **Dashboard:** Real-time latency graphs and historical trends

---

## 9. Open Questions & Decisions Needed

### 9.1 Questions for User/Team
1. **Ping Frequency:** Is 500ms (2 pings/sec) acceptable, or prefer faster/slower?
2. **Threshold Tuning:** Are proposed latency thresholds appropriate for your use case?
3. **Per-Adapter vs Overall:** Keep adapter-specific health tracking or just overall?
4. **Speedify Integration:** Should new service still integrate with Speedify in some way?
5. **Backwards Compatibility:** Keep adapter methods returning null, or remove entirely?

### 9.2 Technical Decisions
1. **Buffer Size:** 10 samples at 500ms = 5 seconds (meets requirement)
2. **Failed Ping Representation:** Use sentinel value (9999) vs null/exception
3. **Thread Safety:** Maintain current locking strategy
4. **Logging Level:** Debug for each ping, or only warnings/errors?

---

## 10. Summary

### 10.1 Key Changes
1. **Data Source:** Speedify CLI stats → Direct ICMP ping to 1.1.1.1
2. **Metric:** Packet loss → Latency + Jitter + Success rate
3. **Scope:** Per-adapter → Overall connection only
4. **Platform:** Linux-focused → Windows-optimized

### 10.2 Preserved Elements
1. CircularBuffer utility (reusable)
2. Rolling window approach (5 seconds)
3. ConnectionHealth model (extended)
4. IConnectionHealthService interface
5. Health status enum (Excellent/Good/Fair/Poor/Critical)

### 10.3 Benefits
1. **Simplicity:** No dependency on Speedify CLI for health checks
2. **Reliability:** Direct network measurement
3. **Performance:** Lower overhead than streaming stats
4. **Accuracy:** Ping latency directly measures connection quality
5. **Platform-Native:** Uses .NET's built-in Ping class optimized for Windows

### 10.4 Risks & Mitigations
| Risk | Impact | Mitigation |
|------|--------|------------|
| Firewall blocks ICMP | Service doesn't work | Document firewall requirements; handle gracefully |
| 1.1.1.1 unreachable | False negatives | Add fallback targets (8.8.8.8) |
| High CPU on low-end devices | Performance issue | Configurable interval; efficient implementation |
| Loss of per-adapter data | Feature regression | Consider hybrid approach if needed |

---

## 11. Next Steps

### 11.1 For Implementation Team
1. Review and approve this design
2. Answer open questions (Section 9)
3. Create implementation tickets
4. Assign developers
5. Set milestone/timeline

### 11.2 For Code Mode
Once this design is approved:
1. Implement PingSnapshot model
2. Extend ConnectionHealth model
3. Implement PingHealthService
4. Update service registration
5. Write unit tests
6. Perform integration testing
7. Update documentation

---

**End of Design Document**