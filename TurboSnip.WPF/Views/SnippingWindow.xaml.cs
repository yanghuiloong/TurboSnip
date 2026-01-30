using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TurboSnip.WPF.Services;

using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TurboSnip.WPF.Views;

public partial class SnippingWindow : Window
{
    private readonly BitmapSource _screenImage;
    private System.Windows.Point _startPoint;
    private bool _isDragging;

    public System.Drawing.Rectangle SelectedRegion { get; private set; }
    public SnipAction ResultAction { get; private set; } = SnipAction.Cancel;

    public SnippingWindow(BitmapSource screenImage)
    {
        InitializeComponent();
        _screenImage = screenImage;
        BackgroundImage.Source = _screenImage;

        // Note: Window positioning is handled by the Service/Caller to cover VirtualScreen.
        // We rely on this Window being exactly the size of the passed bitmap (screen).

        this.KeyDown += OnKeyDown;

        // Attach Mouse Events to the Canvas which covers the whole window
        MainCanvas.MouseDown += OnMouseDown;
        MainCanvas.MouseMove += OnMouseMove;
        MainCanvas.MouseUp += OnMouseUp;
        MainCanvas.MouseRightButtonUp += OnMouseRightButtonUp;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // If clicking on Toolbar, ignore (Bubbling should be handled by buttons, but just in case)
        if (e.OriginalSource is DependencyObject obj && FindParent<StackPanel>(obj) == ToolbarPanel)
            return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            // Reset Toolbar
            ToolbarPanel.Visibility = Visibility.Collapsed;

            _startPoint = e.GetPosition(MainCanvas);
            _isDragging = true;

            SelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionRect, _startPoint.X);
            Canvas.SetTop(SelectionRect, _startPoint.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;

            // Capture Mouse to ensure we track it even if it leaves window (unlikely)
            MainCanvas.CaptureMouse();
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            var endPoint = e.GetPosition(MainCanvas);

            var x = Math.Min(_startPoint.X, endPoint.X);
            var y = Math.Min(_startPoint.Y, endPoint.Y);
            var w = Math.Abs(_startPoint.X - endPoint.X);
            var h = Math.Abs(_startPoint.Y - endPoint.Y);

            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = w;
            SelectionRect.Height = h;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            MainCanvas.ReleaseMouseCapture();

            var x = Canvas.GetLeft(SelectionRect);
            var y = Canvas.GetTop(SelectionRect);
            var w = SelectionRect.Width;
            var h = SelectionRect.Height;

            // Simple debounce/check for tiny accidental clicks
            if (w > 5 && h > 5)
            {
                // Handle High DPI scaling for RESULT
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    double dpiX = source.CompositionTarget.TransformToDevice.M11;
                    double dpiY = source.CompositionTarget.TransformToDevice.M22;

                    SelectedRegion = new System.Drawing.Rectangle(
                        (int)(x * dpiX),
                        (int)(y * dpiY),
                        (int)(w * dpiX),
                        (int)(h * dpiY));

                    // Show Toolbar instead of Closing
                    ToolbarPanel.Visibility = Visibility.Visible;

                    // CRITICAL FIX: Force layout update so ActualWidth is valid
                    ToolbarPanel.UpdateLayout();

                    // Position Toolbar: Bottom Left aligned, or Top Left if no space
                    double toolbarTop = y + h + 5;
                    if (toolbarTop + ToolbarPanel.ActualHeight > this.ActualHeight)
                    {
                        toolbarTop = y - ToolbarPanel.ActualHeight - 5;
                    }
                    // Ensure it stays within bounds vertically
                    if (toolbarTop < 0) toolbarTop = 0;

                    // Ensure horizontal bounds
                    // ALIGN RIGHT: x + w - ToolbarWidth
                    double toolbarLeft = (x + w) - ToolbarPanel.ActualWidth;

                    // Fallback to Left Align if it goes offscreen (left side)
                    if (toolbarLeft < 0)
                    {
                        toolbarLeft = x;
                    }
                    // Ensure it doesn't go offscreen right
                    if (toolbarLeft + ToolbarPanel.ActualWidth > this.ActualWidth)
                    {
                        toolbarLeft = this.ActualWidth - ToolbarPanel.ActualWidth;
                    }

                    Canvas.SetLeft(ToolbarPanel, toolbarLeft);
                    Canvas.SetTop(ToolbarPanel, toolbarTop);

                }
            }
            else
            {
                // Reset selection if just a click.
                SelectionRect.Visibility = Visibility.Collapsed;
                ToolbarPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Right Click -> Cancel/Close
        ResultAction = SnipAction.Cancel;
        Close();
    }

    private void OcrBtn_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = SnipAction.OcrOnly;
        Close();
    }

    private void TranslateBtn_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = SnipAction.Translate;
        Close();
    }

    // Private backing field
    private string _notificationMessage = "Screenshot Copied";

    // Public property to set localized message
    public string NotificationMessage
    {
        set { _notificationMessage = value; }
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Crop and set clipboard
            if (SelectedRegion.Width > 0 && SelectedRegion.Height > 0)
            {
                var crop = new CroppedBitmap(_screenImage, new Int32Rect(SelectedRegion.X, SelectedRegion.Y, SelectedRegion.Width, SelectedRegion.Height));
                System.Windows.Clipboard.SetImage(crop);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Clipboard operation failed: {ex.Message}"); }

        ResultAction = SnipAction.Copy;

        // Show non-blocking notification
        var notification = new NotificationWindow(_notificationMessage);
        notification.Show();

        // Close blocking overlay immediately
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = SnipAction.Cancel;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ResultAction = SnipAction.Cancel;
            Close();
        }
    }
}
