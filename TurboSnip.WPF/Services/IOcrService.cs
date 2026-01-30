using System.Threading;
using System.Threading.Tasks;

namespace TurboSnip.WPF.Services;

public interface IOcrService
{
    Task<string> RecognizeAsync(byte[] imageBytes, CancellationToken token = default);
    Task WarmupAsync();
}
