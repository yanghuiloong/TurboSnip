using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace TurboSnip.WPF.Views
{
    public partial class NotificationWindow : Window
    {
        public NotificationWindow(string message = "Screenshot Copied")
        {
            InitializeComponent();
            ToastText.Text = message;

            // Start animation on load
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Initial State
                this.Opacity = 0;

                // 2. Fade In
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2));
                this.BeginAnimation(OpacityProperty, fadeIn);

                // 3. Wait
                await Task.Delay(1200);

                // 4. Fade Out
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
                fadeOut.Completed += (s, _) => Close(); // Close window after animation
                this.BeginAnimation(OpacityProperty, fadeOut);
            }
            catch (Exception ex)
            {
                // Handle any errors in async void gracefully
                System.Diagnostics.Debug.WriteLine($"NotificationWindow animation error: {ex.Message}");
                try { Close(); } catch { }
            }
        }
    }
}