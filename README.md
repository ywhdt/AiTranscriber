# Windows AI Transcriber

Windows AI Transcriber 是一个 **Windows-first 的 .NET MAUI 转写桌面应用**。它使用 NAudio 的 WASAPI loopback 捕获电脑正在播放的系统声音，将音频重采样为 24 kHz mono PCM16，并把识别出的文字显示在主窗口和可选的字幕悬浮窗中。

> 本项目面向学习、实验和个人使用；当前工程只配置了 Windows 目标框架，虽然保留了 MAUI 模板生成的 iOS、Android、MacCatalyst 目录，但实际音频采集实现仅在 Windows 下可用。

## 主要功能

- **系统音频捕获**：抓取电脑扬声器/应用正在播放的声音，不使用麦克风输入。
- **OpenAI 转写**：实时模式通过 `wss://api.openai.com/v1/realtime?intent=transcription`，高精度字幕模式通过 `/v1/audio/transcriptions`。
- **按模式固定模型**：实时模式固定 `gpt-realtime-whisper`，高精度字幕模式固定 `gpt-4o-transcribe`。
- **语言与提示词**：当前界面可选择中文、英文、日文；支持填写专有名词提示词（**实时对话模式不支持提示词**）。
- **本地 VAD 分段**：实时模式用本地 VAD 判断声音开始和长静音提交；高精度字幕模式用 VAD 辅助寻找短窗口切点。
- **两种分段模式**：
  - `实时对话`：检测到声音后边录边发送完整语音段，长静音时提交，适合低延迟字幕。
  - `高精度字幕`：按 6-8 秒左右的可调短窗口调用 `gpt-4o-transcribe`，适合允许一到两句话延迟的高准确率字幕。
- **字幕悬浮窗**：可打开独立的 WPF 透明置顶字幕条，支持拖动、单行增量显示、满行停留和空闲清空。
- **转写保存**：停止时可自动保存，也可以手动保存；文本会保存到应用数据目录下的 `Transcripts` 文件夹。
- **事件日志**：每次会话会重置并写入 `realtime-events.log`，便于排查 Realtime 服务端事件和本地提交策略。
- **安全存储**：OpenAI API Key 使用 MAUI `SecureStorage` 保存；模型、语言、VAD 和字幕设置使用 `Preferences` 保存。

## 技术栈与运行环境

- .NET MAUI，目标框架：`net10.0-windows10.0.19041.0`
- Windows 最低支持版本：Windows 10 `10.0.17763.0`
- NAudio `2.3.0` 用于 WASAPI loopback 和 Media Foundation 重采样
- WPF 用于字幕悬浮窗
- OpenAI API Key，需要有 Realtime 转写和 Audio Transcriptions 接口可用权限

## 快速开始

### 使用 Visual Studio

1. 在 Windows 上安装 Visual Studio，并安装 .NET MAUI / Windows 桌面开发相关工作负载。
2. 打开 `WindowsAiTranscriber.sln`。
3. 选择 Windows 目标并启动应用。
4. 在主界面填写 OpenAI API Key。
5. 选择语言和可选的专有名词提示；转写模型会根据分段模式自动确定。
6. 播放电脑里的音频，然后点击“开始”。
7. 点击“停止”结束会话；如果设置里开启了自动保存，转写文本会自动保存。

### 使用命令行

```powershell
dotnet build WindowsAiTranscriber.csproj
dotnet run --project WindowsAiTranscriber.csproj -f net10.0-windows10.0.19041.0
```

> 如果命令行提示缺少 MAUI 工作负载或 Windows 目标包，请先按提示安装对应 .NET SDK / workload。

## 本地发布包

项目提供了一个 PowerShell 发布脚本，用于生成无需 MSIX 安装流程的本地文件夹发布包：

```powershell
.\tools\publish-local.ps1
```

默认输出：

- `artifacts\WindowsAiTranscriber-local\WindowsAiTranscriber.exe`
- `artifacts\WindowsAiTranscriber-local.zip`

发布脚本默认使用 `Release` 和 `win-x64`，并设置 `WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`、`--self-contained true`。把整个 `WindowsAiTranscriber-local` 文件夹复制到任意位置后，运行里面的 `WindowsAiTranscriber.exe` 即可。

如果需要双击安装、开始菜单入口和卸载入口，可以后续改为 MSIX 安装包。MSIX 必须签名；本机测试可使用自签证书，正式分发应使用受信任的代码签名方式。

## 使用说明

### 主窗口

- **OpenAI API Key**：必填。保存/启动时写入系统安全存储；以空值保存时会移除保存的 Key。
- **转写模型**：只读显示。实时模式固定 `gpt-realtime-whisper`，高精度字幕模式固定 `gpt-4o-transcribe`。
- **语言**：当前界面提供 `zh`、`en`、`ja` 三个选项，默认 `zh`。
- **专有名词提示**：填写课程名、人名、产品名、术语等，以提升识别稳定性。
- **开始/停止**：开始后按当前模式启动转写管线和系统音频采集；停止时提交剩余音频并关闭会话。
- **清空/保存**：清空当前窗口内容，或手动把已完成片段保存为 `.txt`。
- **设置**：调整分段模式、本地 VAD、自动保存和字幕悬浮窗参数。
- **打开字幕窗**：打开/关闭独立字幕条窗口。

