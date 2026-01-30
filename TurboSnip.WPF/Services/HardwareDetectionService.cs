using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;

namespace TurboSnip.WPF.Services;

public record HardwareInfo(double RamGb, double VramGb, string TopGpuName, string CpuName);

public record RecommendedConfig(bool UseGpu, string ModelName, string Reason);

public partial class HardwareDetectionService
{
    private const string ModelHigh = "Qwen3-4B-Instruct-2507-UD-Q4_K_XL.gguf";
    private const string ModelMid = "qwen2.5-3b-instruct-q4_k_m.gguf";
    private const string ModelLow = "qwen2.5-1.5b-instruct-q4_k_m.gguf";

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d+)\s*GB", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NameVramRegex();

    public static HardwareInfo DetectHardware()
    {
        double ramGb = 0;
        double vramGb = 0;
        string topGpuName = "Unknown GPU";

        try
        {
            // 1. Get System RAM
            // Using Win32_OperatingSystem.TotalVisibleMemorySize (KB)
            using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
            {
                foreach (var obj in searcher.Get())
                {
                    if (double.TryParse(obj["TotalVisibleMemorySize"]?.ToString(), out double totalVisibleMemorySizeKb))
                    {
                        ramGb = totalVisibleMemorySizeKb / 1024.0 / 1024.0;
                    }
                }
            }

            // 2. Get VRAM (Best Guess)
            // Using Win32_VideoController
            using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
            {
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (long.TryParse(obj["AdapterRAM"]?.ToString(), out long adapterRamBytes))
                    {
                        double adapterRamGb = adapterRamBytes / 1024.0 / 1024.0 / 1024.0;

                        // Prefer NVIDIA > AMD > Intel for "Top GPU"
                        bool isNvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

                        // Fix for 32-bit WMI overflow (shows 4GB for cards with >4GB)
                        // Method 1: Registry Lookup (Most Reliable)
                        long regVramBytes = GetVramFromRegistry(name);
                        if (regVramBytes > adapterRamBytes)
                        {
                            adapterRamGb = regVramBytes / 1024.0 / 1024.0 / 1024.0;
                        }
                        // Method 2: Name Heuristic (Fallback)
                        else if (adapterRamGb >= 3.9 && adapterRamGb <= 4.1)
                        {
                            var vramMatch = NameVramRegex().Match(name);
                            if (vramMatch.Success && int.TryParse(vramMatch.Groups[1].Value, out int nameVram) && nameVram > 4)
                            {
                                adapterRamGb = nameVram;
                            }
                        }

                        // If this is the highest VRAM found so far OR it's an NVIDIA card and we haven't found one yet
                        if (adapterRamGb > vramGb || (isNvidia && !topGpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
                        {
                            vramGb = adapterRamGb;
                            topGpuName = name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log hardware detection errors for debugging
            System.Diagnostics.Debug.WriteLine($"Hardware detection error: {ex.Message}");
        }

        // 3. Get CPU Name
        string cpuName = "Unknown CPU";
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                cpuName = obj["Name"]?.ToString() ?? "Unknown CPU";
                break; // Only need the first one
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CPU detection error: {ex.Message}");
        }

        return new HardwareInfo(Math.Round(ramGb, 2), Math.Round(vramGb, 2), topGpuName, cpuName);
    }

    private static long GetVramFromRegistry(string adapterName)
    {
        try
        {
            // Display Adapters Class GUID
            string keyPath = @"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
            if (key == null) return 0;

            foreach (string subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                // Match Adapter Name (DriverDesc)
                string driverDesc = subKey.GetValue("DriverDesc")?.ToString() ?? "";
                if (string.Equals(driverDesc, adapterName, StringComparison.OrdinalIgnoreCase))
                {
                    // Check for 64-bit VRAM value
                    object? qwMemorySize = subKey.GetValue("HardwareInformation.qwMemorySize");
                    if (qwMemorySize is long vramLong) return vramLong;
                    if (qwMemorySize is byte[] vramBytes && vramBytes.Length == 8) return BitConverter.ToInt64(vramBytes, 0);

                    // Fallback to 32-bit (though likely same as WMI)
                    object? memorySize = subKey.GetValue("HardwareInformation.MemorySize");
                    if (memorySize is int vramInt) return vramInt;
                    if (memorySize is byte[] memBytes && memBytes.Length == 4) return BitConverter.ToInt32(memBytes, 0);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Registry VRAM lookup error: {ex.Message}");
        }
        return 0;
    }

    public static List<string> GetAvailableModels()
    {
        string[] possiblePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "models", "llm"),
            // Dev environment: bin/Debug/net8.0-windows/ -> ../../../models/llm
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "models", "llm"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "llm")
        ];

        string modelDir = possiblePaths.FirstOrDefault(Directory.Exists) ?? "";

        if (string.IsNullOrEmpty(modelDir)) return [];

        return Directory.GetFiles(modelDir, "*.gguf")
                        .Select(Path.GetFileName)
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToList()!;
    }

    public RecommendedConfig GetRecommendation()
    {
        var hw = DetectHardware();
        var availableModels = GetAvailableModels();

        // Helper to check existence
        bool Has(string name) => availableModels.Contains(name, StringComparer.OrdinalIgnoreCase);
        // Helper to pick best available fallback
        string PickBestAvailable(params string[] preferences)
        {
            foreach (var p in preferences) if (Has(p)) return p;
            return availableModels.FirstOrDefault() ?? "";
        }

        // --- Logic Implementation ---

        // Tier 1: High Performance (VRAM >= 6GB)
        if (hw.VramGb >= 6)
        {
            string target = PickBestAvailable(ModelHigh, ModelMid, ModelLow);
            return new RecommendedConfig(
                UseGpu: true,
                ModelName: target,
                Reason: $"Excellent GPU detected ({hw.TopGpuName}, {hw.VramGb}GB VRAM). Using high-performance model."
            );
        }

        // Tier 2: Balanced (3GB <= VRAM < 6GB)
        if (hw.VramGb >= 3)
        {
            string target = PickBestAvailable(ModelMid, ModelHigh, ModelLow);
            return new RecommendedConfig(
                UseGpu: true,
                ModelName: target,
                Reason: $"Standard GPU detected ({hw.TopGpuName}, {hw.VramGb}GB VRAM). Using balanced model."
            );
        }

        // Tier 3: Entry Level (VRAM < 3GB) or CPU Fallback
        // Check if RAM is high enough to upgrade to 3B model on CPU
        bool highRam = hw.RamGb >= 16;
        string cpuTarget = highRam
            ? PickBestAvailable(ModelMid, ModelLow)
            : PickBestAvailable(ModelLow, ModelMid);

        return new RecommendedConfig(
            UseGpu: false,
            ModelName: cpuTarget,
            Reason: $"Limited VRAM ({hw.VramGb}GB). Using CPU mode with {(cpuTarget == ModelMid ? "balanced" : "lightweight")} model."
        );
    }
}
