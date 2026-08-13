using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
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
        Assert.Equal(
            new[] { "device", "set", "wlan0", "autoconnect", "no" },
            WifiControlService.BuildAutoconnectArgs("wlan0", false));
        Assert.Equal(
            new[] { "device", "set", "wlan0", "autoconnect", "yes" },
            WifiControlService.BuildAutoconnectArgs("wlan0", true));
    }

    [Fact]
    public void BuildsDisconnectArguments()
    {
        Assert.Equal(new[] { "device", "disconnect", "wlan0" }, WifiControlService.BuildDisconnectArgs("wlan0"));
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
            new[]
            {
                "nmcli device set wlan0 autoconnect no",
                "nmcli device disconnect wlan0"
            },
            calls);
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

        Assert.Equal(new[] { "nmcli device set wlan0 autoconnect yes" }, calls);
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
            Task.FromResult(args.Contains("set")
                ? (10, "", "Error: Device 'wlan0' not found.")
                : (0, "", "")));

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

    [Fact]
    public async Task ConnectIsRefusedWhenInterfaceIsBlankAndWlan0IsDisabled()
    {
        var settings = new WifiControlSettings();
        settings.DisabledInterfaces["wlan0"] = true;
        var control = CreateService(settings, (_, _, _) => Task.FromResult((0, "", "")));
        var wifi = new WifiService(NullLogger<WifiService>.Instance, control);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wifi.ConnectAsync("  ", "Some SSID", "password", CancellationToken.None));
    }

    private static WifiControlService CreateService(
        WifiControlSettings settings,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output, string Error)>> runner) =>
        new(NullLogger<WifiControlService>.Instance, settings, null, TimeProvider.System, runner);
}
