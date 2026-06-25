using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class LocalDeviceProxyTests
{
    [Fact]
    public async Task SaveEntryAsync_NormalizesAndPersistsProxyRoutes()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"local-device-proxies-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LocalDeviceProxySettings();
            var store = new LocalDeviceProxySettingsStore(
                NullLogger<LocalDeviceProxySettingsStore>.Instance,
                filePath);
            var service = new LocalDeviceProxyService(settings, store);

            await service.SaveEntryAsync(new LocalDeviceProxyEntry
            {
                DisplayName = "GOMO",
                ExposedRoute = "gomo/",
                TargetUrl = "http://192.168.5.1/",
                Enabled = true,
                TelemetryEnabled = true
            });

            var entry = Assert.Single(service.GetEntries());
            Assert.Equal("/gomo", entry.ExposedRoute);
            Assert.Equal("http://192.168.5.1", entry.TargetUrl);

            var loaded = new LocalDeviceProxySettings();
            store.Load(loaded);

            var persisted = Assert.Single(loaded.Entries);
            Assert.Equal("/gomo", persisted.ExposedRoute);
            Assert.Equal("http://192.168.5.1", persisted.TargetUrl);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task ValidateEntry_RejectsReservedDuplicateAndInvalidTargets()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"local-device-proxies-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LocalDeviceProxySettings();
            var service = new LocalDeviceProxyService(
                settings,
                new LocalDeviceProxySettingsStore(
                    NullLogger<LocalDeviceProxySettingsStore>.Instance,
                    filePath));

            await service.SaveEntryAsync(new LocalDeviceProxyEntry
            {
                DisplayName = "Lab Device",
                ExposedRoute = "/lab-device",
                TargetUrl = "http://192.168.99.1"
            });

            Assert.Contains(
                "already used",
                string.Join(" ", service.ValidateEntry(new LocalDeviceProxyEntry
                {
                    DisplayName = "Duplicate",
                    ExposedRoute = "/lab-device",
                    TargetUrl = "http://192.168.99.1"
                }).Errors),
                StringComparison.OrdinalIgnoreCase);

            Assert.Contains(
                "collides",
                string.Join(" ", service.ValidateEntry(new LocalDeviceProxyEntry
                {
                    DisplayName = "Settings",
                    ExposedRoute = "/settings/cudy",
                    TargetUrl = "http://192.168.10.1"
                }).Errors),
                StringComparison.OrdinalIgnoreCase);

            Assert.Contains(
                "http:// or https://",
                string.Join(" ", service.ValidateEntry(new LocalDeviceProxyEntry
                {
                    DisplayName = "Bad target",
                    ExposedRoute = "/bad",
                    TargetUrl = "ftp://192.168.4.1"
                }).Errors),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task ValidateEntry_RejectsDuplicateAndUnsafePortProxyPorts()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"local-device-proxies-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LocalDeviceProxySettings();
            var service = new LocalDeviceProxyService(
                settings,
                new LocalDeviceProxySettingsStore(
                    NullLogger<LocalDeviceProxySettingsStore>.Instance,
                    filePath));

            await service.SaveEntryAsync(new LocalDeviceProxyEntry
            {
                DisplayName = "Smart",
                ProxyMode = LocalDeviceProxyModes.Port,
                ListenPort = 18081,
                TargetUrl = "http://192.168.3.1"
            });

            var duplicate = service.ValidateEntry(new LocalDeviceProxyEntry
            {
                DisplayName = "Duplicate Smart",
                ProxyMode = LocalDeviceProxyModes.Port,
                ListenPort = 18081,
                TargetUrl = "http://192.168.3.1"
            });
            Assert.Contains(
                "port is already used",
                string.Join(" ", duplicate.Errors),
                StringComparison.OrdinalIgnoreCase);

            var unsafePort = service.ValidateEntry(new LocalDeviceProxyEntry
            {
                DisplayName = "Bad port",
                ProxyMode = LocalDeviceProxyModes.Port,
                ListenPort = 8080,
                TargetUrl = "http://192.168.4.1"
            });
            Assert.Contains(
                "between 18080 and 18999",
                string.Join(" ", unsafePort.Errors),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public void BuildTargetUri_PreservesSubPathsAndQueryStrings()
    {
        var uri = LocalDeviceProxyMiddleware.BuildTargetUri(
            "http://192.168.5.1/admin",
            "/gomo",
            "/gomo/goform/goform_get_cmd_process",
            "?cmd=signalbar");

        Assert.NotNull(uri);
        Assert.Equal(
            "http://192.168.5.1/admin/goform/goform_get_cmd_process?cmd=signalbar",
            uri.ToString());
    }

    [Fact]
    public void BuildPortTargetUri_PreservesRootMountedPathsAndQueryStrings()
    {
        var uri = LocalDevicePortProxyHostedService.BuildPortTargetUri(
            "http://192.168.3.1",
            "/m/index.html?login=1");

        Assert.NotNull(uri);
        Assert.Equal("http://192.168.3.1/m/index.html?login=1", uri.ToString());
    }

    [Fact]
    public async Task FindAdminProxyForAdapter_MatchesGatewayBeforeProviderName()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"local-device-proxies-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LocalDeviceProxySettings();
            var service = new LocalDeviceProxyService(
                settings,
                new LocalDeviceProxySettingsStore(
                    NullLogger<LocalDeviceProxySettingsStore>.Instance,
                    filePath));

            await service.SaveEntryAsync(new LocalDeviceProxyEntry
            {
                DisplayName = "Smart",
                ProxyMode = LocalDeviceProxyModes.Port,
                ListenPort = 18081,
                TargetUrl = "http://192.168.3.1"
            });
            await service.SaveEntryAsync(new LocalDeviceProxyEntry
            {
                DisplayName = "DITO",
                ProxyMode = LocalDeviceProxyModes.Port,
                ListenPort = 18082,
                TargetUrl = "http://192.168.4.1"
            });

            var gatewayMatch = service.FindAdminProxyForAdapter("enx1", "Not Smart", "192.168.3.1");
            Assert.NotNull(gatewayMatch);
            Assert.Equal(18081, gatewayMatch.ListenPort);

            var nameFallback = service.FindAdminProxyForAdapter("enx2", "Dito Telecommunity", null);
            Assert.NotNull(nameFallback);
            Assert.Equal(18082, nameFallback.ListenPort);

            Assert.Null(service.FindAdminProxyForAdapter("wlan0", "XNetwork Wi-Fi Asia", null));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public void TryBuildDirectoryRedirectPath_RedirectsProxyRootToDirectoryPath()
    {
        var shouldRedirect = LocalDeviceProxyMiddleware.TryBuildDirectoryRedirectPath(
            "/gomo",
            "/gomo",
            "?token=1",
            out var redirectPath);

        Assert.True(shouldRedirect);
        Assert.Equal("/gomo/?token=1", redirectPath);

        Assert.False(LocalDeviceProxyMiddleware.TryBuildDirectoryRedirectPath(
            "/gomo",
            "/gomo/",
            null,
            out _));

        Assert.False(LocalDeviceProxyMiddleware.TryBuildDirectoryRedirectPath(
            "/gomo",
            "/gomo/mobile.html",
            null,
            out _));
    }

    [Fact]
    public void RewriteLocalDeviceBody_KeepsRootRelativeAssetsInsideProxyRoute()
    {
        var rewritten = LocalDeviceProxyMiddleware.RewriteLocalDeviceBody(
            """
            <form action="/goform/login"><script src="/js/app.js"></script><a href="/index.html">Home</a></form>
            <style>.icon{background:url(/img/icon.png)}</style>
            <script>fetch('/goform/status');</script>
            """,
            "/gomo");

        Assert.Contains("action=\"/gomo/goform/login\"", rewritten);
        Assert.Contains("src=\"/gomo/js/app.js\"", rewritten);
        Assert.Contains("href=\"/gomo/index.html\"", rewritten);
        Assert.Contains("url(/gomo/img/icon.png)", rewritten);
        Assert.Contains("fetch('/gomo/goform/status')", rewritten);
    }

    [Fact]
    public void RewriteLocalDeviceBody_KeepsRelativeScriptNavigationInsideProxyRoute()
    {
        var rewritten = LocalDeviceProxyMiddleware.RewriteLocalDeviceBody(
            """
            <script>
            var tempUrl = "m/index.html";
            window.location.href = "mobile.html";
            location.replace('index.html');
            top.location = "../logout.html";
            </script>
            """,
            "/gomo");

        Assert.Contains("var tempUrl = \"/gomo/m/index.html\"", rewritten);
        Assert.Contains("window.location.href = \"/gomo/mobile.html\"", rewritten);
        Assert.Contains("location.replace('/gomo/index.html')", rewritten);
        Assert.Contains("top.location = \"/gomo/logout.html\"", rewritten);
    }

    [Fact]
    public void F50Telemetry_MapsNetworkGenerationAndSignalBars()
    {
        Assert.Equal("5G", F50ModemTelemetryService.SelectGeneration(new F50ModemTelemetryResponse
        {
            CurrentNetworkType = "NR5G"
        }));
        Assert.Equal("5G", F50ModemTelemetryService.SelectGeneration(new F50ModemTelemetryResponse
        {
            NetworkType = "ENDC"
        }));
        Assert.Equal("4G", F50ModemTelemetryService.SelectGeneration(new F50ModemTelemetryResponse
        {
            CurrentNetworkType = "LTE"
        }));
        Assert.Equal("3G", F50ModemTelemetryService.SelectGeneration(new F50ModemTelemetryResponse
        {
            CurrentNetworkType = "WCDMA"
        }));

        Assert.Equal(5, F50ModemTelemetryService.SelectSignalBars("7"));
        Assert.Equal(0, F50ModemTelemetryService.SelectSignalBars("-1"));
        Assert.Null(F50ModemTelemetryService.SelectSignalBars("unknown"));
    }
}
