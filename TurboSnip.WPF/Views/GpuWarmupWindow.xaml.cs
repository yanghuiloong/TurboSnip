using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TurboSnip.WPF.Services;

namespace TurboSnip.WPF.Views;

public partial class GpuWarmupWindow : Window
{
    private readonly ILlmService _llmService;
    private readonly IHotkeyService? _hotkeyService;
    private readonly string _modelName;
    private Storyboard? _spinnerStoryboard;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _hintTimer;
    private int _currentHintIndex = 0;
    
    // 提示文本列表 - 轮流显示
    private static readonly string[] HintMessages = new[]
    {
        "⏱️ 加载时间取决于您的显卡性能",
        "💡 您可以最小化窗口，预热完成后会弹窗提醒",
        "⚡ 预热完成后翻译将非常快速",
        "🎯 首次加载需要预热，后续切换会更快",
        "📋 预热期间可以继续其他工作"
    };
    
    /// <summary>
    /// Static reference to the current warmup window instance, if any.
    /// Used to bring the window to focus when user tries to use the app during warmup.
    /// </summary>
    public static GpuWarmupWindow? CurrentInstance { get; private set; }
    
    public bool WarmupComplete { get; private set; }
    
    /// <summary>
    /// Create a warmup window for model loading
    /// </summary>
    /// <param name="llmService">LLM service instance</param>
    /// <param name="modelName">Model name to display (optional)</param>
    /// <param name="hotkeyService">Hotkey service to disable during loading (optional)</param>
    public GpuWarmupWindow(ILlmService llmService, string? modelName = null, IHotkeyService? hotkeyService = null)
    {
        InitializeComponent();
        _llmService = llmService;
        _hotkeyService = hotkeyService;
        _modelName = modelName ?? "";
        
        // Set static instance for external access
        CurrentInstance = this;
        
        // Show model name if provided
        if (!string.IsNullOrEmpty(_modelName))
        {
            ModelNameText.Text = $"模型: {GetFriendlyModelName(_modelName)}";
        }
        
        Loaded += OnLoaded;
        Closing += OnClosing;
        
        // Monitor main window state to auto-minimize when main window is minimized/hidden
        if (System.Windows.Application.Current.MainWindow != null)
        {
            System.Windows.Application.Current.MainWindow.StateChanged += MainWindow_StateChanged;
        }
    }
    
