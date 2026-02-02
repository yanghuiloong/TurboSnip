using Xunit;
using TurboSnip.WPF.Services;

namespace TurboSnip.Tests;

public class LlamaServiceTests
{
    [Theory]
    [InlineData("prefer.All", "prefer. All")]
    [InlineData("sentence.New", "sentence. New")]
    [InlineData("data,But", "data, But")]
    [InlineData("end)Start", "end) Start")]
    public void PreprocessOcrText_FixesPunctuationSpacing(string input, string expected)
    {
        var result = LlamaService.PreprocessOcrText(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PreprocessOcrText_FixesHyphenatedWords()
    {
        // "pro- \ncess" -> "process"
        string input = "pro-\ncess";
        string result = LlamaService.PreprocessOcrText(input);
        Assert.Equal("process", result);
    }

    [Fact]
    public void PreprocessOcrText_FixesBulletSpacing()
    {
        string input = "•Item";
        string result = LlamaService.PreprocessOcrText(input);
        Assert.Equal("• Item", result);
    }

    [Fact]
    public void PreprocessOcrText_CompressesWhitespace()
    {
        string input = "Too   many    spaces";
        string result = LlamaService.PreprocessOcrText(input);
        Assert.Equal("Too many spaces", result);
    }

    [Fact]
    public void BuildPrompt_FormatsCorrectly()
    {
        string input = "Test Input";
        // Create Config with specific values to verify they are used
        var config = new AppConfig();
        config.Llm.SystemPrompt = "TEST SYSTEM PROMPT";

        // Instantiate Service
        var service = new LlamaService(config);

        string prompt = service.BuildPrompt(input);

        Assert.Contains("<|im_start|>system", prompt);
        Assert.Contains("TEST SYSTEM PROMPT", prompt);
        Assert.Contains("<|im_start|>user", prompt);
        Assert.Contains(input, prompt);
        Assert.EndsWith("<|im_start|>assistant\n", prompt);
    }
}
