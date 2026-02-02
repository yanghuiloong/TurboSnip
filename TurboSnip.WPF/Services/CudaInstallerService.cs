using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TurboSnip.WPF.Services;

/// <summary>
/// Service to check and install CUDA Runtime for users with NVIDIA GPU but no CUDA installed.
/// This enables GPU acceleration without requiring users to manually install CUDA Toolkit.
/// </summary>
public class CudaInstallerService
{
    // CUDA 12.4 Redistributable - lightweight runtime only (~600MB download, ~1.5GB installed)
    // This is the minimum required for LLamaSharp CUDA backend
    private const string CUDA_REDIST_URL = "https://developer.download.nvidia.com/compute/cuda/12.4.1/local_installers/cuda_12.4.1_551.78_windows.exe";
    private const string CUDA_REDIST_FILENAME = "cuda_12.4.1_installer.exe";
    private const string CUDA_MIN_VERSION = "12.4";
    
    public event Action<string>? OnProgressUpdate;
    public event Action<double>? OnDownloadProgress;
    
    /// <summary>
    /// Check if CUDA Runtime is installed and accessible
    /// </summary>
    public static bool IsCudaInstalled()
    {
        // Method 1: Check CUDA_PATH environment variable
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cudaPath) && Directory.Exists(cudaPath))
        {
            var cudartPath = Path.Combine(cudaPath, "bin", "cudart64_12.dll");
            if (File.Exists(cudartPath))
                return true;
        }
        
        // Method 2: Check registry for CUDA installation
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NVIDIA Corporation\GPU Computing Toolkit\CUDA");
            if (key != null)
            {
                var subKeys = key.GetSubKeyNames();
                foreach (var version in subKeys)
                {
                    if (version.StartsWith("v12"))
                        return true;
                }
            }
        }
        catch { }
        
        // Method 3: Check common installation paths
        var commonPaths = new[]
        {
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.4\bin\cudart64_12.dll",
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.5\bin\cudart64_12.dll",
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6\bin\cudart64_12.dll",
        };
        
        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Check if the system has an NVIDIA GPU
    /// </summary>
    public static bool HasNvidiaGpu()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var item in searcher.Get())
            {
                var name = item["Name"]?.ToString() ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        
        return false;
    }
    
    /// <summary>
    /// Get the NVIDIA GPU name
    /// </summary>
    public static string GetNvidiaGpuName()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var item in searcher.Get())
            {
                var name = item["Name"]?.ToString() ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        catch { }
        
        return "Unknown NVIDIA GPU";
    }
    
    /// <summary>
    /// Download and install CUDA Runtime silently
    /// </summary>
    public async Task<bool> InstallCudaRuntimeAsync(CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TurboSnip_CudaInstall");
        var installerPath = Path.Combine(tempDir, CUDA_REDIST_FILENAME);
        
        try
        {
            // Create temp directory
            Directory.CreateDirectory(tempDir);
            
            // Download installer
            OnProgressUpdate?.Invoke("正在下载 CUDA 运行时 (约600MB)...");
            
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);
            
            using var response = await httpClient.GetAsync(CUDA_REDIST_URL, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var downloadedBytes = 0L;
            
            using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                int bytesRead;
                
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;
                    
                    if (totalBytes > 0)
                    {
                        var progress = (double)downloadedBytes / totalBytes;
                        OnDownloadProgress?.Invoke(progress);
                        OnProgressUpdate?.Invoke($"正在下载 CUDA 运行时... {progress:P0} ({downloadedBytes / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB)");
                    }
                }
            }
            
            // Run installer silently with only runtime components
            OnProgressUpdate?.Invoke("正在安装 CUDA 运行时 (需要管理员权限)...");
            
            // Silent install with only the required components
            // -s = silent, cudart_12.4 = CUDA Runtime, cublas_12.4 = cuBLAS
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "-s cudart_12.4 cublas_12.4 cublas_dev_12.4",
                UseShellExecute = true,
                Verb = "runas", // Request admin elevation
                CreateNoWindow = true
            };
            
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                OnProgressUpdate?.Invoke("无法启动 CUDA 安装程序");
                return false;
            }
            
            await process.WaitForExitAsync(cancellationToken);
            
            if (process.ExitCode == 0)
            {
                OnProgressUpdate?.Invoke("CUDA 运行时安装成功！请重启应用程序以启用 GPU 加速。");
                return true;
            }
            else
            {
                OnProgressUpdate?.Invoke($"CUDA 安装失败 (退出代码: {process.ExitCode})。请手动安装 CUDA Toolkit 12.4。");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            OnProgressUpdate?.Invoke("CUDA 安装已取消。");
            return false;
        }
        catch (Exception ex)
        {
            OnProgressUpdate?.Invoke($"CUDA 安装错误: {ex.Message}");
            return false;
        }
        finally
        {
            // Cleanup
            try
            {
                if (File.Exists(installerPath))
                    File.Delete(installerPath);
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch { }
        }
    }
    
    /// <summary>
    /// Check system and prompt for CUDA installation if needed
    /// Returns true if CUDA is available (installed or just installed)
    /// </summary>
    public static (bool HasGpu, bool HasCuda, string Message) CheckCudaStatus()
    {
        var hasGpu = HasNvidiaGpu();
        var hasCuda = IsCudaInstalled();
        
        if (!hasGpu)
        {
            return (false, false, "未检测到 NVIDIA GPU，将使用 CPU 模式（较慢）。");
        }
        
        if (hasCuda)
        {
            var gpuName = GetNvidiaGpuName();
            return (true, true, $"检测到 {gpuName}，CUDA 已安装，将使用 GPU 加速。");
        }
        
        var gpuNameNoNvidia = GetNvidiaGpuName();
        return (true, false, $"检测到 {gpuNameNoNvidia}，但未安装 CUDA 运行时。是否安装 CUDA 以启用 GPU 加速？");
    }
}