    /// <summary>
    /// Bring the warmup window to focus (restore from minimized state).
    /// Called when user tries to use the app during warmup.
    /// Only brings the warmup window to front, not the main window.
    /// </summary>
    public static void BringToFocus()
    {
        if (CurrentInstance != null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (CurrentInstance.WindowState == WindowState.Minimized)
                {
                    CurrentInstance.WindowState = WindowState.Normal;
                }
                
                // Temporarily set Topmost to bring window to front, then remove it
                CurrentInstance.Topmost = true;
                CurrentInstance.Activate();
                CurrentInstance.Focus();
                CurrentInstance.Topmost = false;
            });
        }
    }
    
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // If main window is minimized, minimize this window too
        if (System.Windows.Application.Current.MainWindow?.WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Minimized;
        }
    }
    
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }
    
    private static string GetFriendlyModelName(string fileName)
    {
        // Convert file name to friendly name
        if (fileName.Contains("qwen2.5-1.5b", StringComparison.OrdinalIgnoreCase))
            return "Qwen 2.5 - 1.5B (轻量版)";
        if (fileName.Contains("qwen2.5-3b", StringComparison.OrdinalIgnoreCase))
            return "Qwen 2.5 - 3B (标准版)";
        if (fileName.Contains("qwen3-4b", StringComparison.OrdinalIgnoreCase) || 
            fileName.Contains("Qwen3-4B", StringComparison.OrdinalIgnoreCase))
            return "Qwen 3 - 4B (高级版)";
        return fileName;
    }
    
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartSpinner();
        StartHintRotation();
        _cts = new CancellationTokenSource();
        
        // Disable hotkey during loading
        if (_hotkeyService != null)
        {
            _hotkeyService.IsEnabled = false;
        }
        
        try
        {
            // Subscribe to status updates
            _llmService.OnStatusUpdated += OnLlmStatusUpdated;
            
            // Step 1: Initialize LLM (Load model to GPU)
            StatusText.Text = "正在加载模型到显存...";
            
            // Initialize (this loads the model)
            await _llmService.InitializeAsync();
            
            // Step 2: Perform warmup translation (compile CUDA kernels)
            StatusText.Text = "正在预热 GPU...";
            
            // Do a test translation to fully warm up
            await PerformWarmupTranslation(_cts.Token);
            
            // Done! Stop hint rotation
            StopHintRotation();
            StatusText.Text = "✅ 准备就绪！";
            HintText.Text = "🎉 现在可以开始使用了";
            WarmupComplete = true;
            
            // Check if window is not in foreground (minimized, or not active/obscured by other windows)
            bool isNotInForeground = (WindowState == WindowState.Minimized) || !IsActive;
            
            if (isNotInForeground)
            {
                // Show notification to user since they can't see this window
                ShowWarmupCompleteNotification();
                
                // Restore window to show completion
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();
            }
            
            await Task.Delay(800); // Brief pause to show success
            
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            // User closed window - should not happen as we don't allow close
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warmup error: {ex.Message}");
            // Show error but still allow to continue
            StopHintRotation();
            StatusText.Text = "⚠️ 加载出现问题";
            HintText.Text = "将在首次翻译时重试";
            WarmupComplete = false;
            
            await Task.Delay(1500);
            
            DialogResult = true;
            Close();
        }
        finally
        {
            _llmService.OnStatusUpdated -= OnLlmStatusUpdated;
            StopSpinner();
            StopHintRotation();
            
            // Re-enable hotkey after loading
            if (_hotkeyService != null)
            {
                _hotkeyService.IsEnabled = true;
            }
        }
    }
    
    private void OnLlmStatusUpdated(string status)
    {
        Dispatcher.Invoke(() =>
        {
            // Show detailed status
            if (status.Contains("Initializing") || status.Contains("初始化"))
            {
                StatusText.Text = "正在初始化模型...";
            }
            else if (status.Contains("GPU Mode") || status.Contains("GPU"))
            {
                StatusText.Text = "正在加载到 GPU...";
            }
            else if (status.Contains("CPU Mode") || status.Contains("CPU"))
            {
                StatusText.Text = "正在加载到 CPU...";
                HintText.Text = "💡 CPU 模式，速度较慢但仍可使用";
            }
        });
    }
    
    private async Task PerformWarmupTranslation(CancellationToken token)
    {
        // Perform a small translation to fully compile CUDA kernels
        string testInput = "Hello";
        
        await foreach (var chunk in _llmService.TranslateStreamAsync(testInput, token))
        {
            // Just consume the output, we don't need to show it
            token.ThrowIfCancellationRequested();
        }
        
        System.Diagnostics.Debug.WriteLine("Warmup translation complete");
    }
    
    private void StartSpinner()
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
        _spinnerStoryboard.Begin();
    }
    
    private void StopSpinner()
    {
        _spinnerStoryboard?.Stop();
    }
    
    private void StartHintRotation()
    {
        _currentHintIndex = 0;
        HintText.Text = HintMessages[0];
        
        _hintTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3) // 每3秒切换一次提示
        };
        _hintTimer.Tick += (s, e) =>
        {
            _currentHintIndex = (_currentHintIndex + 1) % HintMessages.Length;
            
            // 使用淡入淡出动画切换提示
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (_, _) =>
            {
                HintText.Text = HintMessages[_currentHintIndex];
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                HintText.BeginAnimation(OpacityProperty, fadeIn);
            };
            HintText.BeginAnimation(OpacityProperty, fadeOut);
        };
        _hintTimer.Start();
    }
    
    private void StopHintRotation()
    {
        _hintTimer?.Stop();
        _hintTimer = null;
    }
    
    private void ShowWarmupCompleteNotification()
    {
        // Show a notification window to alert user that warmup is complete
        try
        {
            var notification = new NotificationWindow(
                $"✅ {GetFriendlyModelName(_modelName)} 已准备就绪！"
            );
            notification.Show();
            
            // Also bring the window to front after showing notification
            System.Diagnostics.Debug.WriteLine("Showed warmup complete notification");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to show notification: {ex.Message}");
        }
    }
    
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Unsubscribe from main window events
        if (System.Windows.Application.Current.MainWindow != null)
        {
            System.Windows.Application.Current.MainWindow.StateChanged -= MainWindow_StateChanged;
        }
        
        // Prevent user from closing while loading (unless warmup is complete)
        if (!WarmupComplete)
        {
            e.Cancel = true;
            return;
        }
        
        // Clear static instance
        CurrentInstance = null;
        
        _cts?.Cancel();
        StopSpinner();
    }
}
