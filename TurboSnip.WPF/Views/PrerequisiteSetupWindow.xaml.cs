using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using TurboSnip.WPF.Services;

namespace TurboSnip.WPF.Views;

public partial class PrerequisiteSetupWindow : Window
{
    private readonly PrerequisiteInstallerService _installer;
    private CancellationTokenSource? _cts;
    private Storyboard? _spinnerStoryboard;
    
    private bool _hasGpu;
    private bool _hasVcRedist;
    private bool _hasCuda;
    
    public bool AllPrerequisitesMet { get; private set; } = false;
    public bool UserRequestedExit { get; private set; } = false;
    
    public PrerequisiteSetupWindow()
    {
        InitializeComponent();
        _installer = new PrerequisiteInstallerService();
        
        // Setup event handlers
        _installer.OnStatusUpdate += msg => Dispatcher.Invoke(() => 
        {
            StatusMessage.Text = msg;
            DownloadText.Text = msg;
        });
        
        _installer.OnDownloadProgress += progress => Dispatcher.Invoke(() => 
        {
            DownloadProgressBar.Value = progress * 100;
        });
        
        _installer.OnInstallingStateChanged += isInstalling => Dispatcher.Invoke(() =>
        {
            if (isInstalling)
            {
                DownloadPanel.Visibility = Visibility.Collapsed;
                InstallingPanel.Visibility = Visibility.Visible;
                StartSpinner();
            }
            else
            {
                InstallingPanel.Visibility = Visibility.Collapsed;
                StopSpinner();
            }
        });
        
        // Create spinner animation
        CreateSpinnerAnimation();
        
        // Check prerequisites on load
        Loaded += async (s, e) => await CheckPrerequisitesAsync();
    }
    
