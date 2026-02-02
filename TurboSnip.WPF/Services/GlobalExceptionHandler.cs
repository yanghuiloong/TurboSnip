using System;
using System.IO;
using System.Text;
using System.Windows;

namespace TurboSnip.WPF.Services;

public class GlobalExceptionHandler
{
    private static readonly object _lock = new();
    private const string LogFileName = "error.log";

    public static void HandleException(Exception ex, string source, bool isTerminating = false)
    {
        try
        {
            LogException(ex, source);

            string message = isTerminating
                ? $"A critical error occurred and the application must close.\n\nError: {ex.Message}\nSource: {source}"
                : $"An unexpected error occurred.\n\nError: {ex.Message}\nSource: {source}";

            // Show dialog on UI thread if possible
            if (System.Windows.Application.Current != null && System.Windows.Application.Current.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(message, "TurboSnip Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            else
            {
                System.Windows.MessageBox.Show(message, "TurboSnip Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception logEx)
        {
            // Fallback if logging fails
            System.Diagnostics.Debug.WriteLine($"Failed to log exception: {logEx.Message}");
        }
    }

    private static void LogException(Exception ex, string source)
    {
        lock (_lock)
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logPath);
            string filePath = Path.Combine(logPath, LogFileName);

            var sb = new StringBuilder();
            sb.AppendLine("-----------------------------------------------------------------------------");
            sb.AppendLine($"Date: {DateTime.Now}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Exception: {ex.GetType().FullName}");
            sb.AppendLine($"Message: {ex.Message}");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                sb.AppendLine("Inner Exception:");
                sb.AppendLine($"{ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                sb.AppendLine(ex.InnerException.StackTrace);
            }
            sb.AppendLine("-----------------------------------------------------------------------------");

            File.AppendAllText(filePath, sb.ToString());
        }
    }
}
