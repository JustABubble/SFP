#region

using PuppeteerSharp;

#endregion

namespace SFP.Models.Injection;

public static class Injector
{
    private static PuppeteerSharp.Browser? s_browser;
    private static bool s_webkitHooked;
    private static bool s_isInjected;
    private static readonly SemaphoreSlim s_semaphore = new(1, 1);
    public static bool IsInjected => s_isInjected && s_browser != null;

    public static event EventHandler? InjectionStateChanged;

    public static async Task StartInjectionAsync(bool noError = false)
    {
        if (s_browser is { IsConnected: true })
        {
            return;
        }

        if (!await s_semaphore.WaitAsync(TimeSpan.Zero))
        {
            return;
        }

        try
        {
            string browser = (await Browser.GetBrowserAsync()).WebSocketDebuggerUrl!;
            ConnectOptions options = new() { BrowserWSEndpoint = browser, DefaultViewport = null };

            Log.Logger.Info("Connecting to " + browser);
            s_browser = await Puppeteer.ConnectAsync(options);
            s_browser.Disconnected += OnDisconnected;
            Log.Logger.Info("Connected");
            s_browser.TargetCreated += Browser_TargetUpdate;
            s_browser.TargetChanged += Browser_TargetUpdate;
            await InjectAsync();
            s_isInjected = true;
            InjectionStateChanged?.Invoke(null, EventArgs.Empty);
            Log.Logger.Info("Injection finished");
        }
        catch (Exception e)
        {
            StopInjection();
            if (noError)
            {
                return;
            }

            Log.Logger.Error(e);
        }
        finally
        {
            s_semaphore.Release();
        }
    }

    private static async Task InjectAsync()
    {
        if (s_browser == null)
        {
            Log.Logger.Warn("Inject was called but CEF instance is not connected");
            return;
        }

        Target[]? targets = s_browser.Targets();
        Log.Logger.Info("Found " + targets.Length + " targets");
        foreach (Target? target in targets)
        {
            Page? page = await target.PageAsync();
            await ProcessPage(page);
        }
    }

    public static void StopInjection()
    {
        if (s_browser?.IsConnected ?? false)
        {
            Log.Logger.Info("Disconnecting from Steam instance");
        }

        s_webkitHooked = false;
        s_isInjected = false;
        s_browser?.Disconnect();
        s_browser = null;
        InjectionStateChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void OnDisconnected(object? sender, EventArgs e)
    {
        Log.Logger.Info("Disconnected from Steam instance");
        StopInjection();
    }

    private static async void Browser_TargetUpdate(object? sender, TargetChangedArgs e)
    {
        Page? page = await e.Target.PageAsync();
        await ProcessPage(page);
    }

    private static async Task ProcessPage(Page? page)
    {
        if (page == null)
        {
            return;
        }

        if (page.Url.Contains("store.steampowered.com") || page.Url.Contains("steamcommunity.com") ||
            page.Url.Contains("!--/library/"))
        {
            if (!s_webkitHooked)
            {
                page.DOMContentLoaded += Page_Load;
                s_webkitHooked = true;
            }

            await InjectCssAsync(page, "webkit.css", "Steam web", client: false);
            return;
        }

        string? title = await page.GetTitleAsync();

        if (title == "Steam Big Picture Mode" || title.StartsWith("QuickAccess_") || title.StartsWith("MainMenu_") ||
            title.StartsWith(@"notificationtoasts_"))
        {
            await InjectCssAsync(page, @"bigpicture.custom.css", "Steam Big Picture Mode");
            return;
        }


        if (title == "Steam" || title == "Steam Settings" || title == "GameOverview" || title.EndsWith("Menu") ||
            title.EndsWith(@"Supernav") || title.StartsWith("SP Overlay:"))
        {
            await InjectCssAsync(page, @"libraryroot.custom.css", "Steam client");
            return;
        }

        if (await page.QuerySelectorAsync(".friendsui-container") != null)
        {
            await InjectCssAsync(page, "friends.custom.css", "Friends and Chat");
        }
    }

    private static async void Page_Load(object? sender, EventArgs _)
    {
        if (sender is Page page)
        {
            await InjectCssAsync(page, "webkit.css", "Steam web", client: false);
        }
    }

    private static async Task InjectCssAsync(Page page, string cssFileRelativePath, string tabFriendlyName,
        bool retry = true, bool silent = false, bool client = true)
    {
        string cssInjectString =
            $$"""
                function injectCss() {
                    if (document.getElementById('{{page.Target.TargetId}}') !== null) return;
                    const link = document.createElement('link');
                    link.id = '{{page.Target.TargetId}}';
                    link.rel = 'stylesheet';
                    link.type = 'text/css';
                    link.href = 'https://steamloopback.host/{{cssFileRelativePath}}';
                    document.head.append(link);
                }
                if ((document.readyState === 'loading') && '{{!client}}' === 'True') {
                    addEventListener('DOMContentLoaded', injectCss);
                } else {
                    injectCss();
                }
                """;
        try
        {
            await page.EvaluateExpressionAsync(cssInjectString);
            if (!silent)
            {
                Log.Logger.Info("Injected into " + tabFriendlyName);
            }
        }
        catch (EvaluationFailedException e)
        {
            if (!silent && tabFriendlyName != "Steam web")
            {
                Log.Logger.Error(tabFriendlyName + " failed to inject: " + e);
                Log.Logger.Info("Retrying...");
            }

            if (retry)
            {
                await InjectCssAsync(page, cssFileRelativePath, tabFriendlyName, false, silent, client);
            }
        }
    }
}