using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using TurboSnip.WPF.Properties;
using TurboSnip.WPF.Services;

namespace TurboSnip.WPF;

public partial class StartupWindow : Window, INotifyPropertyChanged
{
    private readonly HardwareDetectionService _hwService;
    private readonly AppConfig _config;
    private List<string> _availableModels = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private void RefreshAllProperties() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    // Localized Text Properties
#pragma warning disable CA1822 // Mark members as static
    public string TitleText => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Setup_Title", TurboSnip.WPF.Properties.Resources.Culture) ?? "TurboSnip Setup";
    public string SubtitleText => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Setup_Subtitle", TurboSnip.WPF.Properties.Resources.Culture) ?? "Hardware Check & Configuration";
    public string CpuLabel => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Hardware_Cpu", TurboSnip.WPF.Properties.Resources.Culture) ?? "CPU";
    public string RamLabel => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Hardware_Ram", TurboSnip.WPF.Properties.Resources.Culture) ?? "RAM";
    public string GpuLabel => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Hardware_Gpu", TurboSnip.WPF.Properties.Resources.Culture) ?? "GPU (VRAM)";
    public string ModeLabel => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Execution_Mode", TurboSnip.WPF.Properties.Resources.Culture) ?? "Execution Mode";
    public string GpuModeText => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Mode_Gpu", TurboSnip.WPF.Properties.Resources.Culture) ?? "GPU (CUDA)";
    public string CpuModeText => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Mode_Cpu", TurboSnip.WPF.Properties.Resources.Culture) ?? "CPU Only";
    public string ModelLabel => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Label_SelectModel", TurboSnip.WPF.Properties.Resources.Culture) ?? "Select Language Model";
    public string LanguageLabel => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Label_Language", TurboSnip.WPF.Properties.Resources.Culture) ?? "Language";
    public string LaunchText => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Btn_Launch", TurboSnip.WPF.Properties.Resources.Culture) ?? "Launch TurboSnip";
    public string TipText => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Tip_Stability", TurboSnip.WPF.Properties.Resources.Culture) ?? "Recommended for stability.";
    public string GpuReqText => TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Gpu_Requirements", TurboSnip.WPF.Properties.Resources.Culture) ?? "GPU Requirements...";
#pragma warning restore CA1822

    // Data for Bindings
    public List<CultureInfo> AvailableLanguages { get; } =
    [
        new CultureInfo("zh-CN"),
        new CultureInfo("en-US")
    ];

    private CultureInfo _selectedLanguage;
    public CultureInfo SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage != value)
            {
                _selectedLanguage = value;
                NotifyPropertyChanged(nameof(SelectedLanguage)); // FIX: Notify property changed for UI binding
                Thread.CurrentThread.CurrentUICulture = value;
                Properties.Resources.Culture = value;

                RefreshAllProperties();
                UpdateHardwareDisplay(); // Refresh detected text if needed (e.g. Recommendation)
            }
        }
    }

    private HardwareInfo? _hwInfo;

    public StartupWindow(HardwareDetectionService hwService, AppConfig config)
    {
        InitializeComponent();
        DataContext = this;
        _hwService = hwService;
        _config = config;

        // Default to Chinese if system is Chinese, else default to Chinese (as req) or English?
        // User said: "Default is Chinese".
        var current = Thread.CurrentThread.CurrentUICulture;
        _selectedLanguage = current.Name.StartsWith("zh") ? current : new CultureInfo("zh-CN");

        // Ensure thread is set
        Thread.CurrentThread.CurrentUICulture = _selectedLanguage;
        Properties.Resources.Culture = _selectedLanguage;

        Loaded += StartupWindow_Loaded;
    }

    private async void StartupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Populate Models
        _availableModels = HardwareDetectionService.GetAvailableModels();
        ModelCombo.ItemsSource = _availableModels;

        // 2. Scan Hardware
        await Task.Delay(100); // UI Render first
        DetectHardware();
    }

    private void DetectHardware()
    {
        try
        {
            _hwInfo = HardwareDetectionService.DetectHardware();
            UpdateHardwareDisplay();

            // Pre-select based on recommendation
            var rec = _hwService.GetRecommendation();
            if (rec.ModelName != null && _availableModels.Contains(rec.ModelName))
            {
                ModelCombo.SelectedItem = rec.ModelName;
            }
            else if (_availableModels.Count > 0)
            {
                ModelCombo.SelectedIndex = 0;
            }

            if (rec.UseGpu) RadioGpu.IsChecked = true;
            else RadioCpu.IsChecked = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error determining hardware: {ex.Message}", "Hardware Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateHardwareDisplay()
    {
        if (_hwInfo == null) return;

        CpuText.Text = _hwInfo.CpuName;
        RamText.Text = $"{_hwInfo.RamGb:F1} GB";

        // Show VRAM
        GpuText.Text = $"{_hwInfo.TopGpuName} ({_hwInfo.VramGb:F1} GB)";

        // Update Recommendation Text (Localized)
        var rec = _hwService.GetRecommendation();
        // We need to re-generate the reason string based on current culture
        string reason;
        if (rec.UseGpu)
        {
            if (_hwInfo.VramGb >= 6) reason = string.Format(TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Rec_High", TurboSnip.WPF.Properties.Resources.Culture) ?? "{0}", _hwInfo.TopGpuName, _hwInfo.VramGb);
            else reason = string.Format(TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Rec_Balanced", TurboSnip.WPF.Properties.Resources.Culture) ?? "{0}", _hwInfo.TopGpuName, _hwInfo.VramGb);
        }
        else
        {
            reason = string.Format(TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Rec_Low", TurboSnip.WPF.Properties.Resources.Culture) ?? "{0}GB", _hwInfo.VramGb);
        }
        RecommendationText.Text = reason;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not string selectedModel)
        {
            System.Windows.MessageBox.Show(TurboSnip.WPF.Properties.Resources.ResourceManager.GetString("Label_SelectModel", TurboSnip.WPF.Properties.Resources.Culture), "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool useGpu = RadioGpu.IsChecked == true;

        // Update In-Memory Config
        _config.Llm.DefaultModelName = selectedModel;
        _config.Llm.GpuLayerCount = useGpu ? 100 : 0;
        _config.Language = SelectedLanguage.Name; // Synchronize Language!

        SaveSettings(selectedModel, useGpu);

        DialogResult = true;
        Close();
    }

    private void SaveSettings(string modelName, bool useGpu)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            JsonObject jNode = [];

            if (File.Exists(path))
            {
                try { jNode = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? []; } catch (Exception parseEx) { System.Diagnostics.Debug.WriteLine($"Failed to parse existing config, creating new: {parseEx.Message}"); }
            }

            // Ensure structure
            if (!jNode.ContainsKey("Llm")) jNode["Llm"] = new JsonObject();
            var llmNode = jNode["Llm"]!.AsObject();

            llmNode["DefaultModelName"] = modelName;
            llmNode["GpuLayerCount"] = useGpu ? 100 : 0;

            // Save Language
            jNode["Language"] = SelectedLanguage.Name; // e.g. "zh-CN"

            // Ensure IsFirstRun is persisted (as true if it was true)
            // This ensures MainViewModel sees it as true even if the file was newly created
            if (!jNode.ContainsKey("IsFirstRun"))
            {
                jNode["IsFirstRun"] = _config.IsFirstRun;
            }

            File.WriteAllText(path, jNode.ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}
