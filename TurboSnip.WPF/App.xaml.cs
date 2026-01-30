using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration; // Add this
using TurboSnip.WPF.Services;
using TurboSnip.WPF.ViewModels;

namespace TurboSnip.WPF;

public partial class App : System.Windows.Application
{
    public IServiceProvider Services { get; }

    public App()
    {
        Services = ConfigureServices();
        InitializeComponent();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 1. Configuration
        var builder = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        var configuration = builder.Build();
        var appConfig = new AppConfig();
        configuration.Bind(appConfig);

        services.AddSingleton(appConfig);

        // Services
        services.AddSingleton<IOcrService, PaddleOcrService>();
        services.AddSingleton<ILlmService, LlamaService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<ISnippingService, SnippingService>();
        services.AddSingleton<HardwareDetectionService>();

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views
        services.AddTransient<MainWindow>();
        services.AddTransient<StartupWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Global Exception Hooks
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception ?? new Exception("Unknown AppDomain Exception");
            GlobalExceptionHandler.HandleException(ex, "AppDomain.UnhandledException", isTerminating: args.IsTerminating);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            GlobalExceptionHandler.HandleException(args.Exception, "DispatcherUnhandledException");
            args.Handled = true; // Prevent crash if possible
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            GlobalExceptionHandler.HandleException(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved(); // Prevent crash
        };

        // Prevent partial shutdown when StartupWindow closes
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        // 1. Start OCR Warmup in background (Fire & Forget)
        // This speeds up the first snip while user is configuring settings
        Task.Run(async () =>
        {
            try
            {
                var ocr = Services.GetRequiredService<IOcrService>();
                await ocr.WarmupAsync();
            }
            catch (Exception ex)
            {
                // Log warmup errors for debugging instead of silently ignoring
                System.Diagnostics.Debug.WriteLine($"OCR Warmup failed: {ex.Message}");
            }
        });

        // 2. Determine Flow based on First Run
        var appConfig = Services.GetRequiredService<AppConfig>(); // Get Singleton
        bool proceedToMain = false;

        if (appConfig.IsFirstRun)
        {
            // Show Startup Wizard
            var startupWindow = Services.GetRequiredService<StartupWindow>();
            if (startupWindow.ShowDialog() == true)
            {
                proceedToMain = true;
            }
        }
        else
        {
            // Skip Startup Wizard
            proceedToMain = true;
        }

        if (proceedToMain)
        {
            // 3. Show Main Window
            var mainWindow = Services.GetRequiredService<MainWindow>();

            // Ensure language is applied if skipping startup
            if (!string.IsNullOrEmpty(appConfig.Language))
            {
                var culture = new System.Globalization.CultureInfo(appConfig.Language);
                System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                TurboSnip.WPF.Properties.Resources.Culture = culture;
            }

            // Register as main window and show
            MainWindow = mainWindow;
            mainWindow.Show();

            // Revert shutdown mode so closing main window kills app
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        else
        {
            // User closed/cancelled -> Shutdown
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.OnExit(e);
    }
}
