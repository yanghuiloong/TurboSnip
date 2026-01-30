# TurboSnip - AI Screenshot Translator

**TurboSnip** is a high-performance, privacy-focused Windows desktop application that allows you to snip any part of your screen, perform instant OCR (Optical Character Recognition), and translate the text using a local Large Language Model (LLM).

## Key Features

*   **Privacy First**: All processing (OCR & Translation) happens 100% offline on your device. No data leaves your computer.
*   **High Performance**:
    *   **PaddleOCR**: Accurate Chinese/English text recognition.
    *   **Local LLM (Qwen 2.5)**: High-quality translation powered by `LLamaSharp` (GPU/CPU).
    *   **Streaming**: Real-time translation feedback as tokens are generated.
*   **Smart Resource Management**:
    *   **Auto-Unload**: Frees up RAM/VRAM after 5 minutes (default) of inactivity.
    *   **Cold Start**: Automatically reloads models when you need them.
*   **Modern UX**:
    *   **Snippet Tool**: Built-in region selector with magnifying glass overlay.
    *   **Hotkeys**: Global `Alt+Q` to capture.
    *   **Localization**: Fully distinct English and Simplified Chinese (简体中文) interfaces.

## Requirements

*   **OS**: Windows 10/11 (64-bit)
*   **Runtime**: .NET 8.0 Desktop Runtime
*   **Hardware**:
    *   **CPU**: Modern multi-core processor (AVX2 supported).
    *   **GPU (Optional but Recommended)**: NVIDIA GPU with CUDA 12 support.
        *   *Requirements*: 4GB+ VRAM recommended.
        *   *Driver*: Latest NVIDIA driver (530.xx or higher) is required for CUDA 12.
*   **Models**:
    *   Place `.gguf` models in local `models/llm/` directory.
    *   Ensure `inference` directory contains PaddleOCR models.

## Configuration

Settings can be customized in `appsettings.json`:

```json
{
  "Llm": {
    "ModelPath": "models/llm/",
    "DefaultModelName": "qwen2.5-3b-instruct-q4_k_m.gguf",
    "ContextSize": 8192,
    "GpuLayerCount": 100, // Set to 0 to force CPU
    "UnloadTimeoutMinutes": 5 // Set to 0 to disable auto-unload
  },
  "Ocr": {
    "ScoreThreshold": 0.6,
    "UpscaleFactor": 3.0,
    "EnableDarkThemeSupport": true
  }
}
```

## Build Instructions

1.  **Prerequisites**: Install .NET 8 SDK.
2.  **Clone**: Clone this repository.
3.  **Build**:
    ```powershell
    dotnet build -c Release
    ```
4.  **Run**:
    ```powershell
    dotnet run --project TurboSnip.WPF
    ```

## Project Structure

*   `TurboSnip.WPF`: Main WPF Application (MVVM architecture).
*   `TurboSnip.Tests`: xUnit test suite.
*   `models/`: Directory for LLM (`.gguf`) and OCR models.
*   `logs/`: Application logs (errors and exceptions).

## License

MIT License.
