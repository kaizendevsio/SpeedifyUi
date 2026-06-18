namespace XNetwork.Models;

public class XBondSettings
{
    public bool Enabled { get; set; } = true;

    public string TrafficEngineMode { get; set; } = XBondTrafficEngineModes.XBondActive;

    public bool AllowServiceControl { get; set; }

    public string ClientServiceName { get; set; } = "xbond-client.service";

    public string ServiceManagerPath { get; set; } = "systemctl";

    public bool UseSudoForServiceManager { get; set; } = true;

    public string SudoPath { get; set; } = "sudo";

    public int ServiceCommandTimeoutSeconds { get; set; } = 10;

    public string ClientBinaryPath { get; set; } = "xbond-client";

    public string ClientConfigPath { get; set; } = "/etc/xbond/client.toml";

    public string RuntimeStatusPath { get; set; } = "/run/xbond/client-status.json";

    public string ClientControlSocketPath { get; set; } = "/run/xbond/client-control.sock";

    public string ScheduleMode { get; set; } = "anchor-duplicate-1";

    public int MaxActiveBackups { get; set; } = 1;

    public int StatusTimeoutSeconds { get; set; } = 2;

    public string PublicTestServerAddress { get; set; } = "45.77.241.247:8444";

    public int PublicTestPathId { get; set; } = 1;

    public List<int> PublicTestPathIds { get; set; } = new();

    public List<string> PublicTestBinds { get; set; } = new();

    public int PublicTestCount { get; set; } = 10;

    public int PublicTestIntervalMs { get; set; } = 100;

    public int PublicTestPacketTimeoutMs { get; set; } = 2500;

    public int PublicTestCommandTimeoutSeconds { get; set; } = 45;

    public string PublicTestKeyEnvironmentVariable { get; set; } = "XBOND_PSK";

    public string PublicTestKeyFilePath { get; set; } = "/home/xeon-network/.config/XNetwork/xbond-psk";

    public string TunnelDevice { get; set; } = "xbond0";

    public string TunnelSource { get; set; } = "10.250.0.2";

    public string RouteCommandPath { get; set; } = "ip";

    public string PingCommandPath { get; set; } = "ping";

    public string ScopedRouteDefaultTarget { get; set; } = "8.8.8.8";

    public int ScopedRouteTestCount { get; set; } = 10;

    public int ScopedRoutePacketTimeoutSeconds { get; set; } = 2;

    public int ScopedRouteCommandTimeoutSeconds { get; set; } = 45;

    public string SpeedTestCommandPath { get; set; } = "speedtest";

    public int SpeedTestCommandTimeoutSeconds { get; set; } = 180;

    public int SpeedTestHttpTimeoutSeconds { get; set; } = 15;

    public string IperfCommandPath { get; set; } = "iperf3";

    public string ServerSpeedTestHost { get; set; } = "10.250.0.1";

    public int ServerSpeedTestPort { get; set; } = 5201;

    public string NativeSpeedTestHost { get; set; } = "45.77.241.247";

    public int NativeSpeedTestPort { get; set; } = 5202;

    public int ServerSpeedTestDurationSeconds { get; set; } = 8;

    public int ServerSpeedTestCommandTimeoutSeconds { get; set; } = 45;

    public int NativeSpeedTestCommandTimeoutSeconds { get; set; } = 12;

    public int IperfConnectTimeoutMs { get; set; } = 3000;

    public string PerformanceArtifactDirectory { get; set; } = "/var/lib/xnetwork/diagnostics";

    public int PerformanceMatrixSettleSeconds { get; set; } = 4;

    public int PerformanceMatrixOverrideTtlSeconds { get; set; } = 120;

    public string IptablesCommandPath { get; set; } = "iptables";

    public bool MssClampEnabled { get; set; }

    public int MssClampValue { get; set; } = 1360;

    public int[] MtuSweepValues { get; set; } = [1200, 1280, 1360, 1400, 1420];

    public string TrafficControlCommandPath { get; set; } = "tc";

    public int SimulationDelayMs { get; set; } = 350;

    public int SimulationJitterMs { get; set; } = 120;

    public double SimulationLossPercent { get; set; } = 20;

    public string SimulationRateLimit { get; set; } = "5mbit";
}

public static class XBondTrafficEngineModes
{
    public const string XBondActive = "xbond-active";

    public static bool IsKnown(string mode) =>
        string.Equals(mode, XBondActive, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? mode) => XBondActive;
}

public class XBondTrafficEngineStatus
{
    public string Mode { get; set; } = XBondTrafficEngineModes.XBondActive;

    public bool ServiceControlAllowed { get; set; }

    public string ClientServiceName { get; set; } = "";

    public string ClientServiceState { get; set; } = "unknown";

    public bool ClientServiceRunning { get; set; }

    public string ClientServiceEnableState { get; set; } = "unknown";

    public bool ClientServiceEnabled { get; set; }

    public bool CanStart => ServiceControlAllowed && !ClientServiceRunning;

    public bool CanStop => ServiceControlAllowed && ClientServiceRunning;

    public bool CanEnableAtBoot => ServiceControlAllowed && !ClientServiceEnabled;

    public bool CanDisableAtBoot => ServiceControlAllowed && ClientServiceEnabled;

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class XBondMssClampStatus
{
    public bool IsSupported { get; set; }

    public bool IsEnabled { get; set; }

    public int MssValue { get; set; }

    public string TunnelDevice { get; set; } = "xbond0";

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}
