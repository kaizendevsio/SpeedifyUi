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
builder.Services.AddSingleton<XBondLabService>();
builder.Services.AddSingleton<XBondTrafficEngineService>();
builder.Services.AddSingleton<XBondClientConfigService>();
builder.Services.AddSingleton<XBondScopedRouteService>();
builder.Services.AddSingleton<XBondSpeedTestService>();
builder.Services.AddSingleton<XBondMssClampService>();

// Add direct Starlink dish telemetry polling for Starlink adapters
builder.Services.AddSingleton(sp =>
    builder.Configuration.GetSection("StarlinkTelemetry").Get<StarlinkTelemetrySettings>() ?? new StarlinkTelemetrySettings());
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
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
