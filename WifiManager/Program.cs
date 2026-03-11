using WifiManager.Services;

var builder = WebApplication.CreateBuilder(args);

// Ensure static web assets (including Blazor framework scripts) are available when running without publish.
builder.WebHost.UseStaticWebAssets();

// ── Services ──────────────────────────────────────────────────
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Register DBus service as singleton – one connection for the lifetime of the app
builder.Services.AddSingleton<WifiDbusService>();
builder.Services.AddSingleton<WifiManagerService>();

// ── App ───────────────────────────────────────────────────────
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

// Required for serving framework/static web assets on newer ASP.NET Core versions.
app.MapStaticAssets();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Eagerly initialize the DBus / WiFi service at startup
var wifiService = app.Services.GetRequiredService<WifiManagerService>();
await wifiService.InitializeAsync();

app.Run();
