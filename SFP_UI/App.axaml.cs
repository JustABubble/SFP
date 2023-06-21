#region

using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using SFP.Models;
using SFP.Models.Injection;
using SFP.Properties;
using SFP_UI.Models;
using SFP_UI.ViewModels;
using SFP_UI.Views;

#endregion

namespace SFP_UI;

public class App : Application
{
    public App()
    {
        DataContext = new AppViewModel();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (!Settings.Default.StartMinimized || !Settings.Default.MinimizeToTray)
        {
            StartMainWindow();
        }

        base.OnFrameworkInitializationCompleted();

        if (Design.IsDesignMode)
        {
            return;
        }

        SetIconsState(Settings.Default.ShowTrayIcon);

        Injector.SetColorScheme(ActualThemeVariant.ToString());
        ActualThemeVariantChanged += (_, _) =>
        {
            Injector.SetColorScheme(ActualThemeVariant.ToString());
            Injector.UpdateColorScheme();
        };

        await HandleStartupTasks();

        if (Settings.Default.CheckForUpdates)
        {
            Dispatcher.UIThread.Post(() => _ = UpdateChecker.CheckForUpdates());
        }
    }

    private static async Task HandleStartupTasks()
    {
        await Task.Run(Steam.StartMonitorSteam);

        if (Settings.Default.InjectOnAppStart)
        {
            await Task.Run(Steam.TryInject);
        }

        if (Settings.Default.RunSteamOnStart)
        {
            await Task.Run(() => Steam.StartSteam(Settings.Default.SteamLaunchArgs));
        }
    }

    private static void SetIconsState(bool state)
    {
        var icons = TrayIcon.GetIcons(Current!);
        if (icons == null)
        {
            return;
        }
        foreach (var icon in icons)
        {
            icon.IsVisible = state;
        }
    }

    public static void SetApplicationTheme(string themeVariantString)
    {
        var faTheme = Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (faTheme != null)
        {
            faTheme.PreferSystemTheme = themeVariantString == "System Default";
        }

        Current!.RequestedThemeVariant = themeVariantString switch
        {
            FluentAvaloniaTheme.DarkModeString => ThemeVariant.Dark,
            FluentAvaloniaTheme.LightModeString => ThemeVariant.Light,
            FluentAvaloniaTheme.HighContrastModeString => FluentAvaloniaTheme.HighContrastTheme,
            _ => ThemeVariant.Default
        };
    }

    public static void StartMainWindow()
    {
        if (MainWindow.Instance is not null ||
            Current!.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        desktop.MainWindow = new MainWindow();
    }

    public static void QuitApplication()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log.Logger.Info("Quitting");
                lifetime.Shutdown();
            });
        }
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void TrayIcon_OnClicked(object? sender, EventArgs e)
    {
        MainWindow.ShowWindow();
    }
}
