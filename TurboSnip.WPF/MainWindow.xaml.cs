using System.Windows;
using TurboSnip.WPF.ViewModels;

namespace TurboSnip.WPF;

public partial class MainWindow : Window
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isRealExit = false;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (s, e) =>
        {
            // Wait for the app to be fully idle and rendered to ensure DWM is ready
            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => ApplyTheme(_isDark)));
        };

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "TurboSnip",
            Visible = true
        };

        try
        {
            // Try to load custom icon from resources
            var resourceInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/ico/app.ico"));
            if (resourceInfo?.Stream != null)
            {
                using var stream = resourceInfo.Stream; // Properly dispose the stream
                _notifyIcon.Icon = new System.Drawing.Icon(stream);
            }
            else
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        }
        catch
        {
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        _notifyIcon.DoubleClick += (s, e) => ShowWindow();

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open", null, (s, e) => ShowWindow());
        contextMenu.Items.Add("Exit", null, (s, e) => ForceExit());
        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ForceExit()
    {
        _isRealExit = true;
        _notifyIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isRealExit)
        {
            e.Cancel = true;
            Hide(); // Minimize to Tray
        }
        else
        {
            base.OnClosing(e);
        }
    }

    private bool _isDark = true;

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        ApplyTheme(_isDark);
    }

    private void ApplyTheme(bool isDark)
    {
        SetTitleBarTheme(isDark);

        if (isDark)
        {
            // Dark Mode
            UpdateColor("AppBackgroundColor", "#1E1E1E");
            UpdateBrush("AppBackgroundBrush", "#1E1E1E");

            UpdateColor("ContentBackgroundColor", "#252526");
            UpdateBrush("ContentBackgroundBrush", "#252526");

            UpdateColor("BorderColor", "#3E3E42");
            UpdateBrush("BorderBrush", "#3E3E42");

            UpdateColor("PrimaryTextColor", "#FFFFFF");
            UpdateBrush("PrimaryTextBrush", "#FFFFFF");

            UpdateColor("TextSecondaryColor", "#CCCCCC");
            UpdateBrush("TextSecondaryBrush", "#CCCCCC");
        }
        else
        {
            // Light Mode
            UpdateColor("AppBackgroundColor", "#F3F3F3");
            UpdateBrush("AppBackgroundBrush", "#F3F3F3");

            UpdateColor("ContentBackgroundColor", "#FFFFFF");
            UpdateBrush("ContentBackgroundBrush", "#FFFFFF");

            UpdateColor("BorderColor", "#CCCCCC");
            UpdateBrush("BorderBrush", "#CCCCCC");

            UpdateColor("PrimaryTextColor", "#000000");
            UpdateBrush("PrimaryTextBrush", "#000000");

            UpdateColor("TextSecondaryColor", "#333333"); // Dark Grey for Light Background
            UpdateBrush("TextSecondaryBrush", "#333333");
        }
    }

    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private void SetTitleBarTheme(bool isDark)
    {
        if (System.Environment.OSVersion.Version.Major >= 10)
        {
            // Check if handle is available
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return;

            int darkMode = isDark ? 1 : 0;

            // Try standard attribute (Win11 / Win10 20H1+) first
            int hr = DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // If that fails (return value != 0), try the legacy attribute
            if (hr != 0)
            {
                _ = DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
            }

            // Force Frame Change - usage of SetWindowPos is the standard way to trigger DWM frame repaint
            SetWindowPos(helper.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);

            // "Nuclear Option": Resize Twitch
            // Force a meaningful resize (1px) to compel the Window Manager to discard the cached frame
            if (WindowState == WindowState.Normal)
            {
                Width += 1; // Change size by 1 pixel
                Width -= 1; // Revert size
            }
        }
    }

    private void UpdateColor(string key, string hex)
    {
        this.Resources[key] = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    private void UpdateBrush(string key, string hex)
    {
        this.Resources[key] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }
}