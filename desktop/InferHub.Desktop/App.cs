using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;

namespace InferHub.Desktop;

/// <summary>The application: the Fluent theme, the tray icon, the one window (phase 115).</summary>
public sealed class App : Application
{
    private MainWindow? window;
    private AppController? controller;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Name = "InferHub";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            controller = new AppController(DesktopSettings.Load());
            window = new MainWindow(controller);
            desktop.MainWindow = window;
            window.Show();

            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://InferHub.Desktop/Assets/inferhub-32.png"))),
                ToolTipText = "InferHub",
                Menu = TrayMenu(desktop)
            };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, [tray]);

            desktop.Exit += (_, _) => controller.Dispose();
            _ = controller.StartAsync();

            if (Environment.GetEnvironmentVariable("INFERHUB_DESKTOP_SCREENSHOTS") is { Length: > 0 } shots)
            {
                Avalonia.Threading.DispatcherTimer.RunOnce(async () => await window.SaveScreenshotsAsync(shots), TimeSpan.FromSeconds(15));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private NativeMenu TrayMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open InferHub");
        open.Click += (_, _) => ShowWindow();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());

        foreach (var (label, mode) in new[] { ("Share with the network", "always"), ("Share when I am not using it", "idle"), ("Pause sharing", "paused") })
        {
            var item = new NativeMenuItem(label);
            item.Click += async (_, _) => await controller!.SetSharingAsync(mode);
            menu.Add(item);
        }

        menu.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            controller?.Dispose();
            desktop.Shutdown();
        };
        menu.Add(quit);
        return menu;
    }

    private void ShowWindow()
    {
        if (window is null)
        {
            return;
        }

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
