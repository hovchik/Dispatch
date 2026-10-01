using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Dispatch.App.Services;
using Dispatch.App.ViewModels;
using Dispatch.App.Views;
using Dispatch.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.App;

public sealed class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices();
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            _services.GetRequiredService<ClipboardService>().Attach(window);
            _services.GetRequiredService<DialogService>().Attach(window);
            _services.GetRequiredService<DialogService>().HelpRequested = viewModel.ShowHelp;

            // Last line of defence: surface unexpected UI-thread errors instead of crashing.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                viewModel.ReportError(e.Exception.Message);
                e.Handled = true;
            };

            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            desktop.Exit += (_, _) => _services.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddDispatchCore(DispatchPaths.DefaultDatabase);

        services.AddSingleton<ClipboardService>();
        services.AddSingleton<IClipboardService>(sp => sp.GetRequiredService<ClipboardService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<Func<Dispatch.Infrastructure.Capture.CaptureProxy>>(sp => sp.GetRequiredService<Dispatch.Infrastructure.Capture.CaptureProxy>);
        services.AddSingleton<RequestTabServices>();
        services.AddSingleton<MainServices>();

        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<EnvironmentsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
