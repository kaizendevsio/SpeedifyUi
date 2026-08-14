using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondMssClampServiceTests
{
    [Fact]
    public async Task CachesStatusSoPollingDoesNotForkIptablesEveryTime()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var service = CreateService(time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult((0, ""));
        });

        var first = await service.GetStatusAsync();
        var second = await service.GetStatusAsync();
        var third = await service.GetStatusAsync();

        Assert.Equal(1, calls);
        Assert.True(first.IsEnabled);
        Assert.True(second.IsEnabled);
        Assert.True(third.IsEnabled);
    }

    [Fact]
    public async Task ReReadsAfterTheCacheWindowExpires()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var service = CreateService(time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult((0, ""));
        });

        await service.GetStatusAsync();
        time.Advance(TimeSpan.FromSeconds(16));
        await service.GetStatusAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvalidateForcesAFreshRead()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var service = CreateService(time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult((0, ""));
        });

        await service.GetStatusAsync();
        service.InvalidateStatus();
        await service.GetStatusAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReportsDisabledWhenTheRuleIsMissing()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var service = CreateService(time, (_, _, _) => Task.FromResult((1, "")));

        var status = await service.GetStatusAsync();

        Assert.False(status.IsEnabled);
    }

    private static XBondMssClampService CreateService(
        TimeProvider timeProvider,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> runner) =>
        new(
            NullLogger<XBondMssClampService>.Instance,
            new XBondSettings { AllowServiceControl = true },
            timeProvider,
            TimeSpan.FromSeconds(15),
            runner,
            () => true);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
