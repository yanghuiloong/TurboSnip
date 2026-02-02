namespace TurboSnip.WPF.Services;

public class AppConfig
{
    public bool IsFirstRun { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    public LlmConfig Llm { get; set; } = new();
    public OcrConfig Ocr { get; set; } = new();
}

public class LlmConfig
{
    public string ModelPath { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public int ContextSize { get; set; } = 8192;
    public int BatchSize { get; set; } = 1024;
    public float Temperature { get; set; } = 0.1f;
    public float RepeatPenalty { get; set; } = 1.2f;
    public float TopP { get; set; } = 0.9f;
    public int MaxTokens { get; set; } = 2048;
    public string[] AntiPrompts { get; set; } = ["<|im_end|>"];
    public string DefaultModelName { get; set; } = "qwen2.5-3b-instruct-q4_k_m.gguf";
    public int GpuLayerCount { get; set; } = 100;
    public int UnloadTimeoutMinutes { get; set; } = 5;
}

public class OcrConfig
{
    public float ScoreThreshold { get; set; } = 0.6f;
    public double UpscaleFactor { get; set; } = 3.0;
    public bool EnableDarkThemeSupport { get; set; } = true;
    public float ParagraphGapMultiplier { get; set; } = 1.2f;
}
