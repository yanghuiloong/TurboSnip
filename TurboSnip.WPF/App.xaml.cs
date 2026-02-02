using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration; // Add this
using TurboSnip.WPF.Services;
using TurboSnip.WPF.ViewModels;
using TurboSnip.WPF.Views;

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

        // ============================================
        // STEP 0: Check Prerequisites (VC++ and CUDA)
        // ============================================
        // This must happen BEFORE any LLamaSharp code runs
        if (!CheckAndInstallPrerequisites())
        {
            Shutdown();
            return;
        }

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
            // 3. Show Main Window FIRST (so user sees the app)
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
            
            // 4. Show Model Loading Window (blocks until ready)
            // This ensures user cannot use the app until GPU is ready
            PerformModelWarmup(appConfig);
        }
        else
        {
            // User closed/cancelled -> Shutdown
            Shutdown();
        }
    }
    
    /// <summary>
    /// Show model loading window and wait for warmup to complete.
    /// This blocks user interaction until GPU is ready.
    /// </summary>
    private void PerformModelWarmup(AppConfig config)
    {
        try
        {
            var llmService = Services.GetRequiredService<ILlmService>();
            var hotkeyService = Services.GetRequiredService<IHotkeyService>();
            var modelName = config.Llm.DefaultModelName;
            
            var warmupWindow = new GpuWarmupWindow(llmService, modelName, hotkeyService);
            
            // Set owner only if MainWindow exists and is not the warmup window itself
            if (MainWindow != null && MainWindow != warmupWindow && MainWindow.IsLoaded)
            {
                warmupWindow.Owner = MainWindow;
            }
            
            warmupWindow.ShowDialog();
            
            System.Diagnostics.Debug.WriteLine($"Model warmup complete: {warmupWindow.WarmupComplete}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Model warmup failed: {ex.Message}");
            // Continue anyway
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
    
    /// <summary>
    /// Check and install prerequisites (VC++ Runtime and CUDA).
    /// Returns true if all prerequisites are met, false if user cancelled.
    /// </summary>
    private bool CheckAndInstallPrerequisites()
    {
        try
        {
            // Quick check if all prerequisites are already installed
            var (hasGpu, hasVcRedist, hasCuda) = PrerequisiteInstallerService.GetPrerequisiteStatus();
            
            // If no NVIDIA GPU, CUDA is not required
            if (!hasGpu)
                hasCuda = true;
            
            // All good, proceed
            if (hasVcRedist && hasCuda)
                return true;
            
            // Show prerequisite setup window
            var setupWindow = new PrerequisiteSetupWindow();
            var result = setupWindow.ShowDialog();
            
            // User clicked exit
            if (setupWindow.UserRequestedExit)
                return false;
            
            // Check if prerequisites were installed successfully
            return setupWindow.AllPrerequisitesMet;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Prerequisite check failed: {ex.Message}");
            // Allow to continue even if check fails
            return true;
        }
    }
}
