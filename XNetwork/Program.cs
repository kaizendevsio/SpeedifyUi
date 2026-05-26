using XNetwork.Components;
using XNetwork.Services;
using XNetwork.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<BlazorTransitionableRoute.IRouteTransitionInvoker, BlazorTransitionableRoute.DefaultRouteTransitionInvoker>();

// Add network monitor service
builder.Services.AddSingleton<NetworkMonitorSettingsStore>();
builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration.GetSection("NetworkMonitor").Get<NetworkMonitorSettings>() ?? new NetworkMonitorSettings();
    sp.GetRequiredService<NetworkMonitorSettingsStore>().Load(settings);
    return settings;
});
builder.Services.AddSingleton<SpeedifyService>();
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

// Add connection health service (both as singleton and hosted service)
builder.Services.AddSingleton<ConnectionHealthService>();
builder.Services.AddSingleton<IConnectionHealthService>(sp => sp.GetRequiredService<ConnectionHealthService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionHealthService>());

// Add automatic Speedify server health switcher
builder.Services.AddSingleton(sp =>
    builder.Configuration.GetSection("AutoServerSwitch").Get<AutoServerSwitchSettings>() ?? new AutoServerSwitchSettings());
builder.Services.AddHttpClient<ProbeScoreClient>();
builder.Services.AddSingleton<ServerSwitchRecommendationSelector>();
builder.Services.AddSingleton<LocalWanStabilityEvaluator>();
builder.Services.AddSingleton<RecommendationConfidenceTracker>();
builder.Services.AddSingleton<AutoServerSwitchStateStore>();
builder.Services.AddSingleton<AutoServerSwitchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoServerSwitchService>());

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
