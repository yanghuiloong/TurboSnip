using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TurboSnip.WPF.Services;
using TurboSnip.WPF.Views; // For AboutWindow
using System.Text.Json.Nodes; // For Config Saving

namespace TurboSnip.WPF.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IOcrService _ocrService;
    private readonly ILlmService _llmService;
    private readonly IHotkeyService _hotkeyService;

    // Cancellation token source for the current pipeline execution
    private CancellationTokenSource? _cts;

    // Model filename constants for maintainability
    private const string ModelFile1_5B = "qwen2.5-1.5b-instruct-q4_k_m.gguf";
    private const string ModelFile3B = "qwen2.5-3b-instruct-q4_k_m.gguf";
    private const string ModelFile4B = "Qwen3-4B-Instruct-2507-UD-Q4_K_XL.gguf";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOcrPlaceholderVisible))]
    [NotifyPropertyChangedFor(nameof(IsManualTranslateVisible))]
    [NotifyPropertyChangedFor(nameof(IsTranslationPlaceholderVisible))]
    private string _originalText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslationPlaceholderVisible))]
    [NotifyPropertyChangedFor(nameof(IsManualTranslateVisible))]
    private string _translatedText = "";

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _metricsText = "Ready";

    [ObservableProperty]
    private BitmapSource? _capturedImage;

    // --- Localization Support ---
    public record LanguageOption(string Name, string Code);

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<LanguageOption> _languageOptions = [];

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    // --- UI State ---
    [ObservableProperty]
    private bool _isHelpVisible;

    // --- Services ---
    private readonly AppConfig _config;

    // --- Commands ---
    [RelayCommand]
    private void ShowHelp() => IsHelpVisible = true;

    [RelayCommand]
    private void CloseHelp()
    {
        IsHelpVisible = false;
        if (_config.IsFirstRun)
        {
            _config.IsFirstRun = false;
            // Ideally save Config here. For now, rely on app lifecycle or specific save.
            // But StartupWindow saves settings manually. We should probably implement Save here too.
            SaveConfig();
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var about = new TurboSnip.WPF.Views.AboutWindow
        {
            DataContext = this,
            Owner = System.Windows.Application.Current.MainWindow
        };
        about.ShowDialog();
    }

    private static void SaveConfig()
    {
        try
        {
            // Simple re-implementation of saving IsFirstRun to appsettings.json
            // We only need to touch IsFirstRun, but parsing/writing full JSON is safest.
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var jNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [];
            jNode["IsFirstRun"] = false;
            File.WriteAllText(path, jNode.ToString());
        }
        catch { /* Ignore */ }
    }

    // Resource Manager for Localization
    private readonly System.Resources.ResourceManager _resourceManager = new("TurboSnip.WPF.Properties.Resources", typeof(MainViewModel).Assembly);

    private string GetString(string key)
    {
        if (SelectedLanguage == null) return "";
        var culture = new System.Globalization.CultureInfo(SelectedLanguage.Code);
        return _resourceManager.GetString(key, culture) ?? key;
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value != null)
        {
            UpdateUiStrings();
            // Re-init options to refresh their text (e.g. Pros/Cons)
            InitOptions();
        }
    }

    // localized strings
    [ObservableProperty] private string _uiTitle = "TurboSnip";
    [ObservableProperty] private string _uiCaptureBtn = "Capture";
    [ObservableProperty] private string _uiCancelBtn = "Cancel";
    [ObservableProperty] private string _uiModelLabel = "Model:";
    [ObservableProperty] private string _uiHardwareLabel = "Hardware:";
    [ObservableProperty] private string _uiCopyBtn = "Copy";
    [ObservableProperty] private string _uiCopyImageBtn = "Copy Image";
    [ObservableProperty] private string _uiSnipBtn = "Screen Snip";
    [ObservableProperty] private string _uiLanguageLabel = "Language:";
    [ObservableProperty] private string _uiSourceLabel = "Captured Text";
    [ObservableProperty] private string _uiTargetLabel = "Translation";

    // --- UX Overlays ---
    [ObservableProperty] private string _screenshotHeader = "Screenshot";
    [ObservableProperty] private string _screenshotOverlayText = "Press Alt+Q to Capture";
    [ObservableProperty] private string _ocrOverlayText = "Waiting for capture...";
    [ObservableProperty] private string _translationOverlayText = "Waiting for text...";

    // --- Help Window Strings (Localized) ---
    [ObservableProperty] private string _helpTitle = "";
    [ObservableProperty] private string _helpFeaturesTitle = "";
    [ObservableProperty] private string _helpPrivacyRaw = "";
    [ObservableProperty] private string _helpLiveCaptureRaw = "";
    [ObservableProperty] private string _helpPerformanceRaw = "";
    [ObservableProperty] private string _helpUsageTitle = "";
    [ObservableProperty] private string _helpStep1Raw = "";
    [ObservableProperty] private string _helpStep2Raw = "";
    [ObservableProperty] private string _helpStep3Raw = "";
    [ObservableProperty] private string _helpStep4Raw = "";
    [ObservableProperty] private string _helpGotIt = "";

    // --- About Window Strings (Localized) ---
    [ObservableProperty] private string _aboutTitle = "";
    [ObservableProperty] private string _aboutDesignedBy = "";
    [ObservableProperty] private string _aboutPoweredBy = "";
    [ObservableProperty] private string _aboutClose = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOcrPlaceholderVisible))]
    private bool _isOcrBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslationPlaceholderVisible))]
    [NotifyPropertyChangedFor(nameof(IsManualTranslateVisible))]
    private bool _isTranslationBusy;

    // Logic: Visible if NOT busy, Text is Present, but Translation is Empty
    public bool IsManualTranslateVisible => !IsTranslationBusy && !string.IsNullOrEmpty(OriginalText) && string.IsNullOrEmpty(TranslatedText);

    public bool IsOcrPlaceholderVisible => !IsOcrBusy && string.IsNullOrEmpty(OriginalText);
    public bool IsTranslationPlaceholderVisible => !IsTranslationBusy && string.IsNullOrEmpty(TranslatedText) && !IsManualTranslateVisible;

    [ObservableProperty] private string _translateBtnText = "Translate Text";

    [RelayCommand]
    private async Task TranslateManual()
    {
        if (string.IsNullOrEmpty(OriginalText)) return;

        // Directly trigger translation stream
        // Reuse same logic as ProcessOcrResult but with existing text
        await TranslateAndDisplay(OriginalText);
    }

    private void UpdateUiStrings()
    {
        UiTitle = GetString("AppTitle");
        UiCaptureBtn = GetString("CaptureBtn");
        UiSnipBtn = GetString("SnipBtn");
        UiCancelBtn = GetString("CancelBtn");

        // UX Strings
        ScreenshotHeader = GetString("ScreenshotHeader");
        ScreenshotOverlayText = GetString("ScreenshotOverlayText");

        // Only update these if NOT busy (to avoid overwriting progress messages)
        if (!IsProcessing)
        {
            OcrOverlayText = GetString("OcrOverlayWaiting");
            TranslationOverlayText = GetString("TranslationOverlayWaiting");
            StatusMessage = GetString("StatusReady");
        }

        UiModelLabel = GetString("ModelLabel");
        UiHardwareLabel = GetString("HardwareLabel");
        UiCopyBtn = GetString("CopyBtn");
        UiCopyImageBtn = GetString("CopyImageBtn");
        UiLanguageLabel = GetString("LanguageLabel");
        UiSourceLabel = GetString("SourceLabel");
        UiTargetLabel = GetString("TargetLabel");
        TranslateBtnText = GetString("TranslateBtn");

        // Help Window Strings
        HelpTitle = GetString("Help_Title");
        HelpFeaturesTitle = GetString("Help_Features_Title");
        HelpPrivacyRaw = GetString("Help_Privacy");
        HelpLiveCaptureRaw = GetString("Help_LiveCapture");
        HelpPerformanceRaw = GetString("Help_Performance");
        HelpUsageTitle = GetString("Help_Usage_Title");
        HelpStep1Raw = GetString("Help_Step1");
        HelpStep2Raw = GetString("Help_Step2");
        HelpStep3Raw = GetString("Help_Step3");
        HelpStep4Raw = GetString("Help_Step4");
        HelpGotIt = GetString("Help_GotIt");

        // About Window Strings
        AboutTitle = GetString("About_Title");
        AboutDesignedBy = GetString("About_DesignedBy");
        AboutPoweredBy = GetString("About_PoweredBy");
        AboutClose = GetString("About_Close");

        // Ensure StatusMessage is updated if we assume ready state, but strictly we should respect current state
        if (!IsProcessing && (StatusMessage == "Ready (Press Alt+Q to Snip)" || StatusMessage.StartsWith("就绪")))
        {
            StatusMessage = GetString("StatusReady");
        }
    }

    // --- Dropdown Support ---
    public record ModelOption(string Name, string Filename, string ModelInfo);
    public record HardwareOption(string Name, bool IsGpu, string DeviceInfo);

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<ModelOption> _modelOptions = [];

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<HardwareOption> _hardwareOptions = [];

    [ObservableProperty]
    private ModelOption? _selectedModel;

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        if (value != null)
        {
            _llmService.SwitchModel(value.Filename);
            _ = _llmService.InitializeAsync(); // Force Init UX
        }
    }

    [ObservableProperty]
    private HardwareOption? _selectedHardware;

    partial void OnSelectedHardwareChanged(HardwareOption? value)
    {
        if (value != null)
        {
            _llmService.SwitchHardware(value.IsGpu);
        }
    }

    // --- Constructor Logic ---
    private void InitOptions()
    {
        string currentFile = SelectedModel?.Filename ?? ModelFile3B; // Default to 3B if null

        ModelOptions =
        [
            new ModelOption(
                GetString("Model_1_5B"),
                ModelFile1_5B,
                "Ultra-fast, Low Memory"),
            new ModelOption(
                GetString("Model_3B"),
                ModelFile3B,
                "Balanced Speed/Quality"),
            new ModelOption(
                GetString("Model_4B"),
                ModelFile4B,
                "Max Quality, Requires GPU")
        ];
        // Restore logic: Config > Current > 3B Default
        string targetFile = !string.IsNullOrEmpty(_config.Llm?.DefaultModelName)
            ? _config.Llm.DefaultModelName
            : (SelectedModel?.Filename ?? ModelFile3B);

        SelectedModel = ModelOptions.FirstOrDefault(x => x.Filename == targetFile)
                        ?? ModelOptions.FirstOrDefault(x => x.Filename == ModelFile3B)
                        ?? ModelOptions[0];

        bool useGpu = SelectedHardware?.IsGpu ?? true;
        HardwareOptions =
        [
            new HardwareOption(
                GetString("Hardware_GPU"),
                true,
                "NVIDIA/AMD/Intel GPU"),
            new HardwareOption(
                GetString("Hardware_CPU"),
                false,
                "Central Processor")
        ];
        SelectedHardware = HardwareOptions.FirstOrDefault(x => x.IsGpu == useGpu) ?? HardwareOptions[0];
    }

    public MainViewModel(IOcrService ocrService, ILlmService llmService, IHotkeyService hotkeyService, ISnippingService snippingService, AppConfig config)
    {
        _ocrService = ocrService;
        _llmService = llmService;
        _hotkeyService = hotkeyService;
        _snippingService = snippingService;
        _config = config;

        // Auto-show help on first run
        if (_config.IsFirstRun)
        {
            IsHelpVisible = true;
        }

        // Initialize commands
        // ... (Commands are source generated)
        _llmService.OnStatusUpdated += (status) => StatusMessage = status;

        // Register Alt+Q (Global)
        _hotkeyService.Register(ModifierKeys.Alt, Key.Q, OnHotkeyTriggered);

        // Initialize Languages
        LanguageOptions =
        [
            new LanguageOption("English", "en-US"),
            new LanguageOption("中文 (Chinese)", "zh-CN")
        ];

        // Select language: Persisted Config -> Setup Window (CurrentUICulture) -> Fallback
        var preferredCode = !string.IsNullOrEmpty(_config.Language)
            ? _config.Language
            : System.Globalization.CultureInfo.CurrentUICulture.Name;

        SelectedLanguage = LanguageOptions.FirstOrDefault(x => x.Code.Equals(preferredCode, StringComparison.OrdinalIgnoreCase))
                           ?? LanguageOptions.FirstOrDefault(x => preferredCode.StartsWith(x.Code[..2], StringComparison.OrdinalIgnoreCase))
                           ?? LanguageOptions[1]; // Fallback to Chinese

        InitOptions(); // Initialize Dropdowns

        // FIX: Inject/wire-up localization for LlamaService
        // We pass the key, it returns the string FROM THIS ViewModel (which holds the ResourceManager)
        _llmService.SetLocalization(System.Globalization.CultureInfo.CurrentUICulture);

        // Trigger Async Init of LLM (fire and forget to not block UI)
        _ = _llmService.InitializeAsync();

        // Trigger Async Warmup of OCR (fire and forget)
        _ = _ocrService.WarmupAsync();
    }

    // --- Copy Feedback ---
    // Segoe MDL2 Assets: E8C8 = Copy, E73E = CheckMark
    [ObservableProperty] private string _originalCopyButtonText = "\xE8C8";
    [ObservableProperty] private string _translationCopyButtonText = "\xE8C8";
    [ObservableProperty] private string _imageCopyButtonText = "\xE8C8";

    // --- Commands ---
    [RelayCommand]
    private async Task CopyOriginal()
    {
        if (!string.IsNullOrEmpty(OriginalText))
        {
            System.Windows.Clipboard.SetText(OriginalText);
            OriginalCopyButtonText = "\xE73E"; // Check
            await Task.Delay(1500);
            OriginalCopyButtonText = "\xE8C8"; // Copy
        }
    }

    [RelayCommand]
    private async Task CopyTranslation()
    {
        if (!string.IsNullOrEmpty(TranslatedText))
        {
            System.Windows.Clipboard.SetText(TranslatedText);
            TranslationCopyButtonText = "\xE73E";
            await Task.Delay(1500);
            TranslationCopyButtonText = "\xE8C8";
        }
    }

    [RelayCommand]
    private async Task CopyImage()
    {
        if (CapturedImage != null)
        {
            System.Windows.Clipboard.SetImage(CapturedImage);
            ImageCopyButtonText = "\xE73E";
            await Task.Delay(1500);
            ImageCopyButtonText = "\xE8C8";
        }
    }



    private readonly ISnippingService _snippingService;

    private bool _isSnipping = false;

    private void OnHotkeyTriggered()
    {
        // Must run on UI thread if triggered from hook
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // Prevent re-entry if already snipping or processing
            if (_isSnipping || IsProcessing) return;
            CaptureCommand.Execute(null);
        });
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (_isSnipping || IsProcessing) return;

        // NOTE: Don't reset OriginalText, TranslatedText, CapturedImage here!
        // Only clear after user confirms an action (OCR/Translate), not on Cancel/Copy
        _cts = new CancellationTokenSource();

        try
        {
            _isSnipping = true;

            // 1. Snip Region
            StatusMessage = GetString("Status_SelectRegion");
            // NOTE: Don't clear CapturedImage here - preserve previous content until user confirms action

            string copyMsg = GetString("Msg_Copied");
            var result = await _snippingService.SnipAsync(copyMsg);

            // UX: Restore UI logic
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _isSnipping = false;
            });

            // UX FIX: If user cancels or copies, preserve previous content and show ready status
            if (result.Action == SnipAction.Cancel || result.Action == SnipAction.Copy || result.Image == null)
            {
                StatusMessage = result.Action == SnipAction.Copy ? GetString("Status_ImageCopied") : GetString("StatusReady");
                return;
            }

            // User chose OCR or Translate - NOW we clear previous content
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                OriginalText = "";
                TranslatedText = "";
                // Reset Overlays
                OcrOverlayText = GetString("OcrOverlay_Running");
                TranslationOverlayText = GetString("TranslationOverlayWaiting");
                IsOcrBusy = true;
                IsTranslationBusy = false;
            });

            var bmp = result.Image;

            // UX FIX: IMMEDIATELY Set Image Property
            var hBitmap = bmp.GetHbitmap();
            try
            {
                var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                bitmapSource.Freeze();
                CapturedImage = bitmapSource;
            }
            finally
            {
                DeleteObject(hBitmap); // Prevent GDI leak
            }

            // UX FIX: Force Window Show & Refresh IMMEDIATELY
            ShowWindowAndActivate();

            IsProcessing = true;
            StatusMessage = GetString("Status_Analzying");

            // CRITICAL PERF FIX: Move HEAVY Processing to Background Thread
            // We fire-and-forget this task but monitor it via IsProcessing