### 设置页

可调整的参数包括：

- 自动保存转写结果
- 分段模式：实时对话 / 高精度字幕
- 高精度目标窗口、最长窗口、重叠时长
- VAD 前置音频、长静音提交时间、最低语音音量、噪声倍率
- 字幕字号、背景不透明度、满行停留时间、空闲清空时间

默认值集中在 `Models/AppSettings.cs` 中：

```csharp
public double HighPrecisionTargetWindowSeconds { get; set; } = 6.0;
public double HighPrecisionMaxWindowSeconds { get; set; } = 8.0;
public double HighPrecisionOverlapSeconds { get; set; } = 1.2;
public double VadPreRollMilliseconds { get; set; } = 300;
public double VadSilenceCommitMilliseconds { get; set; } = 1200;
public double VadMinimumSpeechRms { get; set; } = 0.012;
public double VadNoiseMultiplier { get; set; } = 3.0;
public double SubtitleBackgroundOpacity { get; set; } = 0.72;
public double SubtitleFontSize { get; set; } = 34;
public double SubtitleLineHoldSeconds { get; set; } = 1.0;
public double SubtitleIdleClearSeconds { get; set; } = 3.0;
```

调参建议：

- 高精度目标窗口越小，字幕更新越快；最长窗口越大，连续对白上下文越完整。
- 高精度重叠时长可减少切断句首句尾的概率，但可能带来少量重复文本。
- `VadMinimumSpeechRms` 越小越容易触发语音，越大越能抑制背景声误触发。
- `VadNoiseMultiplier` 越大，对动态噪声越保守。
- 高精度字幕模式如果延迟太高，可降低目标窗口或最长窗口；如果断句过碎，可提高目标窗口或重叠时长。

## 数据位置

应用使用 MAUI 的 `FileSystem.AppDataDirectory` 保存运行数据：

- `Transcripts/transcript-yyyyMMdd-HHmmss.txt`：转写文本，每段带本地时间戳。
- `realtime-events.log`：OpenAI Realtime 服务端事件和本地音频提交日志。

具体目录由 Windows/MAUI 运行时决定，可在应用界面底部看到保存后的完整路径或事件日志路径。

## 项目结构

```text
.
├── MainPage.xaml / MainPage.xaml.cs                  # 主界面、会话控制、音频/VAD 管线
├── SettingsPage.xaml / SettingsPage.xaml.cs          # VAD、分段、字幕和自动保存设置
├── SubtitleOverlayPage.xaml / SubtitleOverlayPage.xaml.cs
├── Models/
│   ├── AppSettings.cs                                # 配置模型和默认值
│   └── TranscriptSegment.cs                          # 转写片段模型
├── Services/
│   ├── AppSettingsService.cs                         # SecureStorage / Preferences 持久化
│   ├── LocalVoiceActivityDetector.cs                 # 实时模式本地 RMS/VAD 分段
│   ├── HighPrecisionAudioSegmenter.cs                # 高精度字幕短窗口切分
│   ├── OpenAIRealtimeTranscriptionService.cs         # Realtime WebSocket、事件解析、日志
│   ├── OpenAIAudioTranscriptionService.cs            # Audio Transcriptions HTTP 调用
│   ├── SubtitleOverlayService.cs                     # WPF 字幕悬浮窗
│   ├── TranscriptStore.cs                            # 文本保存
│   └── TranscriptionTextCleaner.cs                   # 文本清理和乱码修复
├── Platforms/Windows/Audio/
│   ├── WindowsSystemAudioCaptureService.cs           # WASAPI loopback 系统音频捕获
│   └── AudioResampler.cs                             # 24 kHz mono PCM16 重采样
├── WindowsAiTranscriber.csproj                       # .NET MAUI Windows 项目
└── tools/publish-local.ps1                           # 本地发布脚本
```

## 已知限制

- 当前只实现了 Windows 系统音频捕获；非 Windows 平台目录来自 MAUI 模板，并未接入对应音频采集实现。
- 只能捕获系统混音输出，不能按应用单独选择音源，也不会录入麦克风。
- OpenAI 转写依赖网络和 API 可用性；模型名称和接口权限以账号实际可用情况为准。
- 转写结果为纯文本，尚未导出 `.srt` / `.vtt` 字幕文件。

## 后续可扩展方向

- 导出 `.srt` / `.vtt` 字幕文件。
- 历史记录页面和转写搜索。
- 全局热键开始/停止。
- 按应用或音频设备选择音源。
- 麦克风与系统声音混音。
