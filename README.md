# Windows AI Transcriber

一个 Windows-first 的 .NET MAUI 实时转写工具。当前版本会抓取电脑正在播放的系统音频，通过 OpenAI Realtime 转写接口把声音实时变成文字，并可在停止后保存文本记录。

## 当前功能

- 抓取 Windows 系统音频，不依赖麦克风。
- 默认使用 `gpt-realtime-whisper` 做实时转写。
- 可切换到 `gpt-4o-transcribe`。
- 支持填写语言代码，例如 `zh`、`en`，留空则交给模型自动判断。
- 支持填写专有名词提示，提升课程名、人名、产品名等识别效果。
- API Key 使用系统安全存储保存，其余设置使用本地偏好设置保存。
- 转写结果保存到应用数据目录的 `Transcripts` 文件夹。
- 设置页可以直接调整分段模式、本地 VAD 参数、自动保存、字幕字号、字幕背景不透明度、满行停留时间和空闲清空时间。
- 可打开独立字幕条窗口。字幕条会按单行显示增量文字，满一行后停留一段时间再清空进入下一行。
- 对于实时要求不高的情景，推荐使用 `gpt-4o-transcribe` 准确度更高

## 运行方式

1. 用 Visual Studio 打开 `WindowsAiTranscriber.sln`。
2. 选择 Windows 目标运行。
3. 填入 OpenAI API Key。
4. 播放电脑里的音频，然后点击“开始”。
5. 点击“停止”后，如果设置里开启了自动保存，转写文本会自动保存。

也可以在命令行运行：

```powershell
dotnet build
dotnet run -f net10.0-windows10.0.19041.0
```

## 生成本地发布包

在项目根目录运行：

```powershell
.\tools\publish-local.ps1
```

脚本会生成：

- `artifacts\WindowsAiTranscriber-local\WindowsAiTranscriber.exe`
- `artifacts\WindowsAiTranscriber-local.zip`

这个发布包不需要安装证书。把整个 `WindowsAiTranscriber-local` 文件夹放到任意位置，然后运行里面的 `WindowsAiTranscriber.exe` 即可。

## 调整本地 VAD

默认本地 VAD 会先判断是否有声音，静音时不提交音频；检测到声音后最多每 2 秒切一段。影视字幕模式下，本地 VAD 会先缓存音频，达到固定提交间隔后发送；如果超过提前提交比例后检测到静音，也会提前发送。现在可以直接在软件的“设置”界面修改这些参数。

如果要改默认值，修改 `Models\AppSettings.cs` 里的这些属性：

```csharp
public double MaxSegmentSeconds { get; set; } = 2.0;
public double FixedSegmentSeconds { get; set; } = 6.0;
public double CinemaEarlyCommitPercent { get; set; } = 80.0;
public double VadPreRollMilliseconds { get; set; } = 300;
public double VadSilenceCommitMilliseconds { get; set; } = 650;
public double VadMinimumSpeechRms { get; set; } = 0.012;
public double VadNoiseMultiplier { get; set; } = 3.0;
public double SubtitleBackgroundOpacity { get; set; } = 0.72;
public double SubtitleFontSize { get; set; } = 34;
public double SubtitleLineHoldSeconds { get; set; } = 1.0;
public double SubtitleIdleClearSeconds { get; set; } = 3.0;
```

`MaxSegmentSeconds` 或 `FixedSegmentSeconds` 越小，字幕更新越快，但上下文更少；`VadMinimumSpeechRms` 越小越敏感，越大越不容易误触发。

如果需要双击安装、开始菜单入口和卸载入口，下一步可以改成 MSIX 安装包。MSIX 必须签名；自己电脑测试可以用自签证书，正式分发则应使用受信任的代码签名方式。

## 项目结构

- `MainPage.xaml` / `MainPage.xaml.cs`：主界面和按钮逻辑。
- `Services/OpenAIRealtimeTranscriptionService.cs`：连接 OpenAI Realtime API。
- `Platforms/Windows/Audio/WindowsSystemAudioCaptureService.cs`：抓取电脑系统音频。
- `Platforms/Windows/Audio/AudioResampler.cs`：把系统音频转换为 24kHz mono PCM16。
- `Services/AppSettingsService.cs`：保存 API Key、模型、语言和提示词。
- `Services/TranscriptStore.cs`：保存转写文本。

## 后续可加功能

- 导出 `.srt` 字幕文件。
- 历史记录页面。
- 热键开始/停止。
- 按应用选择音频来源。
