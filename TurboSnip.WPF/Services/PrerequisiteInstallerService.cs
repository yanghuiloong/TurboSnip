using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TurboSnip.WPF.Services;

/// <summary>
/// Service to check and install prerequisites: Visual C++ Redistributable and CUDA Runtime.
/// </summary>
public class PrerequisiteInstallerService
{
    // Visual C++ Redistributable 2015-2022 (x64)
    private const string VCREDIST_URL = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private const string VCREDIST_FILENAME = "vc_redist.x64.exe";
    
    // CUDA 12.4 Redistributable
    private const string CUDA_REDIST_URL = "https://developer.download.nvidia.com/compute/cuda/12.4.1/local_installers/cuda_12.4.1_551.78_windows.exe";
    private const string CUDA_REDIST_FILENAME = "cuda_12.4.1_installer.exe";
    
    public event Action<string>? OnStatusUpdate;
    public event Action<double>? OnDownloadProgress;
    public event Action<bool>? OnInstallingStateChanged; // true = installing, false = not installing
    
    #region Detection Methods
    
    /// <summary>
    /// Check if Visual C++ Redistributable 2015-2022 (x64) is installed
    /// </summary>
    public static bool IsVcRedistInstalled()
    {
        // Check registry for VC++ 2015-2022 Redistributable
        string[] registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
            @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
        };
        
        foreach (var path in registryPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key != null)
                {
                    var installed = key.GetValue("Installed");
                    if (installed != null && (int)installed == 1)
                    {
                        // Check version >= 14.40 (VS 2022 17.10+)
                        var major = key.GetValue("Major");
                        var minor = key.GetValue("Minor");
                        if (major != null && (int)major >= 14)
                            return true;
                    }
                }
            }
            catch { }
        }
        
        // Also check if the DLLs exist in System32
        var systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var requiredDlls = new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" };
        
        foreach (var dll in requiredDlls)
        {
            if (!File.Exists(Path.Combine(systemPath, dll)))
                return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Check if CUDA Runtime 12.x is installed
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
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.7\bin\cudart64_12.dll",
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
    /// Get prerequisite status
    /// </summary>
    public static (bool HasGpu, bool HasVcRedist, bool HasCuda) GetPrerequisiteStatus()
    {
        return (HasNvidiaGpu(), IsVcRedistInstalled(), IsCudaInstalled());
    }
    
    #endregion
    
    #region Installation Methods
    
    /// <summary>
    /// Download a file with progress reporting
    /// </summary>
    private async Task DownloadFileAsync(string url, string destinationPath, string displayName, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(30);
        
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var downloadedBytes = 0L;
        
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        
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
                OnStatusUpdate?.Invoke($"正在下载 {displayName}... {progress:P0} ({downloadedBytes / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB)");
            }
        }
    }
    
    /// <summary>
    /// Install Visual C++ Redistributable 2015-2022
    /// </summary>
    public async Task<bool> InstallVcRedistAsync(CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TurboSnip_Install");
        var installerPath = Path.Combine(tempDir, VCREDIST_FILENAME);
        
        try
        {
            Directory.CreateDirectory(tempDir);
            
            // Download
            OnStatusUpdate?.Invoke("正在下载 Visual C++ 运行库...");
            await DownloadFileAsync(VCREDIST_URL, installerPath, "Visual C++ 运行库", cancellationToken);
            
            // Install
            OnStatusUpdate?.Invoke("正在安装 Visual C++ 运行库 (需要管理员权限)...");
            OnInstallingStateChanged?.Invoke(true);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/install /quiet /norestart",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
            
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                OnStatusUpdate?.Invoke("无法启动安装程序");
                return false;
            }
            
            await process.WaitForExitAsync(cancellationToken);
            OnInstallingStateChanged?.Invoke(false);
            
            if (process.ExitCode == 0 || process.ExitCode == 3010) // 3010 = success, reboot required
            {
                OnStatusUpdate?.Invoke("Visual C++ 运行库安装成功！");
                return true;
            }
            else
            {
                OnStatusUpdate?.Invoke($"Visual C++ 安装失败 (退出代码: {process.ExitCode})");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            OnStatusUpdate?.Invoke("安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            OnStatusUpdate?.Invoke($"安装错误: {ex.Message}");
            return false;
        }
        finally
        {
            OnInstallingStateChanged?.Invoke(false);
            try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        }
    }
    
    /// <summary>
    /// Install CUDA Runtime 12.4
    /// </summary>
    public async Task<bool> InstallCudaAsync(CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TurboSnip_Install");
        var installerPath = Path.Combine(tempDir, CUDA_REDIST_FILENAME);
        
        try
        {
            Directory.CreateDirectory(tempDir);
            
            // Download
            OnStatusUpdate?.Invoke("正在下载 CUDA 运行时 (约600MB)...");
            await DownloadFileAsync(CUDA_REDIST_URL, installerPath, "CUDA 运行时", cancellationToken);
            
            // Install - only runtime components
            OnStatusUpdate?.Invoke("正在安装 CUDA 运行时 (需要管理员权限，请稍候)...");
            OnInstallingStateChanged?.Invoke(true);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                // Silent install with only runtime components: cudart, cublas
                Arguments = "-s cudart_12.4 cublas_12.4 cublas_dev_12.4",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
            
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                OnStatusUpdate?.Invoke("无法启动 CUDA 安装程序");
                return false;
            }
            
            // CUDA installer takes a while, update status periodically
            var startTime = DateTime.Now;
            while (!process.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { }
                    throw new OperationCanceledException();
                }
                
                var elapsed = DateTime.Now - startTime;
                OnStatusUpdate?.Invoke($"正在安装 CUDA 运行时... 已用时 {elapsed.Minutes}分{elapsed.Seconds}秒");
                await Task.Delay(1000, cancellationToken);
            }
            
            OnInstallingStateChanged?.Invoke(false);
            
            if (process.ExitCode == 0)
            {
                OnStatusUpdate?.Invoke("CUDA 运行时安装成功！");
                return true;
            }
            else
            {
                OnStatusUpdate?.Invoke($"CUDA 安装失败 (退出代码: {process.ExitCode})");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            OnStatusUpdate?.Invoke("安装已取消");
            return false;
        }
        catch (Exception ex)
        {
            OnStatusUpdate?.Invoke($"安装错误: {ex.Message}");
            return false;
        }
        finally
        {
            OnInstallingStateChanged?.Invoke(false);
            try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        }
    }
    
    #endregion
}
