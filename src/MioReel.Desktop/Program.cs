using Avalonia;
using Aviora.Controls;
using Aviora.Presentation.Dialogs;
using Aviora.Presentation.Drawers;
using Aviora.Presentation.Loadings;
using Aviora.Presentation.Toasts;
using FrameFlux.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using MioReel.Core.Abstractions;
using MioReel.Desktop.Services;
using MioReel.Desktop.Services.Hotkeys;
using MioReel.Desktop.ViewModels;
using MioReel.Desktop.Views;
using MioReel.Infrastructure;
using ReactiveUI.Avalonia.Splat;

namespace MioReel.Desktop;

public static class Program
{
    private const string SingleInstanceAppId = "MioReel.Desktop";

    public static IServiceProvider Services { get; private set; } = null!;
    internal static SingleInstanceGuard? InstanceGuard { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        using var guard = SingleInstanceGuard.TryAcquire(SingleInstanceAppId);
        if (guard is null)
        {
            SingleInstanceGuard.SignalExistingInstance(SingleInstanceAppId);
            return;
        }

        InstanceGuard = guard;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceGuard = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

#if FF_WINDOWS
        if (OperatingSystem.IsWindows())
            builder = builder.UseFrameFluxWindows();
#endif
#if FF_LINUX
        if (OperatingSystem.IsLinux())
            builder = builder.UseFrameFluxLinux();
#endif

        return builder.UseReactiveUIWithMicrosoftDependencyResolver(
            services =>
            {
                services.AddMioReelInfrastructure();

                // Aviora Presentation Services (same singleton instance serves both VM and Host interfaces)
                services.AddSingleton<DialogService>();
                services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
                services.AddSingleton<IDialogHostService>(sp => sp.GetRequiredService<DialogService>());

                services.AddSingleton<DrawerService>();
                services.AddSingleton<IDrawerService>(sp => sp.GetRequiredService<DrawerService>());
                services.AddSingleton<IDrawerHostService>(sp => sp.GetRequiredService<DrawerService>());

                services.AddSingleton<LoadingService>();
                services.AddSingleton<ILoadingService>(sp => sp.GetRequiredService<LoadingService>());
                services.AddSingleton<ILoadingHostService>(sp => sp.GetRequiredService<LoadingService>());

                services.AddSingleton<ToastService>();
                services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
                services.AddSingleton<IToastHostService>(sp => sp.GetRequiredService<ToastService>());

                services.AddSingleton<IGlobalHotkeyService>(_ => GlobalHotkeyServiceFactory.Create());
                services.AddTransient<LoginWindowViewModel>();
                services.AddTransient<LoginWindow>();
                services.AddSingleton<PlayerViewModel>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            },
            withResolver: sp => Services = sp!);
    }
}