    private void CreateSpinnerAnimation()
    {
        _spinnerStoryboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, SpinnerBorder);
        Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        _spinnerStoryboard.Children.Add(animation);
    }
    
    private void StartSpinner()
    {
        _spinnerStoryboard?.Begin();
    }
    
    private void StopSpinner()
    {
        _spinnerStoryboard?.Stop();
    }
    
    private async Task CheckPrerequisitesAsync()
    {
        ProgressTitle.Text = "正在检测系统环境...";
        
        await Task.Run(() =>
        {
            _hasGpu = PrerequisiteInstallerService.HasNvidiaGpu();
            _hasVcRedist = PrerequisiteInstallerService.IsVcRedistInstalled();
            _hasCuda = PrerequisiteInstallerService.IsCudaInstalled();
        });
        
        // Update GPU status
        if (_hasGpu)
        {
            var gpuName = PrerequisiteInstallerService.GetNvidiaGpuName();
            GpuNameText.Text = gpuName;
            GpuStatusIcon.Text = "✅";
        }
        else
        {
            GpuNameText.Text = "未检测到 NVIDIA GPU (将使用 CPU 模式)";
            GpuStatusIcon.Text = "⚠️";
        }
        
        // Update VC++ status
        if (_hasVcRedist)
        {
            VcRedistStatusText.Text = "已安装";
            VcRedistStatusIcon.Text = "✅";
        }
        else
        {
            VcRedistStatusText.Text = "未安装 (必需)";
            VcRedistStatusIcon.Text = "❌";
        }
        
        // Update CUDA status
        if (!_hasGpu)
        {
            CudaStatusText.Text = "不需要 (无 NVIDIA GPU)";
            CudaStatusIcon.Text = "➖";
            _hasCuda = true; // Mark as satisfied since no GPU
        }
        else if (_hasCuda)
        {
            CudaStatusText.Text = "已安装";
            CudaStatusIcon.Text = "✅";
        }
        else
        {
            CudaStatusText.Text = "未安装 (GPU加速必需)";
            CudaStatusIcon.Text = "❌";
        }
        
        // Check if all prerequisites are met
        AllPrerequisitesMet = _hasVcRedist && _hasCuda;
        
        if (AllPrerequisitesMet)
        {
            // All good, don't show the progress panel
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallButton.Content = "继续";
            InstallButton.Background = System.Windows.Media.Brushes.DodgerBlue;
            WarningPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Show what's missing in the warning panel only
            ProgressPanel.Visibility = Visibility.Collapsed;
            var missing = new System.Collections.Generic.List<string>();
            if (!_hasVcRedist) missing.Add("Visual C++ 运行库");
            if (_hasGpu && !_hasCuda) missing.Add("CUDA 运行时");
            WarningPanel.Visibility = Visibility.Visible;
        }
    }
    
    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (AllPrerequisitesMet)
        {
            DialogResult = true;
            Close();
            return;
        }
        
        InstallButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        WarningPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible; // Show progress panel during installation
        
        _cts = new CancellationTokenSource();
        
        try
        {
            // Install VC++ Redistributable first if needed
            if (!_hasVcRedist)
            {
                ProgressTitle.Text = "安装 Visual C++ 运行库";
                DownloadPanel.Visibility = Visibility.Visible;
                DownloadProgressBar.Value = 0;
                
                var vcResult = await _installer.InstallVcRedistAsync(_cts.Token);
                
                if (vcResult)
                {
                    VcRedistStatusText.Text = "已安装";
                    VcRedistStatusIcon.Text = "✅";
                    _hasVcRedist = true;
                }
                else
                {
                    VcRedistStatusText.Text = "安装失败";
                    VcRedistStatusIcon.Text = "❌";
                    ShowError("Visual C++ 运行库安装失败，请手动安装后重试。");
                    return;
                }
            }
            
            // Install CUDA if needed (only if has NVIDIA GPU)
            if (_hasGpu && !_hasCuda)
            {
                ProgressTitle.Text = "安装 CUDA 运行时";
                DownloadPanel.Visibility = Visibility.Visible;
                DownloadProgressBar.Value = 0;
                
                var cudaResult = await _installer.InstallCudaAsync(_cts.Token);
                
                if (cudaResult)
                {
                    CudaStatusText.Text = "已安装";
                    CudaStatusIcon.Text = "✅";
                    _hasCuda = true;
                }
                else
                {
                    CudaStatusText.Text = "安装失败";
                    CudaStatusIcon.Text = "❌";
                    ShowError("CUDA 运行时安装失败，请手动安装 CUDA Toolkit 12.4 后重试。");
                    return;
                }
            }
            
            // All done!
            DownloadPanel.Visibility = Visibility.Collapsed;
            InstallingPanel.Visibility = Visibility.Collapsed;
            
            AllPrerequisitesMet = true;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressTitle.Text = "✅ 安装完成！";
            StatusMessage.Text = "组件已安装成功。请关闭此程序后重新启动 TurboSnip。\n\n（首次启动可能需要等待 10-30 秒进行 GPU 预热）";
            
            InstallButton.Content = "关闭程序";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= InstallButton_Click;
            InstallButton.Click += (s, args) =>
            {
                // Just close the application, user will restart manually
                Environment.Exit(0);
            };
        }
        catch (OperationCanceledException)
        {
            ProgressTitle.Text = "安装已取消";
            StatusMessage.Text = "";
        }
        catch (Exception ex)
        {
            ShowError($"安装过程中发生错误: {ex.Message}");
        }
        finally
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
            InstallingPanel.Visibility = Visibility.Collapsed;
            StopSpinner();
            
            if (!AllPrerequisitesMet)
            {
                InstallButton.IsEnabled = true;
                ExitButton.IsEnabled = true;
            }
        }
    }
    
    private void ShowError(string message)
    {
        System.Windows.MessageBox.Show(
            message,
            "安装错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        
        InstallButton.IsEnabled = true;
        ExitButton.IsEnabled = true;
    }
    
    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        UserRequestedExit = true;
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }
    
    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        StopSpinner();
        base.OnClosed(e);
    }
}
