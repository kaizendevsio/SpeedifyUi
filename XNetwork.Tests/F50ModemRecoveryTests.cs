using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class F50ModemRecoveryTests
{
    [Fact]
    public void Normalize_ClampsIntervalsAndStripsPingPort()
    {
        var settings = new F50ModemRecoverySettings
        {
            CheckIntervalMinutes = 0,
            FailedRecoveryCooldownRounds = 99,
            SettleTimeoutSeconds = 5,
            UsbResetCommandPath = "  /usr/local/bin/usbreset  ",
            PingTarget = "45.77.241.247:443",
            PingCount = 99,
            PingTimeoutSeconds = 0,
            SevereLossPercent = 10
        };

        F50ModemRecoverySettingsStore.Normalize(settings);

        Assert.Equal(1, settings.CheckIntervalMinutes);
        Assert.Equal(10, settings.FailedRecoveryCooldownRounds);
        Assert.Equal(15, settings.SettleTimeoutSeconds);
        Assert.Equal("/usr/local/bin/usbreset", settings.UsbResetCommandPath);
        Assert.Equal("45.77.241.247", settings.PingTarget);
        Assert.Equal(5, settings.PingCount);
        Assert.Equal(1, settings.PingTimeoutSeconds);
        Assert.Equal(50, settings.SevereLossPercent);
    }

    [Fact]
    public void BuildUsbResetTarget_WalksToUsbParent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"f50-usb-{Guid.NewGuid():N}");
        try
        {
            var usbDevice = Path.Combine(root, "4-1.2");
            var netDevice = Path.Combine(usbDevice, "interface", "net", "enxb8d4bcc3bf30");
            Directory.CreateDirectory(netDevice);
            File.WriteAllText(Path.Combine(usbDevice, "busnum"), "4");
            File.WriteAllText(Path.Combine(usbDevice, "devnum"), "7");

            var target = F50ModemRecoveryService.BuildUsbResetTargetFromUsbDeviceDirectory(
                netDevice,
                path => File.Exists(path) ? File.ReadAllText(path) : null);

            Assert.Equal("004/007", target);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DetermineRecoveryIssue_HealthyPingAndPathNeedsNoAction()
    {
        var path = new XBondPathStatus
        {
            LossRate = 0.05
        };
        var modem = new F50RecoveryProbeResponse
        {
            WanIpAddress = "10.10.10.2",
            PppStatus = "connected"
        };

        var issue = F50ModemRecoveryService.DetermineRecoveryIssue(
            path,
            modem,
            boundPingOk: true,
            severeLossPercent: 95);

        Assert.Equal(RecoveryIssue.None, issue);
    }

    [Fact]
    public void DetermineRecoveryIssue_StaleSocketWithUsableModemRequestsRebindOnly()
    {
        var path = new XBondPathStatus
        {
            SendFailureStreak = 2,
            LastSocketError = "send failed: No such device (os error 19)"
        };
        var modem = new F50RecoveryProbeResponse
        {
            WanIpAddress = "10.10.10.2",
            PppStatus = "connected"
        };

        var issue = F50ModemRecoveryService.DetermineRecoveryIssue(
            path,
            modem,
            boundPingOk: true,
            severeLossPercent: 95);

        Assert.Equal(RecoveryIssue.SocketOnly, issue);
    }

    [Fact]
    public void DetermineRecoveryIssue_SeverePathLossRequestsModemReset()
    {
        var path = new XBondPathStatus
        {
            LossRate = 1.0
        };
        var modem = new F50RecoveryProbeResponse
        {
            WanIpAddress = "0.0.0.0",
            PppStatus = "disconnected"
        };

        var issue = F50ModemRecoveryService.DetermineRecoveryIssue(
            path,
            modem,
            boundPingOk: false,
            severeLossPercent: 95);

        Assert.Equal(RecoveryIssue.ModemReset, issue);
    }
}
