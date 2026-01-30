using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using PaddleModel = Sdcb.PaddleOCR.Models.Local.LocalFullModels;

namespace TurboSnip.WPF.Services;

public partial class PaddleOcrService : IOcrService, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private PaddleOcrAll? _ocr;
    private bool _isInitialized = false;
    private readonly AppConfig _config;

    public PaddleOcrService(AppConfig config)
    {
        _config = config;
    }

    public async Task WarmupAsync()
    {
        // Just call InitializeAsync, but do it safely
        await _semaphore.WaitAsync();
        try
        {
            if (!_isInitialized) await InitializeAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized) return;

        // Use LocalFullModels.ChineseV4 as verified in Phase 1
        FullOcrModel model = LocalFullModels.ChineseV4;

        // Use Mkldnn (CPU) for OCR as it's fast enough (100ms verified) and reliable
        _ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        };

        _isInitialized = true;
        await Task.CompletedTask;
    }

    public async Task<string> RecognizeAsync(byte[] imageBytes, CancellationToken token = default)
    {
        // FIX: Add 60-second timeout to prevent indefinite UI freeze
        bool acquired = await _semaphore.WaitAsync(TimeSpan.FromSeconds(60), token);
        if (!acquired)
        {
            throw new TimeoutException("OCR operation timed out waiting for resource lock.");
        }

        try
        {
            // Try OCR with a simple retry mechanism for robustness
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    if (!_isInitialized) await InitializeAsync();
                    if (_ocr == null) throw new InvalidOperationException("OCR engine failed to initialize.");

                    // Convert byte[] to Mat
                    using Mat src = Mat.FromImageData(imageBytes, ImreadModes.Color);

                    // OPTIMIZATION 2: OCR Enhancement Pipeline
                    // 1. Upscale (3.0x) for small fonts
                    using Mat resized = new();
                    Cv2.Resize(src, resized, new OpenCvSharp.Size(0, 0), _config.Ocr.UpscaleFactor, _config.Ocr.UpscaleFactor, InterpolationFlags.Cubic);

                    // 2. Auto-Invert (Dark Mode Support)
                    if (_config.Ocr.EnableDarkThemeSupport)
                    {
                        Scalar mean = Cv2.Mean(resized);
                        double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;

                        if (brightness < 100)
                        {
                            Cv2.BitwiseNot(resized, resized);
                        }
                    }

                    token.ThrowIfCancellationRequested();

                    // CRITICAL: Protection against "PaddlePredictor(Detector) run failed"
                    PaddleOcrResult result = _ocr.Run(resized);

                    // --- Smart Post-Processing Logic ---
                    // 1. Filter & Sort
                    var validBlocks = result.Regions
                        .Where(b => b.Score > _config.Ocr.ScoreThreshold) // Filter low confidence
                        .OrderBy(b => b.Rect.Center.Y) // Sort top-to-bottom
                        .ToList();

                    if (validBlocks.Count == 0) return string.Empty;

                    return ProcessBlocks(validBlocks, _config);
                }
                catch (Exception ex)
                {
                    // If it's a known native failure or we have retries left
                    if (attempt == 1)
                    {
                        // Log warning if possible (Console.WriteLine here for now)
                        System.Diagnostics.Debug.WriteLine($"OCR Run Failed (Attempt 1): {ex.Message}. Re-initializing...");

                        // Force Re-initialization
                        _ocr?.Dispose();
                        _ocr = null;
                        _isInitialized = false;

                        // Continue to next loop iteration -> Re-init -> Retry
                        continue;
                    }
                    else
                    {
                        // Final attempt failed, throw to UI
                        throw new Exception($"OCR Failed after retry: {ex.Message}", ex);
                    }
                }
            }
            return string.Empty; // Should not reach here
        }
        finally
        {
            _semaphore.Release();
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d+\.|-|•|\*)\s")]
    private static partial System.Text.RegularExpressions.Regex ListRegex();

    public void Dispose()
    {
        _ocr?.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static string ProcessBlocks(List<PaddleOcrResultRegion> validBlocks, AppConfig config)
    {
        var sb = new System.Text.StringBuilder();
        PaddleOcrResultRegion? lastBlock = null;

        foreach (var block in validBlocks)
        {
            string text = block.Text.Trim();

            // 2. Filter Noise (Single char that isn't alphanumeric/punctuation)
            if (text.Length == 1 && !char.IsLetterOrDigit(text[0]) && !".!?".Contains(text[0])) continue;

            if (lastBlock != null)
            {
                // Calculate simple vertical gap
                // Rect is RotatedRect. We need bounding box.
                var lastRect = lastBlock.Value.Rect.BoundingRect();
                var currentRect = block.Rect.BoundingRect();

                float height = lastRect.Height;

                float lastBottom = lastRect.Bottom;
                float currentTop = currentRect.Top;
                float lastHeight = height;

                // Logic A: Paragraph Detection (Gap > config * LineHeight)
                if (currentTop - lastBottom > lastHeight * config.Ocr.ParagraphGapMultiplier)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }
                // Logic B: List Item Detection
                else if (ListRegex().IsMatch(text))
                {
                    if (sb.Length > 0 && sb[^1] != '\n')
                        sb.AppendLine();
                }
                // Logic C: English Hyphenation Repair
                else if (lastBlock.Value.Text.Trim().EndsWith('-'))
                {
                    // Handle hyphenated word split (Remove hyphen and merge)
                    string s = sb.ToString();
                    if (s.TrimEnd().EndsWith('-'))
                    {
                        int hyphenIndex = s.LastIndexOf('-');
                        if (hyphenIndex >= 0)
                        {
                            sb.Length = hyphenIndex; // Truncate to remove '-'
                        }
                    }
                    // No space needed if we are merging "pro-" + "cess" -> "process"
                }
                else
                {
                    sb.AppendLine();
                }
            }

            sb.Append(text);
            lastBlock = block; // Struct is copied if it's struct? No, Region is Class usually.
        }

        return sb.ToString().Trim();
    }


}