#pragma warning disable CS4014
            Task.Run(async () =>
            {
                var stopwatchTotal = System.Diagnostics.Stopwatch.StartNew();
                double ocrTime = 0;
                double llmTime = 0;

                try
                {
                    // Pre-processing - Removed GC.Collect() from hot path for performance

                    // Convert (Fast BMP) & Dispose
                    byte[] imgBytes = BitmapToBytes(bmp);
                    bmp.Dispose();

                    // OCR
                    System.Windows.Application.Current.Dispatcher.Invoke(() => StatusMessage = GetString("Status_RunningOcr"));
                    var swOcr = System.Diagnostics.Stopwatch.StartNew();

                    var ocrResult = await _ocrService.RecognizeAsync(imgBytes, _cts.Token);

                    swOcr.Stop();
                    ocrTime = swOcr.Elapsed.TotalSeconds;

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        // OriginalText = ocrResult; // REMOVED: Replaced by Typewriter Animation
                        OriginalText = "";
                        IsOcrBusy = false; // Stop Spinner
                    });

                    // PARALLEL ACTION 1: Start Visual Typewriter Effect (Fire-and-Forget, tracked via Token)
                    // We do NOT await this, so LLM starts immediately.
                    _ = AnimateOriginalTextAsync(ocrResult, _cts.Token);

                    if (string.IsNullOrWhiteSpace(ocrResult))
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            StatusMessage = GetString("Status_NoText");
                            OcrOverlayText = GetString("Overlay_NoText");
                            MetricsText = $"OCR: {ocrTime:F2}s | No Text";
                            IsProcessing = false;
                        });
                        return;
                    }

                    // Check Action: If OCR Only, stop here
                    if (result.Action == SnipAction.OcrOnly)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            StatusMessage = GetString("Status_OcrDone");
                            IsProcessing = false;

                            // Metrics
                            MetricsText = $"OCR: {ocrTime:F2}s | Skipped Translate";
                        });
                        return;
                    }

                    // Translate
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        StatusMessage = GetString("Status_Translating");
                        TranslationOverlayText = GetString("Overlay_Translating");
                        IsTranslationBusy = true; // Start Spinner
                        TranslatedText = "";
                    });

                    var swLlm = System.Diagnostics.Stopwatch.StartNew(); // Moved here to capture only LLM time

                    // STREAMING IMPLEMENTATION - Use StringBuilder for performance
                    var translationBuilder = new System.Text.StringBuilder();
                    int retries = 0;
                    while (retries < 2)
                    {
                        try
                        {
                            if (retries > 0)
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => StatusMessage = GetString("Status_InitModel"));
                                await Task.Delay(1000, _cts.Token);
                            }

                            bool hasStarted = false;
                            await foreach (var token in _llmService.TranslateStreamAsync(ocrResult, _cts.Token))
                            {
                                hasStarted = true;
                                translationBuilder.Append(token);

                                // Update UI in real-time
                                string currentTranslation = translationBuilder.ToString();
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    TranslatedText = currentTranslation;
                                });
                            }

                            if (hasStarted) break; // If we got any tokens, consider it a success
                            if (translationBuilder.Length > 0) break;
                        }
                        catch (Exception ex)
                        {
                            if (retries == 1) // Last retry
                                translationBuilder.Clear().Append($"[Error: {ex.Message}]");
                        }
                        retries++;
                    }
                    string translation = translationBuilder.ToString();

                    swLlm.Stop();
                    llmTime = swLlm.Elapsed.TotalSeconds;

                    stopwatchTotal.Stop();
                    double totalTime = stopwatchTotal.Elapsed.TotalSeconds;

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TranslatedText = string.IsNullOrWhiteSpace(translation)
                            ? "[Connection Error: No response]"
                            : translation;

                        StatusMessage = GetString("Status_Done");
                        IsProcessing = false;
                        IsTranslationBusy = false; // Stop Spinner

                        // Update Metrics - Safe array access to prevent IndexOutOfRangeException
                        string[] nameParts = SelectedModel?.Name.Split(' ') ?? [];
                        string modelName = nameParts.Length > 1 ? nameParts[1] : (nameParts.Length > 0 ? nameParts[0] : "Unknown");
                        string backend = SelectedHardware?.IsGpu == true ? "GPU (CUDA)" : "CPU";

                        MetricsText = $"Model: {modelName} | Backend: {backend} | Time: OCR {ocrTime:F1}s / LLM {llmTime:F1}s / Total {totalTime:F1}s";
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        StatusMessage = ex is OperationCanceledException ? GetString("Status_Cancelled") : string.Format(GetString("Status_Error"), ex.Message);
                        MetricsText = "Error";
                        IsProcessing = false;
                        IsTranslationBusy = false;
                    });
                }
                finally
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => _isSnipping = false);
                }
            }, _cts.Token);
