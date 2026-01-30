using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboSnip.WPF.Services;

public interface ILlmService
{
    event Action<string> OnStatusUpdated; // For UI notifications (e.g. "GPU Ready" or "CPU Fallback")
    bool IsInitialized { get; }

    Task InitializeAsync();
    void SwitchModel(string modelName);
    void SwitchHardware(bool useGpu);
    string GetCpuName();
    string GetGpuName();
    [Obsolete("Use TranslateStreamAsync instead for streaming translation.")]
    Task<string> TranslateAsync(string text, CancellationToken token = default);
    IAsyncEnumerable<string> TranslateStreamAsync(string text, CancellationToken token = default);
    void Unload();
    void SetLocalization(System.Globalization.CultureInfo culture);
}
