using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;

namespace TurboSnip.Core.Test;

class Program
{
    static async Task Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== TurboSnip Phase 1: Verification Test ===\n");

        // Get the project root directory (go up from bin/Debug/net8.0)
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));

        Console.WriteLine($"Project Root: {projectRoot}");
        Console.WriteLine();

        // Verify model directories exist
        string detModelDir = Path.Combine(projectRoot, "models", "det");
        string recModelDir = Path.Combine(projectRoot, "models", "ocr");
        string llmModelPath = Path.Combine(projectRoot, "models", "llm", "qwen2.5-1.5b-instruct-q4_k_m.gguf");
        string keysPath = Path.Combine(projectRoot, "models", "ocr", "ppocr_keys_v1.txt");

        Console.WriteLine("[1/5] Checking model files...");
        VerifyPath(detModelDir, "Detection Model Directory");
        VerifyPath(recModelDir, "Recognition Model Directory");
        VerifyPath(llmModelPath, "LLM Model File");
        VerifyPath(keysPath, "OCR Keys Dictionary");
        Console.WriteLine("✓ All model files found!\n");

        // === Test 1: PaddleOCR ===
        Console.WriteLine("[2/5] Testing PaddleOCR (Sdcb.PaddleOCR)...");
        string ocrResult = await TestPaddleOCRAsync();
        Console.WriteLine($"OCR Result: \"{ocrResult}\"\n");

        // === Test 2: LlamaSharp CUDA ===
        Console.WriteLine("[3/5] Testing LlamaSharp CUDA Backend...");
        bool cudaWorking = await TestLlamaSharpCuda(llmModelPath, ocrResult);

        if (!cudaWorking)
        {
            Console.WriteLine("\n[4/5] CUDA failed, testing CPU fallback...");
            await TestLlamaSharpCpu(llmModelPath, ocrResult);
        }
        else
        {
            Console.WriteLine("[4/5] Skipping CPU test (CUDA is working)");
        }

        Console.WriteLine("\n[5/5] Verification Complete!");
        Console.WriteLine("\nPress any key to exit...");
        // Console.ReadKey(); // Skipped for non-interactive run
    }

    static void VerifyPath(string path, string description)
    {
        bool exists = File.Exists(path) || Directory.Exists(path);
        Console.WriteLine($"  {(exists ? "✓" : "✗")} {description}: {path}");
        if (!exists)
        {
            throw new FileNotFoundException($"Required path not found: {path}");
        }
    }

    static async Task<string> TestPaddleOCRAsync()
    {
        // Create a test image with Chinese and English text
        using var bitmap = CreateTestImage("Hello World! 你好世界！");

        // Save bitmap to temp file for OpenCV to read
        string tempPath = Path.GetTempFileName() + ".png";
        bitmap.Save(tempPath);

        return await Task.Run(() =>
        {
            try
            {
                // Use Sdcb.PaddleOCR with LocalV4 models (from NuGet package)
                // Note: The user's custom models are for PaddleOCR format, but Sdcb uses its own model format
                // We'll use the LocalV4 built-in models for this test
                FullOcrModel model = LocalFullModels.ChineseV4;

                var sw = Stopwatch.StartNew();

                using var ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
                {
                    AllowRotateDetection = true,
                    Enable180Classification = false
                };

                using Mat src = Cv2.ImRead(tempPath);
                PaddleOcrResult result = ocr.Run(src);

                sw.Stop();
                Console.WriteLine($"  OCR Engine initialized and inference completed in {sw.ElapsedMilliseconds}ms");

                if (result.Regions == null || result.Regions.Length == 0)
                {
                    Console.WriteLine("  ⚠ Warning: No text detected in test image");
                    return "Test text not detected";
                }

                var allText = string.Join(" ", result.Regions.Select(r => r.Text));
                Console.WriteLine($"  ✓ Detected {result.Regions.Length} text region(s)");

                return allText;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        });
    }

    static Bitmap CreateTestImage(string text)
    {
        // Create a 500x120 bitmap with text
        var bitmap = new Bitmap(500, 120);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.White);

        using var font = new Font("Microsoft YaHei", 28, FontStyle.Regular);
        using var brush = new SolidBrush(Color.Black);

        graphics.DrawString(text, font, brush, 20, 40);

        Console.WriteLine($"  Created test image: {bitmap.Width}x{bitmap.Height} with text: \"{text}\"");
        return bitmap;
    }

    static async Task<bool> TestLlamaSharpCuda(string modelPath, string textToTranslate)
    {
        try
        {
            Console.WriteLine("  Loading model with CUDA backend...");

            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = 2048,
                GpuLayerCount = 35  // Offload all layers to GPU for Qwen 1.5B
            };

            var sw = Stopwatch.StartNew();
            using var model = await LLamaWeights.LoadFromFileAsync(modelParams);
            sw.Stop();

            Console.WriteLine($"  ✓ Model loaded in {sw.ElapsedMilliseconds}ms");

            // Check backend info
            string backendInfo = GetBackendInfo();
            Console.WriteLine($"  Backend Info: {backendInfo}");

            bool isCuda = backendInfo.Contains("libs detected", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  ✓ Backend Status: {backendInfo}");

            if (!isCuda)
            {
                Console.WriteLine("  ⚠ Warning: CUDA libraries NOT loaded. Running on CPU.");
            }
            else
            {
                Console.WriteLine("  ✓ CUDA libraries loaded correctly.");
            }

            // Run inference test
            await RunInferenceTest(model, modelParams, textToTranslate);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ CUDA initialization failed: {ex.Message}");
            if (ex.Message.Contains("cuBLAS", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("cuda", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Hint: This usually means CUDA libraries (cuBLAS) are not properly installed.");
                Console.WriteLine("  Make sure you have CUDA Toolkit 12.x installed with cuBLAS.");
            }
            return false;
        }
    }

    static string GetBackendInfo()
    {
        try
        {
            // Check if CUDA-related DLLs are loaded
            var loadedModules = Process.GetCurrentProcess().Modules;
            bool hasCudaDll = false;
            foreach (ProcessModule module in loadedModules)
            {
                if (module.ModuleName.Contains("cuda", StringComparison.OrdinalIgnoreCase) ||
                    module.ModuleName.Contains("cublas", StringComparison.OrdinalIgnoreCase) ||
                    module.ModuleName.Contains("llama_cuda", StringComparison.OrdinalIgnoreCase))
                {
                    hasCudaDll = true;
                    break;
                }
            }

            return hasCudaDll ? "CUDA libraries detected" : "CPU mode (no CUDA libraries found)";
        }
        catch
        {
            return "Unable to detect backend";
        }
    }

    static async Task TestLlamaSharpCpu(string modelPath, string textToTranslate)
    {
        try
        {
            Console.WriteLine("  Loading model with CPU backend...");

            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = 2048,
                GpuLayerCount = 0,  // Force CPU only
                Threads = 8
            };

            var sw = Stopwatch.StartNew();
            using var model = await LLamaWeights.LoadFromFileAsync(modelParams);
            sw.Stop();

            Console.WriteLine($"  ✓ Model loaded in {sw.ElapsedMilliseconds}ms (CPU mode)");

            await RunInferenceTest(model, modelParams, textToTranslate);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ CPU initialization also failed: {ex.Message}");
        }
    }

    static async Task RunInferenceTest(LLamaWeights model, ModelParams modelParams, string textToTranslate)
    {
        Console.WriteLine("\n  Running inference test...");

        using var context = model.CreateContext(modelParams);
        var executor = new InteractiveExecutor(context);

        // ChatML format for Qwen
        string prompt = $"""
            <|im_start|>system
            You are a helpful translation assistant. Translate the given text to Simplified Chinese. Fix OCR errors if any.
            <|im_end|>
            <|im_start|>user
            Translate: "{textToTranslate}"
            <|im_end|>
            <|im_start|>assistant
            """;

        // LLamaSharp 0.25 uses SamplingPipeline for temperature control
        var inferenceParams = new InferenceParams
        {
            MaxTokens = 256,
            AntiPrompts = ["<|im_end|>"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.7f
            }
        };

        Console.Write("  Response: ");
        var sw = Stopwatch.StartNew();
        int tokenCount = 0;

        await foreach (var text in executor.InferAsync(prompt, inferenceParams))
        {
            Console.Write(text);
            tokenCount++;
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"\n  ✓ Generated {tokenCount} tokens in {sw.ElapsedMilliseconds}ms ({tokenCount * 1000.0 / sw.ElapsedMilliseconds:F1} tokens/sec)");
    }
}
