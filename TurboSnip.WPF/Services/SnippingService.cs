using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TurboSnip.WPF.Views;

namespace TurboSnip.WPF.Services;

public enum SnipAction
{
    Cancel,
    Copy,
    OcrOnly,
    Translate
}

public class SnippetResult
{
    public Bitmap? Image { get; set; }
    public SnipAction Action { get; set; }
}

public interface ISnippingService
{
    Task<SnippetResult> SnipAsync(string copyNotificationText = "Screenshot Copied");
}

public partial class SnippingService : ISnippingService
{
    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    public async Task<SnippetResult> SnipAsync(string copyNotificationText = "Screenshot Copied")
    {
        // 1. Capture Virtual Screen (All Monitors)
        // For MVP, capturing Primary is safer/faster.
        // To properly support multi-monitor, we'd need to use System.Windows.Forms.SystemInformation.VirtualScreen

        var screenRect = System.Windows.Forms.SystemInformation.VirtualScreen;
        var bmp = new Bitmap(screenRect.Width, screenRect.Height);

        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        }

        // 2. Wrap in BitmapSource for WPF
        IntPtr hBitmap = bmp.GetHbitmap();
        BitmapSource screenSource;
        try
        {
            screenSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }

        // Freeze to allow cross-thread access if needed, though we run on UI thread here usually.
        screenSource.Freeze();

        // 3. Show Snipping Window
        // Use TCS to await window closing
        var tcs = new TaskCompletionSource<SnippetResult>();

        // Ensure UI operations happen on UI thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var win = new SnippingWindow(screenSource)
            {
                NotificationMessage = copyNotificationText
            };

            win.Closed += (s, e) =>
            {
                if (win.ResultAction != SnipAction.Cancel && win.ResultAction != SnipAction.Copy && win.SelectedRegion.Width > 0 && win.SelectedRegion.Height > 0)
                {
                    // For Copy action, we might have already handled clipboard in the window, or handle it here?
                    // User requested: "Copy: Clipboard.SetImage... Close. (Don't wake main window)".
                    // Actually, if we return SnippetResult with Action=Copy, MainViewModel can decide not to show window.
                    // But if logical separation, MainViewModel handles logic. But SnippingWindow handles UI.
                    // Let's copy image in SnippingWindow for immediate feedback? 
                    // Wait, Step 3 says "Copy... DialogResult = false, Close".
                    // Step 4 says "Case Copy/Cancel: Do nothing".
                    // So SnippingWindow handles clipboard for Copy.

                    try
                    {
                        var cropRect = win.SelectedRegion;
                        // Adjust coordinates relative to VirtualScreen if needed? 
                        // SnippingWindow Canvas (0,0) corresponds to VirtualScreen (Left, Top) if window is maximizing over it correctly.
                        // If we use VirtualScreen size, we should ensure coordinates match.
                        // For now assuming SnippingWindow covers full VirtualScreen area (Top/Left = VirtualScreen Top/Left).

                        var cropped = bmp.Clone(cropRect, bmp.PixelFormat);

                        // For OCR/Translate, we return the image
                        tcs.SetResult(new SnippetResult { Image = cropped, Action = win.ResultAction });
                    }
                    catch (Exception ex)
                    {
                        bmp.Dispose(); // FIX: Ensure disposal even on exception
                        tcs.SetException(ex);
                        return; // Early return after exception
                    }
                }
                else
                {
                    // Cancel or Copy (Logic handled in window for Copy?)
                    // If Action is Copy, we should have the image?
                    // Actually if Action is Copy, User says "Clipboard.SetImage(croppedBitmap)" in Window (Task 3).
                    // So we don't need to return image to MainViewModel necessarily.
                    tcs.SetResult(new SnippetResult { Image = null, Action = win.ResultAction });
                }
                bmp.Dispose(); // Dispose original full capture
            };

            // Allow window to be maximized over all screens
            // IMPORTANT: WPF Window Size/Location is in Logical Units (DPI Scaled).
            // VirtualScreen is in Device Pixels.
            // We must undo the scale.

            double dpiX = 1.0;
            double dpiY = 1.0;

            // FIX: Check if MainWindow is available before getting DPI
            var mainWindow = System.Windows.Application.Current?.MainWindow;
            if (mainWindow != null)
            {
                var source = PresentationSource.FromVisual(mainWindow);
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }
            }

            win.Left = screenRect.Left / dpiX;
            win.Top = screenRect.Top / dpiY;
            win.Width = screenRect.Width / dpiX;
            win.Height = screenRect.Height / dpiY;

            win.Show();
            win.Activate(); // Focus
        });

        return await tcs.Task;
    }
}
