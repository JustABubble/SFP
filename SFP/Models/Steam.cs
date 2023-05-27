#region

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FileWatcherEx;
using Gameloop.Vdf;
using SFP.Models.Injection;
using SFP.Properties;

#endregion

namespace SFP.Models;

public static class Steam
{
    private static FileSystemWatcherEx? s_watcher;
    private static Process? s_steamProcess;
    private static bool s_injectOnce;
    private static readonly SemaphoreSlim s_semaphore = new(1, 1);

    private static readonly int s_processAmount = OperatingSystem.IsWindows() ? 3 : 6;
    public static bool IsSteamWebHelperRunning => SteamWebHelperProcesses.Length > s_processAmount;
    public static bool IsSteamRunning => SteamProcess is not null;

    private static Process[] SteamWebHelperProcesses => Process.GetProcessesByName(@"steamwebhelper")
        .Where(p => p.ProcessName.Equals(@"steamwebhelper", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static Process? SteamProcess => Process.GetProcessesByName("steam")
        .FirstOrDefault(p => p.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase));

    internal static string MillenniumPath => Path.Join(SteamDir, "User32.dll");

    private static string? SteamRootDir
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return SteamDir;
            }

            if (OperatingSystem.IsLinux())
            {
                return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam");
            }

            return OperatingSystem.IsMacOS()
                ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library",
                    "Application Support", "Steam")
                : null;
        }
    }

    public static string? SteamDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.Default.SteamDirectory))
            {
                return Settings.Default.SteamDirectory;
            }

            if (OperatingSystem.IsWindows())
            {
                return GetRegistryData(@"SOFTWARE\Valve\Steam", "SteamPath")?.ToString()?.Replace(@"/", @"\");
            }

            return OperatingSystem.IsLinux()
                ? Path.Join(SteamRootDir, "steam")
                :
                // OSX
                Path.Join(SteamRootDir, "Steam.AppBundle", "Steam", "Contents", "MacOS");
        }
    }

    private static string SteamUiDir => Path.Join(SteamDir, "steamui");

    public static string SkinsDir => Path.Join(SteamUiDir, "skins");

    private static string SteamExe => OperatingSystem.IsWindows() ? Path.Join(SteamDir, "Steam.exe") : "steam";

    public static string GetSkinDir()
    {
        return Path.Join(SteamUiDir, GetRelativeSkinDir());
    }

    public static string GetRelativeSkinDir()
    {
        string relativeSkinDir;
        var selectedSkin = Settings.Default.SelectedSkin;
        if (string.IsNullOrWhiteSpace(selectedSkin) || selectedSkin == "steamui")
        {
            relativeSkinDir = string.Empty;
        }
        else if (selectedSkin == "skins\\steamui")
        {
            relativeSkinDir = "skins/steamui";
        }
        else
        {
            relativeSkinDir = $"skins/{selectedSkin}";
        }
        return relativeSkinDir;
    }

    public static event EventHandler? SteamStarted;
    public static event EventHandler? SteamStopped;

    [SuppressMessage("CodeSmell", "ERP022:Unobserved exception in generic exception handler",
        Justification = "ok if null")]
    private static object? GetRegistryData(string key, string valueName)
    {
        if (OperatingSystem.IsWindows())
        {
            return Utils.GetRegistryData(key, valueName);
        }

        try
        {
            dynamic reg = VdfConvert.Deserialize(File.ReadAllText(Path.Join(SteamRootDir, "registry.vdf")));
            var kn = @$"HKCU/{key.Replace('\\', '/')}/{valueName}";
            var currentVal = reg.Value;
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var keyPart in kn.Split('/'))
            {
                currentVal = currentVal[keyPart];
            }

            return int.TryParse(currentVal.Value, out int val) ? val : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void ShutDownSteam(Process steamProcess)
    {
        if (steamProcess.HasExited)
        {
            return;
        }

        Log.Logger.Info("Shutting down Steam");
        if (IsSteamWebHelperRunning)
        {
            _ = Process.Start(SteamExe, "-shutdown");
        }
        else
        {
            steamProcess.Kill();
        }
    }

    [SuppressMessage("CodeSmell", "ERP022:Unobserved exception in a generic exception handler")]
    public static async Task StartSteam(string? args = null)
    {
        if (IsSteamRunning)
        {
            return;
        }

        args ??= Settings.Default.SteamLaunchArgs;
        if (!args.Contains("-cef-enable-debugging"))
        {
            args += " -cef-enable-debugging";
            args = args.Trim();
        }


        if (File.Exists(MillenniumPath))
        {
            Log.Logger.Warn("Millennium Patcher install detected, disabling millenium patcher...");
            try
            {
                var newPath = Path.Join(MillenniumPath, ".bak");
                File.Move(MillenniumPath, newPath, true);
            }
            catch (Exception e)
            {
                Log.Logger.Warn("Could not disable Millennium patcher, aborting as it is incompatible with SFP");
                Log.Logger.Error(e);
                return;
            }
        }

        Log.Logger.Info("Starting Steam");
        _ = Process.Start(SteamExe, args);
        if (Settings.Default.InjectOnSteamStart)
        {
            await TryInject();
        }
    }

    public static async void RunRestartSteam()
    {
        await Task.Run(() => RestartSteam());
    }

    private static async Task RestartSteam(string? args = null)
    {
        if (IsSteamRunning)
        {
            s_steamProcess = SteamProcess;
            s_steamProcess!.EnableRaisingEvents = true;
            s_steamProcess.Exited -= OnSteamExited;
            s_steamProcess.Exited += OnSteamExited;
            ShutDownSteam(s_steamProcess);
        }
        else
        {
            await StartSteam(args);
        }
    }

    private static async void OnSteamExited(object? sender, EventArgs e)
    {
        s_steamProcess?.Dispose();
        s_steamProcess = null;
        await StartSteam();
    }

    public static void StartMonitorSteam()
    {
        if (s_watcher != null || string.IsNullOrWhiteSpace(SteamDir))
        {
            return;
        }

        s_watcher = new FileSystemWatcherEx(SteamDir) { Filter = ".crash" };
        s_watcher.OnCreated += OnCrashFileCreated;
        s_watcher.OnDeleted += OnCrashFileDeleted;
        s_watcher.OnChanged += OnCrashFileCreated;
        try
        {
            s_watcher.Start();
            Log.Logger.Info("Monitoring Steam state");
        }
        catch (Exception e)
        {
            Log.Logger.Error("Failed to start Steam monitorer");
            Log.Logger.Debug(e);
        }
    }

    private static async void OnCrashFileCreated(object? sender, FileChangedEvent e)
    {
        SteamStarted?.Invoke(null, EventArgs.Empty);
        if (Settings.Default.InjectOnSteamStart || s_injectOnce)
        {
            s_injectOnce = false;
            await TryInject();
        }
    }

    private static void OnCrashFileDeleted(object? sender, FileChangedEvent e)
    {
        SteamStopped?.Invoke(null, EventArgs.Empty);
    }

    public static List<string> GetCommandLine()
    {
        return Utils.GetCommandLine(SteamProcess);
    }

    public static async void RunTryInject()
    {
        await Task.Run(TryInject);
    }

    public static async Task TryInject()
    {
        if (!await s_semaphore.WaitAsync(TimeSpan.Zero))
        {
            return;
        }

        try
        {
            if (!IsSteamRunning)
            {
                return;
            }

            if (Settings.Default.ForceSteamArgs)
            {
                var argumentsMissing = await CheckForMissingArgumentsAsync();
                if (argumentsMissing)
                {
                    s_injectOnce = true;
                    return;
                }
            }

            while (!IsSteamWebHelperRunning)
            {
                if (!IsSteamRunning)
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            await Injector.StartInjectionAsync(true);
        }
        catch (Exception ex)
        {
            Log.Logger.Warn("Failed to inject");
            Log.Logger.Error(ex.ToString());
        }
        finally
        {
            s_semaphore.Release();
        }
    }

    private static async Task<bool> CheckForMissingArgumentsAsync()
    {
        if (!IsSteamRunning)
        {
            Log.Logger.Error("Steam is not running, cannot check arguments");
            return false;
        }

        var cmdLine = Utils.GetCommandLine(SteamProcess);
        if (!cmdLine.Any())
        {
            Log.Logger.Error("Arguments are empty, cannot check arguments");
            return false;
        }

        var argumentMissing = Settings.Default.SteamLaunchArgs.Split(' ')
            .Any(arg => !cmdLine.Contains(arg));

        if (!argumentMissing)
        {
            return false;
        }

        Log.Logger.Info("Steam process detected with missing launch arguments, restarting...");
        await RestartSteam();
        return true;
    }
}