#pragma warning restore CS4014
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(GetString("Status_Error"), ex.Message);
            IsProcessing = false;
            _isSnipping = false;
            IsOcrBusy = false;
            IsTranslationBusy = false;
        }
    }

    // New Helper Method for Manual Translate Button
    private async Task TranslateAndDisplay(string text)
    {
        if (IsTranslationBusy) return;

        // Ensure CTS is ready
        if (_cts == null || _cts.IsCancellationRequested)
            _cts = new CancellationTokenSource();

        IsTranslationBusy = true;
        IsProcessing = true;

        TranslatedText = "";

        StatusMessage = GetString("Status_Translating");
        TranslationOverlayText = GetString("Overlay_Translating");

        var swLlm = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var translationBuilder = new System.Text.StringBuilder();
            int retries = 0;
            while (retries < 2)
            {
                try
                {
                    if (retries > 0)
                    {
                        StatusMessage = GetString("Status_InitModel");
                        await Task.Delay(1000, _cts!.Token);
                    }

                    bool hasStarted = false;
                    await foreach (var token in _llmService.TranslateStreamAsync(text, _cts!.Token))
                    {
                        hasStarted = true;
                        translationBuilder.Append(token);
                        TranslatedText = translationBuilder.ToString();
                    }

                    if (hasStarted) break;
                    if (translationBuilder.Length > 0) break;
                }
                catch (Exception ex)
                {
                    if (retries == 1) translationBuilder.Clear().Append($"[Error: {ex.Message}]");
                }
                retries++;
            }

            if (string.IsNullOrEmpty(TranslatedText))
                TranslatedText = translationBuilder.ToString();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(GetString("Status_Error"), ex.Message);
        }
        finally
        {
            IsTranslationBusy = false;
            IsProcessing = false;
            swLlm.Stop();
            StatusMessage = GetString("Status_Done");
            MetricsText = $"Translate: {swLlm.Elapsed.TotalSeconds:F2}s";
        }
    }
    private static void ShowWindowAndActivate()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
           {
               var win = System.Windows.Application.Current.MainWindow;
               if (win != null)
               {
                   if (win.WindowState == WindowState.Minimized)
                       win.WindowState = WindowState.Normal;

                   win.Show();
                   win.Activate();
                   win.Topmost = true;
                   win.Focus();

                   // Temporary TopMost - using proper TaskScheduler
                   Task.Delay(300).ContinueWith(_ =>
                       System.Windows.Application.Current.Dispatcher.Invoke(() => win.Topmost = false), System.Threading.Tasks.TaskScheduler.Default);
               }
           });
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    private async Task AnimateOriginalTextAsync(string text, CancellationToken token)
    {
        try
        {
            // Dynamic delay: Target ~0.8s total duration, clipped to [2ms, 20ms] per char
            // This is "snappy" but still visible as typing.
            int delay = Math.Clamp(800 / Math.Max(1, text.Length), 2, 20);

            for (int i = 0; i < text.Length; i++)
            {
                if (token.IsCancellationRequested) break;

                string partial = text.Substring(0, i + 1);
                // Low priority update to keep UI responsive
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => OriginalText = partial, System.Windows.Threading.DispatcherPriority.Background);

                await Task.Delay(delay, token);
            }
            // Ensure final consistency
            if (!token.IsCancellationRequested)
                System.Windows.Application.Current.Dispatcher.Invoke(() => OriginalText = text);
        }
        catch { /* Ignore cancellation/errors in visual effect */ }
    }

    private static byte[] BitmapToBytes(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        // Use BMP for maximum speed (no compression overhead on UI/Background thread)
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        return ms.ToArray();
    }
}
