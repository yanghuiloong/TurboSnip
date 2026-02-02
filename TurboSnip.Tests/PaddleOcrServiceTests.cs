using Xunit;
using TurboSnip.WPF.Services;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR;
using System.Collections.Generic;
using OpenCvSharp;

namespace TurboSnip.Tests;

public class PaddleOcrServiceTests
{
    private PaddleOcrResultRegion CreateBlock(string text, float top, float height)
    {
        // Construct a RotatedRect that yields the desired BoundingRect properties.
        // Top = CenterY - Height/2
        // So CenterY = Top + Height/2

        float centerY = top + (height / 2);
        var rect = new RotatedRect(new Point2f(100, centerY), new Size2f(200, height), 0);

        // PaddleOcrResultRegion constructor might vary. 
        // Based on library patterns: (Rect, Text, Score)
        return new PaddleOcrResultRegion(rect, text, 0.9f);
    }

    [Fact]
    public void ProcessBlocks_DetectsParagraphs()
    {
        // Arrange
        // Block 1: Top=0, Height=20 -> Bottom=20
        // Block 2: Top=50, Height=20 (Gap = 30, which is > 1.2*20 = 24) -> Should split
        var blocks = new List<PaddleOcrResultRegion>
        {
            CreateBlock("First Para.", 0, 20),
            CreateBlock("Second Para.", 50, 20)
        };

        // Act
        string result = PaddleOcrService.ProcessBlocks(blocks, new AppConfig());

        // Assert
        // Expect double newline
        Assert.Matches(@"First Para\.\r?\n\r?\nSecond Para\.", result);
    }

    [Fact]
    public void ProcessBlocks_MergesLinesInParagraph()
    {
        // Arrange
        // Block 1: Top=0, Height=20 -> Bottom=20
        // Block 2: Top=25, Height=20 (Gap = 5, < 24) -> Should NOT split
        var blocks = new List<PaddleOcrResultRegion>
        {
            CreateBlock("Line One", 0, 20),
            CreateBlock("Line Two", 25, 20)
        };

        // Act
        string result = PaddleOcrService.ProcessBlocks(blocks, new AppConfig());

        // Assert
        // Expect NO double newline, just concatenated (PaddleOcrService usually just appends text?)
        // Wait, ProcessBlocks logic: 
        // else { sb.AppendLine(); } if not para/list/hyphen
        // So it appends a newline for standard lines too? 
        // Let's check logic:
        // if (para) { append \n\n }
        // else if (list) { ensure \n }
        // else if (hyphen) { merge }
        // else { append \n } 

        // So standard lines ARE separated by newline in output string, but not Double Newline.
        // My Translate Prompt "Restore Sequence" / "Format Preservation" says "Maintain original paragraph structure".
        // But the input to LLM has newlines.

        Assert.Contains("Line One\r\nLine Two", result.Replace("\n", "\r\n").Replace("\r\r", "\r"));
    }

    [Fact]
    public void ProcessBlocks_DetectsListItems()
    {
        // Arrange
        var blocks = new List<PaddleOcrResultRegion>
        {
            CreateBlock("Intro:", 0, 20),
            CreateBlock("1. First Item", 25, 20) // Small gap, but is list
        };

        // Act
        string result = PaddleOcrService.ProcessBlocks(blocks, new AppConfig());

        // Assert
        // Logic B: if ListRegex matches, ensure newline.
        Assert.Contains("Intro:\r\n1. First Item", result.Replace("\n", "\r\n").Replace("\r\r", "\r"));
    }

    [Fact]
    public void ProcessBlocks_FixesHyphenation()
    {
        // Arrange
        var blocks = new List<PaddleOcrResultRegion>
        {
            CreateBlock("This is a pro-", 0, 20),
            CreateBlock("cess.", 25, 20)
        };

        // Act
        string result = PaddleOcrService.ProcessBlocks(blocks, new AppConfig());

        // Assert
        // Should merge "pro-" and "cess." -> "process."
        Assert.Contains("This is a process.", result);
    }
}
