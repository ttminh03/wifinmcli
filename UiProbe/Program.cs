using Microsoft.Playwright;

internal sealed class Program
{
    private static async Task<string[]> VisibleTexts(ILocator locator)
    {
        var count = await locator.CountAsync();
        var result = new List<string>(capacity: count);
        for (var i = 0; i < count; i++)
        {
            var item = locator.Nth(i);
            if (await item.IsVisibleAsync())
            {
                var text = (await item.InnerTextAsync()).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }
        }

        return result.ToArray();
    }

    public static async Task Main()
    {
        var url = Environment.GetEnvironmentVariable("WIFIMANAGER_URL")
                  ?? (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? "http://127.0.0.1:5099/");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var page = await browser.NewPageAsync();

        page.Console += (_, e) => Console.WriteLine($"[console:{e.Type}] {e.Text}");
        page.PageError += (_, e) => Console.WriteLine($"[pageerror] {e}");
        page.RequestFailed += (_, e) => Console.WriteLine($"[requestfailed] {e.Url} {e.Failure}");
        page.Response += (_, r) =>
        {
            if (r.Url.Contains("/_framework/blazor.server.js", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[blazor.server.js] HTTP {r.Status} {r.Url}");
            }
        };

        Console.WriteLine($"Navigating to {url}");
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });

        await page.WaitForTimeoutAsync(1000);

        var scanButton = page.GetByRole(AriaRole.Button, new() { Name = "Scan" });
        var scanCount = await scanButton.CountAsync();
        Console.WriteLine($"Scan button count: {scanCount}");

        var disconnectButton = page.GetByRole(AriaRole.Button, new() { Name = "Disconnect" });
        var disconnectCount = await disconnectButton.CountAsync();
        Console.WriteLine($"Disconnect button count: {disconnectCount}");

        var connectedCard = page.Locator(".card-connected");
        Console.WriteLine($"Connected card count (initial): {await connectedCard.CountAsync()}");

        var errorBanners = page.Locator(".blazor-error-ui, .alert, .alert-danger, .alert-warning, .validation-summary-errors, .error, [role='alert']");
        var initialErrors = await VisibleTexts(errorBanners);
        Console.WriteLine($"Initial visible error banners: {initialErrors.Length}");
        foreach (var e in initialErrors) Console.WriteLine($"[error-ui] {e}");

        if (scanCount > 0)
        {
            var scanEnabled = await scanButton.First.IsEnabledAsync();
            Console.WriteLine($"Scan enabled: {scanEnabled}");

            try
            {
                if (!scanEnabled)
                {
                    Console.WriteLine("Scan is disabled; waiting up to 5s to enable...");
                    var stopAt = DateTimeOffset.UtcNow.AddSeconds(5);
                    while (DateTimeOffset.UtcNow < stopAt)
                    {
                        if (await scanButton.First.IsEnabledAsync())
                        {
                            scanEnabled = true;
                            break;
                        }

                        await page.WaitForTimeoutAsync(250);
                    }

                    Console.WriteLine($"Scan enabled after wait: {scanEnabled}");
                }

                Console.WriteLine("Clicking Scan...");
                await scanButton.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Scan click did not complete: {ex.GetType().Name}: {ex.Message}");
            }

            var scanningText = page.Locator("text=/Scanning\\.?\\.?\\.?/i");
            try
            {
                await scanningText.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                Console.WriteLine("Saw Scanning... indicator.");
            }
            catch
            {
                Console.WriteLine("Did not see Scanning... indicator within 5s.");
            }

            await page.WaitForTimeoutAsync(2000);
            var tables = page.Locator("table");
            Console.WriteLine($"Visible tables after Scan: {await tables.CountAsync()}");

            // Monitor Scan button enable/disable state for up to 15s after scan attempt
            Console.WriteLine("Observing Scan button enabled state for 15s after scan attempt...");
            bool? lastEnabled = null;
            var observeStopAt = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < observeStopAt)
            {
                var nowEnabled = await scanButton.First.IsEnabledAsync();
                if (lastEnabled is null || lastEnabled.Value != nowEnabled)
                {
                    Console.WriteLine($"Scan enabled state changed: {nowEnabled}");
                    lastEnabled = nowEnabled;
                }

                await page.WaitForTimeoutAsync(1000);
            }
            Console.WriteLine($"Scan enabled state at end of observation: {lastEnabled?.ToString() ?? "unknown"}");
        }
        else
        {
            Console.WriteLine("Scan button not found; skipping click.");
        }

        var afterScanErrors = await VisibleTexts(errorBanners);
        Console.WriteLine($"After-Scan visible error banners: {afterScanErrors.Length}");
        foreach (var e in afterScanErrors) Console.WriteLine($"[error-ui] {e}");

        if (disconnectCount > 0)
        {
            var disconnectEnabled = await disconnectButton.First.IsEnabledAsync();
            Console.WriteLine($"Disconnect enabled: {disconnectEnabled}");

            try
            {
                Console.WriteLine("Clicking Disconnect...");
                await disconnectButton.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Disconnect click did not complete: {ex.GetType().Name}: {ex.Message}");
            }

            await page.WaitForTimeoutAsync(2000);

            var afterDisconnectErrors = await VisibleTexts(errorBanners);
            Console.WriteLine($"After-Disconnect visible error banners: {afterDisconnectErrors.Length}");
            foreach (var e in afterDisconnectErrors) Console.WriteLine($"[error-ui] {e}");

            Console.WriteLine($"Connected card count (after Disconnect): {await connectedCard.CountAsync()}");
            Console.WriteLine($"Disconnect button count (after Disconnect): {await disconnectButton.CountAsync()}");

            // Check Scan button enabled state after disconnect
            if (scanCount > 0)
            {
                Console.WriteLine($"Scan enabled after Disconnect: {await scanButton.First.IsEnabledAsync()}");
            }
        }
        else
        {
            Console.WriteLine("Disconnect button not found; skipping click.");
        }

        var suffix = url.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("/", "_")
            .Replace(":", "_");
        var screenshotPath = $"/tmp/wifimanager_probe_{suffix}.png";
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = true });
        Console.WriteLine($"Saved screenshot: {screenshotPath}");
    }
}
