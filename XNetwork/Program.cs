using XNetwork.Components;
using XNetwork.Services;
using XNetwork.Models;
using XNetwork.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add network monitor service
builder.Services.Configure<NetworkMonitorSettings>(
    builder.Configuration.GetSection("NetworkMonitor"));
builder.Services.AddHostedService<NetworkMonitorService>();
builder.Services.AddSingleton<SpeedifyService>();

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