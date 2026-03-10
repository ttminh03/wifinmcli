using WifiManager.Services;

var builder = WebApplication.CreateBuilder(args);

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

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Eagerly initialize the DBus / WiFi service at startup
var wifiService = app.Services.GetRequiredService<WifiManagerService>();
await wifiService.InitializeAsync();

app.Run();
