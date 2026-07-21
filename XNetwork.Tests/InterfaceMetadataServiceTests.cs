using XNetwork.Models;
using XNetwork.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace XNetwork.Tests;

public class InterfaceMetadataServiceTests
{
    [Fact]
    public void ParseNmcliDeviceStatus_UsesNonGenericConnectionNames()
    {
        var names = InterfaceMetadataService.ParseNmcliDeviceStatus(
            """
            enx103c59f1039c:ethernet:connected:Smart Communications
            enxb8d4bcbcb0f0:ethernet:connected:Dito\:Telecommunity
            enxc8a3627e60c1:ethernet:connected:Wired connection 1
            lo:loopback:connected:lo
            """);

        Assert.Equal("Smart Communications", names["enx103c59f1039c"]);
        Assert.Equal("Dito:Telecommunity", names["enxb8d4bcbcb0f0"]);
        Assert.False(names.ContainsKey("enxc8a3627e60c1"));
        Assert.False(names.ContainsKey("lo"));
    }

    [Fact]
    public void ParseNmcliDeviceMetadata_TracksConnectedDashboardCandidates()
    {
        var interfaces = InterfaceMetadataService.ParseNmcliDeviceMetadata(
            """
            wlan0:wifi:connected:XNetwork Wi-Fi Asia
            eth0:ethernet:connected:netplan-eth0
            tailscale0:tun:connected (externally):tailscale0
            xbond0:tun:connected (externally):xbond0
            enxc8a3627e60c1:ethernet:unavailable:
            """);

        var wifi = interfaces.Single(item => item.Device == "wlan0");
        Assert.Equal("XNetwork Wi-Fi Asia", wifi.DisplayName);
        Assert.True(wifi.IsDashboardCandidate);

        Assert.False(interfaces.Single(item => item.Device == "eth0").IsDashboardCandidate);
        Assert.False(interfaces.Single(item => item.Device == "tailscale0").IsDashboardCandidate);
        Assert.False(interfaces.Single(item => item.Device == "xbond0").IsDashboardCandidate);
        Assert.False(interfaces.Single(item => item.Device == "enxc8a3627e60c1").IsDashboardCandidate);
    }

    [Fact]
    public void XBondStatsService_UsesLiveInterfaceNameWhenAvailable()
    {
        var status = new XBondStatus
        {
            Schedule = new XBondSchedulePlan
            {
                DataPathIds = [2],
                DuplicatePathIds = [3]
            },
            Paths =
            [
                new XBondPathStatus
                {
                    PathId = 2,
                    Name = "Configured Smart",
                    InterfaceName = "enx103c59f1039c",
                    Role = "anchor",
                    InterfaceUp = true
                },
                new XBondPathStatus
                {
                    PathId = 3,
                    Name = "Configured Dito",
                    InterfaceName = "enxb8d4bcbcb0f0",
                    Role = "backup",
                    InterfaceUp = true
                }
            ]
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enx103c59f1039c"] = "Smart Communications"
            });

