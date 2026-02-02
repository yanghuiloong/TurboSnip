using System;
using System.Threading;
using System.Windows;
using TurboSnip.WPF.Services;

namespace TurboSnip.WPF.Views;

public partial class CudaSetupWindow : Window
{
    private readonly CudaInstallerService _installer;
    private CancellationTokenSource? _cts;
    private bool _installSucceeded = false;
    
    public bool InstallSucceeded => _installSucceeded;
    public bool SkippedInstall { get; private set; } = false;
    
    public CudaSetupWindow()
    {
        InitializeComponent();
        _installer = new CudaInstallerService();
        
        // Setup event handlers
        _installer.OnProgressUpdate += msg => Dispatcher.Invoke(() => ProgressText.Text = msg);
        _installer.OnDownloadProgress += progress => Dispatcher.Invoke(() => ProgressBar.Value = progress * 100);
        
        // Update UI with current status
        UpdateStatus();
    }
    
    private void UpdateStatus()
    {
        var (hasGpu, hasCuda, message) = CudaInstallerService.CheckCudaStatus();
        
        if (hasGpu)
        {
            GpuInfoText.Text = $"✓ 检测到 {CudaInstallerService.GetNvidiaGpuName()}";
            
            if (hasCuda)
            {
                CudaStatusText.Text = "✓ CUDA 运行时已安装";
                CudaStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                InstallButton.Content = "完成";
                InstallButton.Click -= InstallButton_Click;
                InstallButton.Click += (s, e) => { _installSucceeded = true; Close(); };
            }
            else
            {
                CudaStatusText.Text = "✗ CUDA 运行时未安装";
                CudaStatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }
        else
        {
            GpuInfoText.Text = "✗ 未检测到 NVIDIA GPU";
            GpuInfoText.Foreground = System.Windows.Media.Brushes.Gray;
            CudaStatusText.Text = "将使用 CPU 模式";
            CudaStatusText.Foreground = System.Windows.Media.Brushes.Gray;
            InstallButton.IsEnabled = false;
            InstallButton.Content = "不可用";
        }
    }
    
    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        
        _cts = new CancellationTokenSource();
        
        try
        {
            _installSucceeded = await _installer.InstallCudaRuntimeAsync(_cts.Token);
            
            if (_installSucceeded)
            {
                System.Windows.MessageBox.Show(
                    "CUDA 运行时安装成功！\n\n请重启 TurboSnip 以启用 GPU 加速。",
                    "安装成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"安装失败：{ex.Message}\n\n您可以稍后手动安装 CUDA Toolkit 12.4。",
                "安装失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            Close();
        }
    }
    
    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        SkippedInstall = true;
        _cts?.Cancel();
        Close();
    }
    
    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
