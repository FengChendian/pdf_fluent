using Avalonia;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.FontAwesome;
using pdf_fluent.Diagnostics;
using pdf_fluent.Services;
using System;

namespace pdf_fluent;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        StartupTrace.Start();

        using var coordinator = new SingleInstanceCoordinator();
        StartupTrace.Mark("SingleInstanceCoordinator");
        if (!coordinator.IsPrimary && coordinator.TrySendAsync(args).GetAwaiter().GetResult())
            return;

        SingleInstance = coordinator.IsPrimary ? coordinator : null;
        try
        {
            var builder = BuildAvaloniaApp();
            StartupTrace.Mark("BuildAvaloniaApp");
            builder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance = null;
        }
    }

    internal static SingleInstanceCoordinator? SingleInstance { get; private set; }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current.Register<FontAwesomeIconProvider>();

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
    }
}