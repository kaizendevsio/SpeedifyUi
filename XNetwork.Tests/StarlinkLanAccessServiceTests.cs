using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public sealed class StarlinkLanAccessServiceTests
{
    [Fact]
    public void HelperScript_ScopesRouteForwardingAndNatToStarlinkManagement()
    {
        var helper = File.ReadAllText(FindRepoFile(
            "XNetwork",
            "deploy",
            "scripts",
            "xnetwork-starlink-lan-access-apply"));

        Assert.Contains("ROUTE_PROTOCOL=\"199\"", helper, StringComparison.Ordinal);
        Assert.Contains("RULE_COMMENT=\"ulink-starlink-management\"", helper, StringComparison.Ordinal);
        Assert.Contains("-i \"$LAN_IF\" -o \"$STARLINK_IF\" -s \"$LAN_CIDR\" -d \"$DESTINATION/32\"", helper, StringComparison.Ordinal);
        Assert.Contains("-o \"$STARLINK_IF\" -s \"$LAN_CIDR\" -d \"$DESTINATION/32\" -j MASQUERADE", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("enxc8", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileOnceAsync_AppliesResolvedInterfaceOnlyOnceBeforeVerification()
    {
        var runner = new RecordingRunner();
        var service = CreateService(
            new StaticResolver(StarlinkInterfaceResolution.Available("enx-starlink-a", "matched")),
            runner);

        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();

        Assert.Equal(["apply:enx-starlink-a"], runner.Calls);
    }

    [Fact]
    public async Task ReconcileOnceAsync_ReappliesWhenResolvedInterfaceChanges()
    {
        var runner = new RecordingRunner();
        var resolver = new SequenceResolver(
            StarlinkInterfaceResolution.Available("enx-starlink-a", "matched"),
            StarlinkInterfaceResolution.Available("enx-starlink-b", "re-enumerated"));
        var service = CreateService(resolver, runner);

        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();

        Assert.Equal(["apply:enx-starlink-a", "apply:enx-starlink-b"], runner.Calls);
    }

    [Fact]
    public async Task ReconcileOnceAsync_RemovesStateWhenStarlinkIsUnavailable()
    {
        var runner = new RecordingRunner();
        var resolver = new SequenceResolver(
            StarlinkInterfaceResolution.Available("enx-starlink", "matched"),
            StarlinkInterfaceResolution.Unavailable("disconnected"));
        var service = CreateService(resolver, runner);

        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();

        Assert.Equal(["apply:enx-starlink", "remove"], runner.Calls);
    }

    [Fact]
    public async Task ReconcileOnceAsync_CleansStaleStateAtStartupWhenUnavailable()
    {
        var runner = new RecordingRunner();
        var service = CreateService(
            new StaticResolver(StarlinkInterfaceResolution.Unavailable("not connected")),
            runner);

        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();

        Assert.Equal(["remove"], runner.Calls);
    }

    [Fact]
    public async Task ReconcileOnceAsync_RemovesStaleStateWhenResolverFails()
    {
        var runner = new RecordingRunner();
        var service = CreateService(new ThrowingResolver(), runner);

        await service.ReconcileOnceAsync();

        Assert.Equal(["remove"], runner.Calls);
    }

    private static StarlinkLanAccessService CreateService(
        IStarlinkInterfaceResolver resolver,
        IStarlinkLanAccessCommandRunner runner) =>
        new(
            new StarlinkLanAccessSettings { VerifyIntervalSeconds = 600 },
            resolver,
            runner,
            NullLogger<StarlinkLanAccessService>.Instance);

    private sealed class StaticResolver(StarlinkInterfaceResolution resolution) : IStarlinkInterfaceResolver
    {
        public Task<StarlinkInterfaceResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(resolution);
    }

    private sealed class SequenceResolver(params StarlinkInterfaceResolution[] resolutions) : IStarlinkInterfaceResolver
    {
        private int _index;

        public Task<StarlinkInterfaceResolution> ResolveAsync(CancellationToken cancellationToken = default)
        {
            var result = resolutions[Math.Min(_index, resolutions.Length - 1)];
            _index++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingResolver : IStarlinkInterfaceResolver
    {
        public Task<StarlinkInterfaceResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<StarlinkInterfaceResolution>(new TimeoutException("probe failed"));
    }

    private sealed class RecordingRunner : IStarlinkLanAccessCommandRunner
    {
        public List<string> Calls { get; } = [];

        public Task<StarlinkLanAccessCommandResult> ApplyAsync(
            string starlinkInterface,
            StarlinkLanAccessSettings settings,
            CancellationToken cancellationToken)
        {
            Calls.Add($"apply:{starlinkInterface}");
            return Task.FromResult(new StarlinkLanAccessCommandResult(true, "applied"));
        }

        public Task<StarlinkLanAccessCommandResult> CheckAsync(
            string starlinkInterface,
            StarlinkLanAccessSettings settings,
            CancellationToken cancellationToken)
        {
            Calls.Add($"check:{starlinkInterface}");
            return Task.FromResult(new StarlinkLanAccessCommandResult(true, "current"));
        }

        public Task<StarlinkLanAccessCommandResult> RemoveAsync(
            StarlinkLanAccessSettings settings,
            CancellationToken cancellationToken)
        {
            Calls.Add("remove");
            return Task.FromResult(new StarlinkLanAccessCommandResult(true, "removed"));
        }
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "uLink.sln")))
            {
                return Path.Combine([directory.FullName, .. pathParts]);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate uLink repository root.");
    }
}
