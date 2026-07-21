using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkLanAccessService(
    StarlinkLanAccessSettings settings,
    IStarlinkInterfaceResolver interfaceResolver,
    IStarlinkLanAccessCommandRunner commandRunner,
    ILogger<StarlinkLanAccessService> logger) : BackgroundService
{
    private string? _appliedInterface;
    private bool _initialized;
    private DateTimeOffset _nextVerificationUtc = DateTimeOffset.MinValue;

    public async Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            if (!_initialized || _appliedInterface is not null)
            {
                await RemoveAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            return;
        }

        StarlinkInterfaceResolution resolution;
        try
        {
            resolution = await interfaceResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the current Starlink interface; removing stale LAN access state");
            await RemoveAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            return;
        }
        if (!resolution.IsAvailable || string.IsNullOrWhiteSpace(resolution.InterfaceName))
        {
            if (!_initialized || _appliedInterface is not null)
            {
                await RemoveAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            return;
        }

        var interfaceName = resolution.InterfaceName;
        var needsApply = !_initialized ||
                         !string.Equals(_appliedInterface, interfaceName, StringComparison.Ordinal) ||
                         DateTimeOffset.UtcNow >= _nextVerificationUtc;

        if (!needsApply)
        {
            return;
        }

        if (string.Equals(_appliedInterface, interfaceName, StringComparison.Ordinal))
        {
            var check = await commandRunner.CheckAsync(interfaceName, settings, cancellationToken).ConfigureAwait(false);
            if (check.Succeeded)
            {
                _nextVerificationUtc = DateTimeOffset.UtcNow + settings.VerifyInterval;
                return;
            }
        }

        var apply = await commandRunner.ApplyAsync(interfaceName, settings, cancellationToken).ConfigureAwait(false);
        _initialized = true;
        if (apply.Succeeded)
        {
            _appliedInterface = interfaceName;
            _nextVerificationUtc = DateTimeOffset.UtcNow + settings.VerifyInterval;
            logger.LogInformation(
                "Starlink LAN management access is active through {InterfaceName}: {Message}",
                interfaceName,
                apply.Message);
            return;
        }

        logger.LogWarning(
            "Starlink LAN management access could not use {InterfaceName}: {Message}",
            interfaceName,
            apply.Message);
        await RemoveAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Starlink LAN management reconciliation failed");
            }

            await Task.Delay(settings.CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var remove = await commandRunner.RemoveAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!remove.Succeeded)
        {
            logger.LogWarning("Could not remove stale Starlink LAN management state: {Message}", remove.Message);
        }
        else if (_appliedInterface is not null)
        {
            logger.LogInformation("Removed Starlink LAN management access from {InterfaceName}", _appliedInterface);
        }

        _appliedInterface = null;
        _nextVerificationUtc = DateTimeOffset.MinValue;
    }
}
