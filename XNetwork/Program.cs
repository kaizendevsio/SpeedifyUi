using XNetwork.Components;
using XNetwork.Services;
using XNetwork.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(15);
        options.HandshakeTimeout = TimeSpan.FromSeconds(15);
        options.KeepAliveInterval = TimeSpan.FromSeconds(5);
    });
builder.Services.AddScoped<BlazorTransitionableRoute.IRouteTransitionInvoker, BlazorTransitionableRoute.DefaultRouteTransitionInvoker>();
builder.Services.AddScoped<RouteTransitionDirectionService>();

// Add network monitor service
builder.Services.AddSingleton<NetworkMonitorSettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("NetworkMonitor").Get<NetworkMonitorSettings>() ?? new NetworkMonitorSettings();
    sp.GetRequiredService<NetworkMonitorSettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<BuildInfoService>();
builder.Services.AddSingleton<WifiService>();
builder.Services.AddSingleton<NetworkMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkMonitorService>());

// Add Cudy travel-router AP automation
builder.Services.AddSingleton<CudyAdminPasswordStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("CudyApAutomation").Get<CudyApAutomationSettings>() ?? new CudyApAutomationSettings();
    sp.GetRequiredService<CudyAdminPasswordStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<CudyLuciClient>();
builder.Services.AddSingleton<CudyApControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CudyApControlService>());
builder.Services.AddSingleton<LocalProcessTrafficService>();
builder.Services.AddSingleton<XRouterService>();

// Add XBond runtime observability and controls.
builder.Services.AddSingleton<XBondSettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("XBond").Get<XBondSettings>() ?? new XBondSettings();
    sp.GetRequiredService<XBondSettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<XBondStatusService>();
builder.Services.AddSingleton<InterfaceMetadataService>();
builder.Services.AddSingleton<XBondStatsService>();
builder.Services.AddSingleton<IXBondStatsProvider>(sp => sp.GetRequiredService<XBondStatsService>());
builder.Services.AddSingleton<XBondSnapshotCache>();
builder.Services.AddSingleton<XBondLabService>();
builder.Services.AddSingleton<XBondTrafficEngineService>();
builder.Services.AddSingleton<XBondClientConfigService>();
builder.Services.AddSingleton<XBondScopedRouteService>();
builder.Services.AddSingleton<XBondSpeedTestService>();
builder.Services.AddSingleton<XBondMssClampService>();

builder.Services.AddSingleton<LocalDeviceProxySettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("LocalDeviceProxies").Get<LocalDeviceProxySettings>() ?? new LocalDeviceProxySettings();
    sp.GetRequiredService<LocalDeviceProxySettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<LocalDeviceProxyService>();
builder.Services.AddHostedService<LocalDevicePortProxyHostedService>();
builder.Services.AddSingleton<F50ModemTelemetryService>();
builder.Services.AddSingleton<F50ModemRecoverySettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("F50ModemRecovery").Get<F50ModemRecoverySettings>() ?? new F50ModemRecoverySettings();
    sp.GetRequiredService<F50ModemRecoverySettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<F50ModemRecoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<F50ModemRecoveryService>());
builder.Services.AddSingleton<TrafficBypassSettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("TrafficBypass").Get<TrafficBypassSettings>() ?? new TrafficBypassSettings();
    sp.GetRequiredService<TrafficBypassSettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<TrafficBypassService>();
builder.Services.AddHostedService<TrafficBypassStartupService>();

// Add direct Starlink dish telemetry polling for Starlink adapters
builder.Services.AddSingleton(sp =>
    builder.Configuration.GetSection("StarlinkTelemetry").Get<StarlinkTelemetrySettings>() ?? new StarlinkTelemetrySettings());
builder.Services.AddSingleton<IStarlinkInterfaceResolver, StarlinkInterfaceResolver>();
builder.Services.AddSingleton<IStarlinkHttpClientFactory, StarlinkBoundHttpClientFactory>();
builder.Services.AddSingleton<StarlinkDeviceClient>();
builder.Services.AddSingleton<StarlinkTelemetryService>();
builder.Services.AddSingleton<IStarlinkTelemetryService>(sp => sp.GetRequiredService<StarlinkTelemetryService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<StarlinkTelemetryService>());

// Add connection health service (both as singleton and hosted service)
builder.Services.AddSingleton<ConnectionHealthService>();
builder.Services.AddSingleton<IConnectionHealthService>(sp => sp.GetRequiredService<ConnectionHealthService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionHealthService>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseMiddleware<LocalDeviceProxyMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
