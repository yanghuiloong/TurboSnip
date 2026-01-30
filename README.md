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

## About install_script.iss

### 📦 构建安装包 (Building the Installer)

本项目包含用于生成 Windows 安装程序 (`Setup.exe`) 的 Inno Setup 脚本。如果你想自己构建安装包，请遵循以下步骤：

### 1. 准备工作
* 下载并安装 [Inno Setup](https://jrsoftware.org/isdl.php) (建议版本 6.x+)。
* 确保你已经生成了 Release 版本的构建产物（即 `TurboSnip_v1.0` 文件夹已准备好）。

### 2. 修改脚本
项目根目录下的 `install_script.iss` 是构建脚本。**注意：该脚本中包含本地绝对路径，直接运行可能会报错。**

1. 使用 Inno Setup 打开 `install_script.iss`。
2. 查找 `[Files]` 段落，找到类似 `Source: "D:\Your\Path\To\TurboSnip_v1.0\..."` 的代码。
3. 将路径修改为你本地实际的 `TurboSnip_v1.0` 文件夹路径。
   - *或者，如果你熟悉 Inno Setup，可以使用 `{#SourcePath}` 相对路径变量来优化它。*

### 3. 编译
* 点击 Inno Setup 工具栏上的 **Compile (编译)** 按钮。
* 脚本配置了分卷压缩 (`DiskSpanning=yes`)，编译完成后，你将在输出目录（默认为桌面或脚本同级目录）看到以下文件：
  * `TurboSnip_Setup.exe` (启动器)
  * `TurboSnip_Setup-1.bin` (数据包)
  * ...

> **注意**：为了符合 GitHub Release 单文件 2GB 的限制，我们将安装包拆分成了多个 `.bin` 文件。安装时请确保所有文件在同一目录下。



## License

MIT License.
