using System.Reflection;
using System.Windows;

namespace TurboSnip.WPF.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version}";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
