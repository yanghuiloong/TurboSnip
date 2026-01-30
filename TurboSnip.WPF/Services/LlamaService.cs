using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace TurboSnip.WPF.Services;

public partial class LlamaService : ILlmService, IDisposable
{
    public event Action<string>? OnStatusUpdated;
    public bool IsInitialized { get; private set; }

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private StatelessExecutor? _executor;
    private string _modelPath = string.Empty;

    // Concurrency Control
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private CancellationTokenSource? _warmupCts;

    // System prompt for translation
    // System Prompt is now loaded from Config
    // private const string SystemPrompt = ... (Removed)

    internal static string PreprocessOcrText(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";

        // 1. Normalize line endings (Don't merge paragraphs yet)
        string text = input.Replace("\r\n", "\n").Replace("\r", "\n");

        // 2. Regex Repair
        // 2.1 Fix lowercase-Uppercase collision (e.g. "prefer.All" -> "prefer. All")
        text = LowerUpperRegex().Replace(text, "$1 $2");

        // 2.2 Fix sticky punctuation
        text = StickyPunctuationRegex().Replace(text, "$1 $2");

        // 2.3 Fix hyphenated words across lines (e.g. "pro-\ncess" -> "process")
        // Note: \s+ matches newlines, so this merges the line if split by hyphen.
        text = HyphenatedWordRegex().Replace(text, "$1$2");

        // 2.4 Fix Bullet Spacing (ensure "•Text" becomes "�?Text")
        text = BulletSpacingRegex().Replace(text, "$1 $2");

        // 3. Compress horizontal whitespace only (Keep newlines!)
        text = HorizontalWhitespaceRegex().Replace(text, " ");

        return text.Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial System.Text.RegularExpressions.Regex LowerUpperRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"([,.;)])([a-zA-Z])")]
    private static partial System.Text.RegularExpressions.Regex StickyPunctuationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(\w+)-\s+(\w+)")]
    private static partial System.Text.RegularExpressions.Regex HyphenatedWordRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"[ \t]+")]
    private static partial System.Text.RegularExpressions.Regex HorizontalWhitespaceRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^([•\-\*])([^\s])", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex BulletSpacingRegex();

    private readonly AppConfig _config;

    private readonly System.Timers.Timer? _idleTimer;

    // Use constructor injection
    public LlamaService(AppConfig config)
    {
        _config = config;
        _currentModelName = _config.Llm.DefaultModelName;

        if (_config.Llm.UnloadTimeoutMinutes > 0)
        {
            _idleTimer = new System.Timers.Timer(_config.Llm.UnloadTimeoutMinutes * 60 * 1000)
            {
                AutoReset = false
            };
            _idleTimer.Elapsed += (s, e) => Unload();
        }
    }

    private string _currentModelName;
    private bool _useGpu = true; // Default to GPU

    public void SwitchModel(string modelName)
    {
        if (_currentModelName != modelName)
        {
            _currentModelName = modelName;
            RestartService($"Switched to {modelName}");
        }
    }

    public void SwitchHardware(bool useGpu)
    {
        if (_useGpu != useGpu)
        {
            _useGpu = useGpu;
            RestartService($"Switched to {(useGpu ? "GPU" : "CPU")}");
        }
    }

    private void RestartService(string reason)
    {
        Unload(); // Reuses Unload for clean disposal
        IsInitialized = false;
        OnStatusUpdated?.Invoke($"{reason} (Pending Init)");
    }

    private Task? _initTask;

    // Localization via global Resources
    public void SetLocalization(System.Globalization.CultureInfo culture)
    {
        TurboSnip.WPF.Properties.Resources.Culture = culture;
    }

    private static string GetLocalizedStatus(string key, params object[] args)
    {
        try
        {
            var resMan = TurboSnip.WPF.Properties.Resources.ResourceManager;
            var culture = TurboSnip.WPF.Properties.Resources.Culture;
            string format = resMan.GetString(key, culture) ?? key;

            if (args == null || args.Length == 0) return format;
            return string.Format(format, args);
        }
        catch
        {
            return key;
        }
    }

    private static void LogDebug(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug_log.txt"), $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Debug log write failed: {ex.Message}"); }
    }

    public async Task InitializeAsync()
    {
        // Double-Check Locking Pattern
        if (IsInitialized && _initTask == null) return;

        await _initLock.WaitAsync();
        try
        {
            // FIX: Capture task reference locally to avoid race condition
            var currentTask = _initTask;
            if (IsInitialized || currentTask != null)
            {
                if (currentTask != null) await currentTask;
                return;
            }

            LogDebug($"Initializing LlamaService (Cold Start). GPU={_useGpu}, Model={_currentModelName}");

            _idleTimer?.Stop(); // Ensure timer is paused during init

            // Search for the CURRENT model
            string[] searchPaths =
            [
                 Path.Combine(AppContext.BaseDirectory, "models", "llm", _currentModelName),
             Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "llm", _currentModelName),
             Path.Combine(AppContext.BaseDirectory, "..", "models", "llm", _currentModelName)
            ];
            _modelPath = searchPaths.FirstOrDefault(File.Exists) ?? string.Empty;

            // FALLBACK: If specific model not found, try ANY model in the local llm directory
            if (string.IsNullOrEmpty(_modelPath))
            {
                var localLlmDir = Path.Combine(AppContext.BaseDirectory, "models", "llm");
                if (Directory.Exists(localLlmDir))
                {
                    var anyModel = Directory.GetFiles(localLlmDir, "*.gguf").FirstOrDefault();
                    if (!string.IsNullOrEmpty(anyModel))
                    {
                        _modelPath = anyModel;
                        var newName = Path.GetFileName(_modelPath);
                        OnStatusUpdated?.Invoke($"Configured model '{_currentModelName}' missing. Fallback to '{newName}'.");
                        _currentModelName = newName;
                    }
                }
            }

            if (string.IsNullOrEmpty(_modelPath))
            {
                OnStatusUpdated?.Invoke($"Error: Model {_currentModelName} not found in {Path.Combine(AppContext.BaseDirectory, "models", "llm")}!");
                return;
            }

            _initTask = Task.Run(() =>
            {
                int attempts = 0;
                // Retry loop: Try GPU first (if enabled), then Fallback to CPU
                while (!IsInitialized && attempts < 2)
                {
                    attempts++;
                    try
                    {
                        string mode = _useGpu ? "GPU" : "CPU";
                        OnStatusUpdated?.Invoke(GetLocalizedStatus("Status_InitializingLLM", _currentModelName, mode));

                        // 1. Validate File
                        var fileInfo = new FileInfo(_modelPath);
                        if (!fileInfo.Exists) throw new FileNotFoundException($"Model file not found: {_modelPath}");
                        LogDebug($"File found: {_modelPath}, Size: {fileInfo.Length} bytes");

                        if (fileInfo.Length < 10 * 1024 * 1024) throw new InvalidDataException($"Model file matches but is too small ({fileInfo.Length / 1024} KB). Likely corrupt.");

                        // 1.1 Validate Header (Magic "GGUF")
                        using (var fs = File.OpenRead(_modelPath))
                        {
                            var magic = new byte[4];
                            if (fs.Read(magic, 0, 4) < 4) throw new InvalidDataException("File too short to be GGUF.");
                            // 'G' 'G' 'U' 'F' -> 0x47 0x47 0x55 0x46
                            if (magic[0] != 0x47 || magic[1] != 0x47 || magic[2] != 0x55 || magic[3] != 0x46)
                            {
                                throw new InvalidDataException($"Invalid file format (Magic: {BitConverter.ToString(magic)}). Expected 'GGUF'.");
                            }
                        }
                        LogDebug("Magic Header Valid (GGUF)");

                        var parameters = new ModelParams(_modelPath)
                        {
                            ContextSize = (uint)_config.Llm.ContextSize,
                            BatchSize = (uint)_config.Llm.BatchSize,
                            // Fix for Fallback: Dynamically check _useGpu
                            GpuLayerCount = _useGpu ? _config.Llm.GpuLayerCount : 0,
                            MainGpu = 0,
                            SplitMode = GPUSplitMode.None,
                            FlashAttention = true
                        };

                        LogDebug($"Attempting LoadFromFile. Ctx={parameters.ContextSize}, GpuLayers={parameters.GpuLayerCount}, FlashAttn={parameters.FlashAttention}");
                        _weights = LLamaWeights.LoadFromFile(parameters);
                        LogDebug("Weights Loaded!");
                        _executor = new StatelessExecutor(_weights, parameters);
                        _context = _executor.Context;

                        string hardwareName = _useGpu ? GetGpuName() : GetCpuName();
                        string backendStatus = _useGpu ? "GPU Mode" : "CPU Mode";

                        if (!_useGpu && attempts > 1)
                        {
                            backendStatus += GetLocalizedStatus("Status_LowVramFallback");
                        }

                        OnStatusUpdated?.Invoke(GetLocalizedStatus("Status_LLMReady", backendStatus, hardwareName));

                        IsInitialized = true;
                        ResetIdleTimer();

                        // WARMUP: Detached task that yields lock if User Request comes in
                        _warmupCts = new CancellationTokenSource();
                        _ = Task.Run(() => PerformWarmupAsync(_warmupCts.Token));
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Exception: {ex.Message}\nStack: {ex.StackTrace}");

                        // If we failed and were using GPU, try falling back to CPU
                        if (_useGpu)
                        {
                            OnStatusUpdated?.Invoke(GetLocalizedStatus("Status_GpuInitFailed", ex.Message));
                            _useGpu = false;
                            continue;
                        }

                        // Fatal error (CPU failed)
                        var fileInfo = new FileInfo(_modelPath);
                        string sizeInfo = fileInfo.Exists ? $"{(double)fileInfo.Length / 1024 / 1024 / 1024:F2} GB" : "Missing";

                        OnStatusUpdated?.Invoke(GetLocalizedStatus("Status_LlmInitFatal", $"{ex.Message} (File: {_modelPath}, Size: {sizeInfo})"));
                        break;
                    }
                }
                _initTask = null;
            });

            await _initTask;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public string GetCpuName()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var item in searcher.Get())
            {
                return item["Name"]?.ToString() ?? "Unknown CPU";
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CPU detection failed: {ex.Message}"); }
        return "Generic CPU";
    }

    public string GetGpuName()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var item in searcher.Get())
            {
                string name = item["Name"]?.ToString() ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GPU detection failed: {ex.Message}"); }
        return "Generic GPU";
    }

    private async Task PerformWarmupAsync(CancellationToken token)
    {
        try
        {
            await _inferenceLock.WaitAsync(token); // Wait for lock
            try
            {
                LogDebug("Starting Warmup Inference...");
                var warmupParams = new InferenceParams { MaxTokens = 1 };
                await foreach (var _ in _executor!.InferAsync(" ", warmupParams, token)) { }
                LogDebug("Warmup Complete.");
            }
            finally
            {
                _inferenceLock.Release();
            }
        }
        catch (OperationCanceledException) { LogDebug("Warmup Cancelled by User Request."); }
        catch (Exception ex) { LogDebug($"Warmup Failed: {ex.Message}"); }
    }

    private void ResetIdleTimer()
    {
        if (_idleTimer != null)
        {
            _idleTimer.Stop();
            _idleTimer.Start();
        }
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        _idleTimer?.Stop(); // Pause timer while working

        // 1. Wait for Initialization (Cold Start Fix)
        LogDebug("User Request: TranslateStreamAsync called.");
        await InitializeAsync();

        // 2. Cancellation: Kill any running Warmup
        _warmupCts?.Cancel();

        // 3. Lock: Wait for Warmup to yield (or previous request to finish)
        await _inferenceLock.WaitAsync(token);

        // CRITICAL FIX: Use a flag to track if lock was acquired, and release in finally
        // This pattern ensures the lock is released even if the iterator is not fully consumed
        bool lockAcquired = true;

        try
        {
            if (!IsInitialized || _executor == null)
            {
                yield break;
            }

            string cleanedText = PreprocessOcrText(text);

            // Use ChatML with ZERO-SHOT PROMPTING
            string fullPrompt = BuildPrompt(cleanedText);

            var inferenceParams = new InferenceParams()
            {
                MaxTokens = _config.Llm.MaxTokens,
                AntiPrompts = [.. _config.Llm.AntiPrompts],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = _config.Llm.Temperature,
                    RepeatPenalty = _config.Llm.RepeatPenalty,
                    TopP = _config.Llm.TopP
                }
            };

            await foreach (var output in _executor.InferAsync(fullPrompt, inferenceParams, token))
            {
                yield return output;
            }
        }
        finally
        {
            if (lockAcquired)
            {
                _inferenceLock.Release();
            }
            ResetIdleTimer(); // Restart timer after work is done
        }
    }

    [Obsolete("Use TranslateStreamAsync instead for streaming translation.")]
    public Task<string> TranslateAsync(string text, CancellationToken token = default)
    {
        throw new NotImplementedException("Use TranslateStreamAsync for streaming translation.");
    }

    public void Unload()
    {
        _idleTimer?.Stop();

        // ONLY free the heavy LLM resources, NOT the service locks
        FreeModelResources();

        IsInitialized = false;

        // Force GC
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (OnStatusUpdated != null)
        {
            // We use Task.Run to avoid blocking the timer thread (if called from elapsed)
            Task.Run(() => OnStatusUpdated?.Invoke("Model unloaded (Idle)"));
        }
    }

    private void FreeModelResources()
    {
        _context?.Dispose();
        _context = null;

        _weights?.Dispose();
        _weights = null;

        _executor = null;

        _warmupCts?.Cancel();
        _warmupCts?.Dispose();
        _warmupCts = null;
    }

    public void Dispose()
    {
        FreeModelResources();

        _idleTimer?.Dispose();

        // Dispose locks only when the entire service is dying (App closure)
        _inferenceLock.Dispose();
        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }

    internal string BuildPrompt(string cleanedText)
    {
        string sys = !string.IsNullOrWhiteSpace(_config.Llm.SystemPrompt)
            ? _config.Llm.SystemPrompt
            : "You are a professional translator. Translate the user input to Simplified Chinese directly. Do NOT provide any explanations, notes, or context. Do NOT repeat the original text. Output ONLY the translation.";

        return $"<|im_start|>system\n{sys}<|im_end|>\n" +
               $"<|im_start|>user\nTranslate the following text to Simplified Chinese:\n\n{cleanedText}\n<|im_end|>\n" +
               $"<|im_start|>assistant\n";
    }
}
