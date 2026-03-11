// Program.cs - Dùng Blazor Server kiểu cổ điển (AddServerSideBlazor + MapBlazorHub)
// Cách này dùng blazor.server.js có sẵn trong ASP.NET Core runtime,
// KHÔNG cần package Microsoft.AspNetCore.App.Internal.Assets mới của .NET 10.

using WiFiManager.Services;

var builder = WebApplication.CreateBuilder(args);
// Đảm bảo static web assets (kể cả từ framework) được phục vụ khi chạy trực tiếp từ source
builder.WebHost.UseStaticWebAssets();

// === Blazor Server (kiểu cổ điển, tương thích tốt) ===
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// === Wi-Fi Services ===
builder.Services.AddTransient<WpaSupplicantService>();
builder.Services.AddSingleton<DhcpClientService>();
builder.Services.AddSingleton<DnsConfiguratorService>();
builder.Services.AddScoped<WiFiStateService>();

// === Logging ===
builder.Logging.AddConsole();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

// Map SignalR hub cho Blazor Server
app.MapBlazorHub();

// Trang fallback cho tất cả route → _Host.cshtml
app.MapFallbackToPage("/_Host");

// === Log khởi động ===
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var useMock = app.Configuration.GetValue<bool>("WpaSupplicant:UseMock", false);
var socketPath = app.Configuration["WpaSupplicant:ControlSocket"] ?? "/run/wpa_supplicant/wlp0s20f3";

logger.LogInformation("=== Wi-Fi Manager khởi động ===");
logger.LogInformation("Chế độ: {Mode}", useMock ? "MOCK (Giả lập)" : "THỰC (wpa_supplicant)");
if (!useMock) logger.LogInformation("Socket: {Path}", socketPath);
logger.LogInformation("================================");

app.Run();