        Assert.Equal("Smart Communications", snapshot.Paths[0].Name);
        Assert.Equal("Configured Dito", snapshot.Paths[1].Name);
    }

    [Fact]
    public void XBondStatsService_AddsConnectedNonXbondInterfacesAndHidesNoCarrierFromDashboard()
    {
        var status = new XBondStatus
        {
            Schedule = new XBondSchedulePlan
            {
                DataPathIds = [2]
            },
            Paths =
            [
                new XBondPathStatus
                {
                    PathId = 1,
                    Name = "Starlink",
                    InterfaceName = "enxc8a3627e60c1",
                    Role = "unavailable",
                    InterfaceUp = false
                },
                new XBondPathStatus
                {
                    PathId = 2,
                    Name = "Smart Communications",
                    InterfaceName = "enx103c59f1039c",
                    Role = "anchor",
                    InterfaceUp = true,
                    RttMs = 70
                }
            ]
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    "wlan0",
                    "wifi",
                    "connected",
                    "XNetwork Wi-Fi Asia",
                    "XNetwork Wi-Fi Asia")
            ]);

        Assert.Equal(3, snapshot.Paths.Count);
        Assert.DoesNotContain(snapshot.DashboardPaths, path => path.Name == "Starlink");
        Assert.Contains(snapshot.ActivePaths, path => path.Name == "Smart Communications");

        var wifi = Assert.Single(snapshot.StandbyPaths, path => path.InterfaceName == "wlan0");
        Assert.False(wifi.IsConfigured);
        Assert.Equal("XNetwork Wi-Fi Asia", wifi.Name);
        Assert.Equal("Connected, not in uLink", wifi.StateText);
    }

    [Fact]
    public void XBondStatsService_DoesNotAddCudyWanHandoffAsDashboardAdapter()
    {
        var snapshot = XBondStatsService.FromStatus(
            new XBondStatus(),
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    "eth0",
                    "ethernet",
                    "connected",
                    "netplan-eth0",
                    "netplan-eth0"),
                new InterfaceMetadataService.InterfaceMetadata(
                    "wlan0",
                    "wifi",
                    "connected",
                    "XNetwork Wi-Fi Asia",
                    "XNetwork Wi-Fi Asia")
            ]);

        Assert.DoesNotContain(snapshot.DashboardPaths, path => path.InterfaceName == "eth0");
        Assert.Contains(snapshot.DashboardPaths, path => path.InterfaceName == "wlan0");
    }

    [Fact]
    public void XBondStatsService_AttachesF50TelemetryByInterfaceName()
    {
        var snapshot = XBondStatsService.FromStatus(
            new XBondStatus
            {
                Schedule = new XBondSchedulePlan
                {
                    DataPathIds = [1]
                },
                Paths =
                [
                    new XBondPathStatus
                    {
                        PathId = 1,
                        Name = "Configured GOMO",
                        InterfaceName = "enxb8d4bcc3bf30",
                        Role = "anchor",
                        InterfaceUp = true
                    }
                ]
            },
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    "enxb8d4bcc3bf30",
                    "ethernet",
                    "connected",
                    "GOMO",
                    "GOMO")
            ],
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase)
            {
                ["enxb8d4bcc3bf30"] = new()
                {
                    Host = "192.168.5.1",
                    Generation = "5G",
                    SignalBars = 4,
                    UpdatedAtUtc = DateTime.UtcNow
                }
            });

        var path = Assert.Single(snapshot.Paths);
        Assert.Equal("GOMO", path.Name);
        Assert.Equal("5G", path.CellularGeneration);
        Assert.Equal(4, path.CellularSignalBars);
        Assert.True(path.HasCellularTelemetry);
    }

    [Fact]
    public void XBondStatsService_SuppressesStaleRttForDeadPaths()
    {
        var snapshot = XBondStatsService.FromStatus(new XBondStatus
        {
            Schedule = new XBondSchedulePlan
            {
                DataPathIds = [1]
            },
            Paths =
            [
                new XBondPathStatus
                {
                    PathId = 1,
                    Name = "Bad modem",
                    InterfaceName = "enx1",
                    Role = "anchor",
                    InterfaceUp = true,
                    RttMs = 57,
                    JitterMs = 4,
                    LossRate = 1,
                    StaleAckMs = 10_000
                }
            ]
        });

        var path = Assert.Single(snapshot.Paths);

        Assert.Null(path.RttMs);
        Assert.Null(path.JitterMs);
        Assert.Equal(100, path.LossPercent);
        Assert.Equal(0, snapshot.AverageRttMs);
    }

    [Fact]
    public void XBondStatsService_SuppressesRttWhenAckAgeIsStale()
    {
        var snapshot = XBondStatsService.FromStatus(new XBondStatus
        {
            Schedule = new XBondSchedulePlan
            {
                DataPathIds = [1]
            },
            Paths =
            [
                new XBondPathStatus
                {
                    PathId = 1,
                    Name = "Stale modem",
                    InterfaceName = "enx1",
                    Role = "anchor",
                    InterfaceUp = true,
                    RttMs = 80,
                    JitterMs = 8,
                    LossRate = 0.2,
                    StaleAckMs = 10_000
                }
            ]
        });

        var path = Assert.Single(snapshot.Paths);

        Assert.Null(path.RttMs);
        Assert.Null(path.JitterMs);
        Assert.Equal(20, path.LossPercent);
        Assert.Equal(0, snapshot.AverageRttMs);
    }

    [Fact]
    public void ParseDefaultRoutes_ReadsGatewayRoutesFromLinuxJson()
    {
        var routes = InterfaceMetadataService.ParseDefaultRoutes(
            """
            [
              {"dst":"default","dev":"xbond0","prefsrc":"10.250.0.2"},
              {"dst":"default","gateway":"192.168.3.1","dev":"enx103c59f1039c"},
              {"dst":"default","gateway":"192.168.4.1","dev":"enxb8d4bcbcb0f0"}
            ]
            """);

        Assert.Collection(
            routes,
            route =>
            {
                Assert.Equal("enx103c59f1039c", route.Device);
                Assert.Equal("192.168.3.1", route.Gateway);
            },
            route =>
            {
                Assert.Equal("enxb8d4bcbcb0f0", route.Device);
                Assert.Equal("192.168.4.1", route.Gateway);
            });
    }

    [Fact]
    public void SelectProviderName_UsesF50NetworkProvider()
    {
        var provider = InterfaceMetadataService.SelectProviderName(new InterfaceMetadataService.ModemProviderResponse
        {
            NetworkProvider = "SMART",
            Operator = "",
            Error = null
        });

        Assert.Equal("SMART", provider);
    }

    [Fact]
    public void SelectProviderName_IgnoresBlockedOrEmptyResponses()
    {
        var blocked = InterfaceMetadataService.SelectProviderName(new InterfaceMetadataService.ModemProviderResponse
        {
            NetworkProvider = "SMART",
            Error = "none secure connection"
        });
        var noService = InterfaceMetadataService.SelectProviderName(new InterfaceMetadataService.ModemProviderResponse
        {
            NetworkProvider = "Limited Service"
        });

        Assert.Null(blocked);
        Assert.Null(noService);
    }

    [Fact]
    public async Task ReadGatewayProviderNamesAsync_CachesSlowProviderProbeResults()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var service = new InterfaceMetadataService(
            NullLogger<InterfaceMetadataService>.Instance,
            (gateway, _) =>
            {
                calls++;
                return Task.FromResult<string?>($"Provider {gateway}");
            },
            time,
            new HttpClient());
        var routes = new[]
        {
            new InterfaceMetadataService.GatewayRoute("enx0", "192.168.3.1")
        };

        var first = await service.ReadGatewayProviderNamesAsync(routes);
        var second = await service.ReadGatewayProviderNamesAsync(routes);

        Assert.Equal("Provider 192.168.3.1", first["enx0"]);
        Assert.Equal(first["enx0"], second["enx0"]);
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromSeconds(31));
        await service.ReadGatewayProviderNamesAsync(routes);

        Assert.Equal(2, calls);
    }

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
