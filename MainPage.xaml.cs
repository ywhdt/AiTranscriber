using System.Text;
using System.Threading.Channels;
using WindowsAiTranscriber.Models;
using WindowsAiTranscriber.Services;

namespace WindowsAiTranscriber;

public partial class MainPage : ContentPage
{
	private static readonly List<LanguageOption> LanguageOptions =
	[
		new("中文", "zh"),
		new("英文", "en"),
		new("日语", "ja")
	];

	private const int AudioSampleRate = 24000;
	private const int MinimumCommitBytes = AudioSampleRate * 2 / 10;

	private readonly AppSettingsService _settingsService;
	private readonly TranscriptStore _transcriptStore;
	private readonly OpenAIRealtimeTranscriptionService _realtimeTranscriptionService;
	private readonly OpenAIAudioTranscriptionService _audioTranscriptionService;
	private readonly IAudioCaptureService _audioCaptureService;
	private readonly SubtitleOverlayService _subtitleOverlayService;
	private readonly SemaphoreSlim _audioPipelineLock = new(1, 1);
	private readonly List<TranscriptSegment> _segments = [];
	private readonly StringBuilder _committedText = new();
	private AppSettings _settings = new();
	private AppSettings? _sessionSettings;
	private LocalVoiceActivityDetector _realtimeVoiceActivityDetector = CreateRealtimeVoiceActivityDetector(new AppSettings());
	private HighPrecisionAudioSegmenter _highPrecisionSegmenter = CreateHighPrecisionAudioSegmenter(new AppSettings());
	private Channel<HighPrecisionAudioSegment>? _highPrecisionChannel;
	private Task? _highPrecisionWorkerTask;
	private CancellationTokenSource? _sessionCts;
	private int _hasUncommittedRealtimeAudio;
	private int _highPrecisionSegmentSequence;
	private string _partialText = "";
	private string _lastHighPrecisionText = "";
	private bool _subtitleReceivedDeltaForCurrentSegment;

	public MainPage(
		AppSettingsService settingsService,
		TranscriptStore transcriptStore,
		OpenAIRealtimeTranscriptionService realtimeTranscriptionService,
		OpenAIAudioTranscriptionService audioTranscriptionService,
		IAudioCaptureService audioCaptureService,
		SubtitleOverlayService subtitleOverlayService)
	{
		InitializeComponent();
		_settingsService = settingsService;
		_transcriptStore = transcriptStore;
		_realtimeTranscriptionService = realtimeTranscriptionService;
		_audioTranscriptionService = audioTranscriptionService;
		_audioCaptureService = audioCaptureService;
		_subtitleOverlayService = subtitleOverlayService;

		LanguagePicker.ItemsSource = LanguageOptions;
		LanguagePicker.SelectedItem = LanguageOptions[0];

		_realtimeTranscriptionService.StatusChanged += OnTranscriptionStatusChanged;
		_realtimeTranscriptionService.ErrorOccurred += OnTranscriptionError;
		_realtimeTranscriptionService.PartialTranscriptReceived += OnPartialTranscriptReceived;
		_realtimeTranscriptionService.TranscriptCompleted += OnTranscriptCompleted;
		_audioCaptureService.CaptureError += OnAudioCaptureError;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		_settings = await _settingsService.LoadAsync();
		_settings.Model = ModelForMode(_settings);
		ApiKeyEntry.Text = _settings.ApiKey;
		LanguagePicker.SelectedItem = FindLanguageOption(_settings.Language);
		PromptEditor.Text = _settings.Prompt;
		UpdateModelDisplay();
		_subtitleOverlayService.ApplySettings(_settings);
		UpdateSubtitleWindowButton();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_ = StopSessionAsync(saveTranscript: true);
	}

	private async void OnStartClicked(object? sender, EventArgs e)
	{
		await StartSessionAsync();
	}

	private async void OnStopClicked(object? sender, EventArgs e)
	{
		await StopSessionAsync(saveTranscript: true);
	}

	private void OnClearClicked(object? sender, EventArgs e)
	{
		_segments.Clear();
		_committedText.Clear();
		_partialText = "";
		_lastHighPrecisionText = "";
		_subtitleReceivedDeltaForCurrentSegment = false;
		TranscriptEditor.Text = "";
		PartialLabel.Text = "当前片段：";
		OutputPathLabel.Text = "";
		_subtitleOverlayService.Clear();
	}

	private async void OnSaveClicked(object? sender, EventArgs e)
	{
		await SaveTranscriptAsync();
	}

	private async void OnSettingsClicked(object? sender, EventArgs e)
	{
		var settingsPage = new SettingsPage(_settingsService, _subtitleOverlayService);
		settingsPage.SettingsSaved += (_, settings) =>
		{
			settings.Model = ModelForMode(settings);
			_subtitleOverlayService.ApplySettings(settings);
			UpdateSettingsDuringOrBetweenSessions(settings);
		};

		await Navigation.PushModalAsync(settingsPage);
	}

	private async void OnSubtitleWindowClicked(object? sender, EventArgs e)
	{
		await UpdateSettingsFromUiAsync();

		if (_subtitleOverlayService.IsOpen)
		{
			_subtitleOverlayService.Close();
		}
		else
		{
			_subtitleOverlayService.Clear();
			_subtitleOverlayService.Open(_settings);
		}

		UpdateSubtitleWindowButton();
	}

	private async Task StartSessionAsync()
	{
		if (_sessionCts is not null)
		{
			return;
		}

		await UpdateSettingsFromUiAsync();
		if (string.IsNullOrWhiteSpace(_settings.ApiKey))
		{
			await DisplayAlertAsync("缺少 API Key", "请先填写 OpenAI API Key。", "确定");
			return;
		}

		try
		{
			_settings.Model = ModelForMode(_settings);
			await _settingsService.SaveAsync(_settings);

			_sessionCts = new CancellationTokenSource();
			_sessionSettings = CopySettings(_settings);
			_partialText = "";
			_lastHighPrecisionText = "";
			_subtitleReceivedDeltaForCurrentSegment = false;
			SetRunningState(isRunning: true);
			UpdateModelDisplay();

			if (_sessionSettings.UsesHighPrecisionSubtitleMode)
			{
				await StartHighPrecisionSessionAsync(_sessionSettings, _sessionCts.Token);
			}
			else
			{
				await StartRealtimeSessionAsync(_sessionSettings, _sessionCts.Token);
			}
		}
		catch (Exception ex)
		{
			await StopSessionAsync(saveTranscript: false);
			await DisplayAlertAsync("启动失败", ex.Message, "确定");
			SetStatus("启动失败。请检查 API Key、网络和系统音频设备。");
		}
	}

	private async Task StartRealtimeSessionAsync(AppSettings settings, CancellationToken cancellationToken)
	{
		SetStatus("正在连接 OpenAI 实时转写服务...");
		await _realtimeTranscriptionService.StartAsync(settings, cancellationToken);
		LogSessionStrategy(settings);

		_realtimeVoiceActivityDetector = CreateRealtimeVoiceActivityDetector(settings);
		_realtimeVoiceActivityDetector.Reset();
		Interlocked.Exchange(ref _hasUncommittedRealtimeAudio, 0);
		_audioCaptureService.AudioAvailable += OnAudioAvailable;
		_audioCaptureService.Start();
		SetStatus(BuildListeningStatus(settings));
		OutputPathLabel.Text = $"事件日志：{_realtimeTranscriptionService.EventLogPath}";
	}

	private Task StartHighPrecisionSessionAsync(AppSettings settings, CancellationToken cancellationToken)
	{
		SetStatus("正在启动高精度字幕模式...");
		LogSessionStrategy(settings);

		_highPrecisionSegmenter = CreateHighPrecisionAudioSegmenter(settings);
		_highPrecisionSegmenter.Reset();
		_highPrecisionSegmentSequence = 0;
		_highPrecisionChannel = Channel.CreateUnbounded<HighPrecisionAudioSegment>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});
		_highPrecisionWorkerTask = Task.Run(
			() => ProcessHighPrecisionQueueAsync(_highPrecisionChannel.Reader, cancellationToken),
			CancellationToken.None);

		_audioCaptureService.AudioAvailable += OnAudioAvailable;
		_audioCaptureService.Start();
		PartialLabel.Text = "高精度片段：等待首段结果";
		SetStatus(BuildListeningStatus(settings));
		OutputPathLabel.Text = "高精度字幕模式：音频块将通过 /v1/audio/transcriptions 转写。";
		return Task.CompletedTask;
	}

	private async Task StopSessionAsync(bool saveTranscript)
	{
		if (_sessionCts is null)
		{
			return;
		}

		var cts = _sessionCts;
		var sessionSettings = _sessionSettings ?? _settings;
		_sessionCts = null;

		_audioCaptureService.AudioAvailable -= OnAudioAvailable;
		_audioCaptureService.Stop();

		if (sessionSettings.UsesHighPrecisionSubtitleMode)
		{
			await FlushPendingHighPrecisionAudioAsync("stop", CancellationToken.None);
			_highPrecisionChannel?.Writer.TryComplete();
			if (_highPrecisionWorkerTask is not null)
			{
				try
				{
					await _highPrecisionWorkerTask;
				}
				catch
				{
				}
			}

			_highPrecisionSegmenter.Reset();
			_highPrecisionChannel = null;
			_highPrecisionWorkerTask = null;
		}
		else
		{
			await CommitPendingRealtimeAudioAsync(CancellationToken.None);
			_realtimeVoiceActivityDetector.Reset();
			await _realtimeTranscriptionService.StopAsync();
		}

		cts.Cancel();
		cts.Dispose();
		_sessionSettings = null;

		if (saveTranscript && _settings.AutoSaveTranscriptOnStop)
		{
			await SaveTranscriptAsync();
		}

		SetRunningState(isRunning: false);
		UpdateModelDisplay();
		SetStatus("已停止。");
	}

	private async Task SaveTranscriptAsync()
	{
		if (_segments.Count == 0)
		{
			OutputPathLabel.Text = "还没有可保存的转写内容。";
			return;
		}

		var path = await _transcriptStore.SaveAsync(_segments);
		OutputPathLabel.Text = $"已保存：{path}";
	}

	private async Task UpdateSettingsFromUiAsync()
	{
		_settings = await _settingsService.LoadAsync();
		_settings.ApiKey = ApiKeyEntry.Text?.Trim() ?? "";
		_settings.Model = ModelForMode(_settings);
		_settings.Language = (LanguagePicker.SelectedItem as LanguageOption)?.Code ?? LanguageOptions[0].Code;
		_settings.Prompt = PromptEditor.Text?.Trim() ?? "";
		UpdateModelDisplay();
	}

	private void UpdateSettingsDuringOrBetweenSessions(AppSettings settings)
	{
		if (_sessionSettings is null)
		{
			_settings = settings;
			UpdateModelDisplay();
			return;
		}

		if (!string.Equals(
			_sessionSettings.SegmentationMode,
			settings.SegmentationMode,
			StringComparison.OrdinalIgnoreCase))
		{
			_settings = settings;
			UpdateModelDisplay();
			SetStatus("分段模式将在下次开始时生效。当前会话继续使用启动时的模式。");
			return;
		}

		_settings = settings;
		_sessionSettings = CopySettings(settings);
		UpdateModelDisplay();
		_ = ApplyRuntimeSettingsAsync(_sessionSettings);
	}

	private void OnAudioAvailable(object? sender, AudioChunkEventArgs e)
	{
		var cts = _sessionCts;
		if (cts is null)
		{
			return;
		}

		_ = SendAudioSafelyAsync(e.Pcm16Audio, cts.Token);
	}

	private async Task SendAudioSafelyAsync(byte[] pcm16Audio, CancellationToken cancellationToken)
	{
		var lockTaken = false;
		try
		{
			await _audioPipelineLock.WaitAsync(cancellationToken);
			lockTaken = true;

			var settings = _sessionSettings ?? _settings;
			if (settings.UsesHighPrecisionSubtitleMode)
			{
				ProcessHighPrecisionAudio(pcm16Audio);
				return;
			}

			await ProcessRealtimeAudioAsync(pcm16Audio, cancellationToken);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SetStatus($"处理音频失败：{ex.Message}");
		}
		finally
		{
			if (lockTaken)
			{
				_audioPipelineLock.Release();
			}
		}
	}

	private async Task ProcessRealtimeAudioAsync(byte[] pcm16Audio, CancellationToken cancellationToken)
	{
		var vadResult = _realtimeVoiceActivityDetector.Process(pcm16Audio);
		UpdateRealtimeVadStatus(vadResult);

		if (vadResult.AudioToSend.Length == 0)
		{
			return;
		}

		await _realtimeTranscriptionService.SendAudioAsync(vadResult.AudioToSend, cancellationToken);
		Interlocked.Exchange(ref _hasUncommittedRealtimeAudio, 1);

		if (vadResult.ShouldCommit)
		{
			await CommitPendingRealtimeAudioCoreAsync(cancellationToken);
		}
	}

	private void ProcessHighPrecisionAudio(byte[] pcm16Audio)
	{
		var result = _highPrecisionSegmenter.Process(pcm16Audio);
		UpdateHighPrecisionStatus(result);
		if (result.AudioToSubmit.Length > 0)
		{
			EnqueueHighPrecisionSegment(result);
		}
	}

	private async Task FlushPendingHighPrecisionAudioAsync(string reason, CancellationToken cancellationToken)
	{
		var lockTaken = false;
		try
		{
			await _audioPipelineLock.WaitAsync(cancellationToken);
			lockTaken = true;
			var result = _highPrecisionSegmenter.Flush(reason);
			if (result.AudioToSubmit.Length > 0)
			{
				EnqueueHighPrecisionSegment(result);
			}
		}
		finally
		{
			if (lockTaken)
			{
				_audioPipelineLock.Release();
			}
		}
	}

	private void EnqueueHighPrecisionSegment(HighPrecisionSegmenterResult result)
	{
		if (result.AudioToSubmit.Length < MinimumCommitBytes)
		{
			return;
		}

		var channel = _highPrecisionChannel;
		var settings = CopySettings(_sessionSettings ?? _settings);
		var segment = new HighPrecisionAudioSegment(
			Interlocked.Increment(ref _highPrecisionSegmentSequence),
			result.AudioToSubmit,
			result.Reason,
			result.Duration,
			settings);

		if (channel?.Writer.TryWrite(segment) == true)
		{
			SetStatus($"高精度字幕已排队 {segment.Duration.TotalSeconds:0.0} 秒音频。");
		}
	}

	private async Task ProcessHighPrecisionQueueAsync(
		ChannelReader<HighPrecisionAudioSegment> reader,
		CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var segment in reader.ReadAllAsync(cancellationToken))
			{
				try
				{
					SetStatus($"高精度字幕正在转写第 {segment.Sequence} 段（{segment.Duration.TotalSeconds:0.0} 秒）。");
					var text = await _audioTranscriptionService.TranscribeAsync(
						segment.Audio,
						segment.Settings,
						cancellationToken);
					text = TrimHighPrecisionOverlap(text);
					if (string.IsNullOrWhiteSpace(text))
					{
						continue;
					}

					AddCompletedTranscript(new TranscriptSegment
					{
						StartedAt = DateTimeOffset.Now,
						Text = text
					}, appendToSubtitle: true);
					_lastHighPrecisionText = text;
					SetStatus($"高精度字幕第 {segment.Sequence} 段完成。");
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					SetStatus($"高精度字幕第 {segment.Sequence} 段失败：{ex.Message}");
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task CommitPendingRealtimeAudioAsync(CancellationToken cancellationToken)
	{
		var lockTaken = false;
		try
		{
			await _audioPipelineLock.WaitAsync(cancellationToken);
			lockTaken = true;
			await CommitPendingRealtimeAudioCoreAsync(cancellationToken);
		}
		finally
		{
			if (lockTaken)
			{
				_audioPipelineLock.Release();
			}
		}
	}

	private async Task CommitPendingRealtimeAudioCoreAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _hasUncommittedRealtimeAudio, 0) == 0)
		{
			return;
		}

		try
		{
			await _realtimeTranscriptionService.CommitAudioAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			SetStatus($"提交音频失败：{ex.Message}");
		}
	}

	private void UpdateRealtimeVadStatus(LocalVadResult vadResult)
	{
		switch (vadResult.State)
		{
			case LocalVadState.SpeechStarted:
				SetStatus("本地 VAD 检测到声音，开始发送完整语音段。");
				break;
			case LocalVadState.Commit:
				SetStatus($"检测到长静音，已提交完整语音段。音量 {vadResult.Rms:0.000} / 阈值 {vadResult.Threshold:0.000}");
				break;
		}
	}

	private void UpdateHighPrecisionStatus(HighPrecisionSegmenterResult result)
	{
		switch (result.State)
		{
			case HighPrecisionSegmenterState.SpeechStarted:
				SetStatus("高精度字幕检测到声音，开始缓存音频窗口。");
				break;
			case HighPrecisionSegmenterState.Commit:
				SetStatus($"高精度字幕窗口已切分：{result.Reason}，{result.Duration.TotalSeconds:0.0} 秒。");
				break;
		}
	}

	private void OnPartialTranscriptReceived(object? sender, string delta)
	{
		if (string.IsNullOrEmpty(delta))
		{
			return;
		}

		_partialText += delta;
		_subtitleReceivedDeltaForCurrentSegment = true;
		_subtitleOverlayService.AppendText(delta);
		MainThread.BeginInvokeOnMainThread(() =>
		{
			PartialLabel.Text = $"当前片段：{_partialText}";
			RefreshTranscriptText();
		});
	}

	private void OnTranscriptCompleted(object? sender, TranscriptSegment segment)
	{
		if (string.IsNullOrWhiteSpace(segment.Text))
		{
			return;
		}

		AddCompletedTranscript(segment, appendToSubtitle: !_subtitleReceivedDeltaForCurrentSegment);
		_partialText = "";
		_subtitleReceivedDeltaForCurrentSegment = false;

		MainThread.BeginInvokeOnMainThread(() =>
		{
			PartialLabel.Text = "当前片段：";
			RefreshTranscriptText();
		});
	}

	private void AddCompletedTranscript(TranscriptSegment segment, bool appendToSubtitle)
	{
		_segments.Add(segment);
		var text = segment.Text.Trim();
		if (appendToSubtitle)
		{
			_subtitleOverlayService.AppendText(text);
		}

		_committedText.Append('[')
			.Append(segment.StartedAt.ToLocalTime().ToString("HH:mm:ss"))
			.Append("] ")
			.AppendLine(text);

		MainThread.BeginInvokeOnMainThread(() =>
		{
			_partialText = "";
			PartialLabel.Text = "当前片段：";
			RefreshTranscriptText();
		});
	}

	private void OnTranscriptionStatusChanged(object? sender, string status)
	{
		SetStatus(status);
	}

	private void OnTranscriptionError(object? sender, string message)
	{
		SetStatus($"转写错误：{message}");
	}

	private void OnAudioCaptureError(object? sender, string message)
	{
		SetStatus($"音频采集错误：{message}");
	}

	private void RefreshTranscriptText()
	{
		var visibleText = _committedText.ToString();
		if (!string.IsNullOrWhiteSpace(_partialText))
		{
			visibleText += Environment.NewLine + _partialText;
		}

		TranscriptEditor.Text = visibleText;
	}

	private void SetRunningState(bool isRunning)
	{
		StartButton.IsEnabled = !isRunning;
		StopButton.IsEnabled = isRunning;
		ApiKeyEntry.IsEnabled = !isRunning;
		LanguagePicker.IsEnabled = !isRunning;
		PromptEditor.IsEnabled = !isRunning;
	}

	private void SetStatus(string message)
	{
		MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
	}

	private void UpdateSubtitleWindowButton()
	{
		SubtitleWindowButton.Text = _subtitleOverlayService.IsOpen ? "关闭字幕窗" : "打开字幕窗";
	}

	private void UpdateModelDisplay()
	{
		var settings = _sessionSettings ?? _settings;
		ModelValueLabel.Text = ModelForMode(settings);
	}

	private async Task ApplyRuntimeSettingsAsync(AppSettings settings)
	{
		var lockTaken = false;
		try
		{
			await _audioPipelineLock.WaitAsync();
			lockTaken = true;

			if (settings.UsesHighPrecisionSubtitleMode)
			{
				var pending = _highPrecisionSegmenter.Flush("settings_changed");
				if (pending.AudioToSubmit.Length > 0)
				{
					EnqueueHighPrecisionSegment(pending);
				}

				_highPrecisionSegmenter = CreateHighPrecisionAudioSegmenter(settings);
				return;
			}

			await CommitPendingRealtimeAudioCoreAsync(CancellationToken.None);
			_realtimeVoiceActivityDetector = CreateRealtimeVoiceActivityDetector(settings);
			await _realtimeTranscriptionService.UpdateSessionAsync(settings, CancellationToken.None);
		}
		catch (Exception ex)
		{
			SetStatus($"应用运行设置失败：{ex.Message}");
		}
		finally
		{
			if (lockTaken)
			{
				_audioPipelineLock.Release();
			}
		}
	}

	private void LogSessionStrategy(AppSettings settings)
	{
		_realtimeTranscriptionService.LogClientEvent("local.session.strategy", new
		{
			model = ModelForMode(settings),
			language = settings.Language,
			segmentation_mode = settings.SegmentationMode,
			noise_reduction = settings.NoiseReductionMode,
			turn_detection = settings.UsesHighPrecisionSubtitleMode ? "audio_transcriptions" : "local_long_silence",
			realtime_long_silence_ms = settings.VadSilenceCommitMilliseconds,
			high_precision_target_seconds = settings.HighPrecisionTargetWindowSeconds,
			high_precision_max_seconds = settings.HighPrecisionMaxWindowSeconds,
			high_precision_overlap_seconds = settings.HighPrecisionOverlapSeconds,
			auto_save_transcript_on_stop = settings.AutoSaveTranscriptOnStop
		});
	}

	private static LocalVoiceActivityDetector CreateRealtimeVoiceActivityDetector(AppSettings settings)
	{
		return new LocalVoiceActivityDetector(new LocalVadOptions(
			AudioSampleRate,
			settings.VadMinimumSpeechRms,
			settings.VadNoiseMultiplier,
			TimeSpan.FromMilliseconds(settings.VadPreRollMilliseconds),
			TimeSpan.FromMilliseconds(settings.VadSilenceCommitMilliseconds),
			TimeSpan.Zero,
			TimeSpan.FromHours(1),
			true));
	}

	private static HighPrecisionAudioSegmenter CreateHighPrecisionAudioSegmenter(AppSettings settings)
	{
		var targetSeconds = Math.Clamp(settings.HighPrecisionTargetWindowSeconds, 3.0, 10.0);
		var maxSeconds = Math.Clamp(settings.HighPrecisionMaxWindowSeconds, targetSeconds + 0.5, 15.0);
		var overlapSeconds = Math.Clamp(settings.HighPrecisionOverlapSeconds, 0, Math.Max(0, targetSeconds - 0.5));

		return new HighPrecisionAudioSegmenter(new HighPrecisionAudioSegmenterOptions(
			AudioSampleRate,
			settings.VadMinimumSpeechRms,
			settings.VadNoiseMultiplier,
			TimeSpan.FromMilliseconds(settings.VadPreRollMilliseconds),
			TimeSpan.FromMilliseconds(settings.VadSilenceCommitMilliseconds),
			TimeSpan.FromSeconds(targetSeconds),
			TimeSpan.FromSeconds(maxSeconds),
			TimeSpan.FromSeconds(overlapSeconds)));
	}

	private static string BuildListeningStatus(AppSettings settings)
	{
		var noiseReductionName = NoiseReductionDisplayName(settings.NoiseReductionMode);
		if (settings.UsesHighPrecisionSubtitleMode)
		{
			return $"正在监听电脑系统音频。模型 {AppSettings.Gpt4oTranscribeModel}；高精度字幕；目标窗口 {settings.HighPrecisionTargetWindowSeconds:0.#} 秒，最长 {settings.HighPrecisionMaxWindowSeconds:0.#} 秒，重叠 {settings.HighPrecisionOverlapSeconds:0.#} 秒。";
		}

		return $"正在监听电脑系统音频。模型 {AppSettings.RealtimeWhisperModel}；实时对话；降噪 {noiseReductionName}；长静音 {settings.VadSilenceCommitMilliseconds:0} ms 后提交。";
	}

	private static string NoiseReductionDisplayName(string? mode)
	{
		return mode switch
		{
			AppSettings.NoiseReductionNearField => "近场",
			AppSettings.NoiseReductionFarField => "远场",
			_ => "关闭"
		};
	}

	private static string ModelForMode(AppSettings settings)
	{
		return settings.UsesHighPrecisionSubtitleMode
			? AppSettings.Gpt4oTranscribeModel
			: AppSettings.RealtimeWhisperModel;
	}

	private string TrimHighPrecisionOverlap(string text)
	{
		var cleaned = TranscriptionTextCleaner.CleanCompleted(text).Trim();
		if (string.IsNullOrWhiteSpace(cleaned) || string.IsNullOrWhiteSpace(_lastHighPrecisionText))
		{
			return cleaned;
		}

		var previous = _lastHighPrecisionText.Trim();
		var maxOverlap = Math.Min(80, Math.Min(previous.Length, cleaned.Length));
		for (var length = maxOverlap; length >= 6; length--)
		{
			var suffix = previous[^length..];
			var prefix = cleaned[..length];
			if (string.Equals(suffix, prefix, StringComparison.OrdinalIgnoreCase))
			{
				return cleaned[length..].TrimStart();
			}
		}

		return cleaned;
	}

	private static AppSettings CopySettings(AppSettings settings)
	{
		var copy = new AppSettings
		{
			ApiKey = settings.ApiKey,
			Model = ModelForMode(settings),
			Language = settings.Language,
			Prompt = settings.Prompt,
			SegmentationMode = settings.SegmentationMode,
			NoiseReductionMode = settings.NoiseReductionMode,
			AutoSaveTranscriptOnStop = settings.AutoSaveTranscriptOnStop,
			MaxSegmentSeconds = settings.MaxSegmentSeconds,
			FixedSegmentSeconds = settings.FixedSegmentSeconds,
			CinemaEarlyCommitPercent = settings.CinemaEarlyCommitPercent,
			HighPrecisionTargetWindowSeconds = settings.HighPrecisionTargetWindowSeconds,
			HighPrecisionMaxWindowSeconds = settings.HighPrecisionMaxWindowSeconds,
			HighPrecisionOverlapSeconds = settings.HighPrecisionOverlapSeconds,
			VadPreRollMilliseconds = settings.VadPreRollMilliseconds,
			VadSilenceCommitMilliseconds = settings.VadSilenceCommitMilliseconds,
			VadMinimumSpeechRms = settings.VadMinimumSpeechRms,
			VadNoiseMultiplier = settings.VadNoiseMultiplier,
			SubtitleBackgroundOpacity = settings.SubtitleBackgroundOpacity,
			SubtitleFontSize = settings.SubtitleFontSize,
			SubtitleLineHoldSeconds = settings.SubtitleLineHoldSeconds,
			SubtitleIdleClearSeconds = settings.SubtitleIdleClearSeconds
		};
		return copy;
	}

	private static LanguageOption FindLanguageOption(string? languageCode)
	{
		return LanguageOptions.FirstOrDefault(option =>
			string.Equals(option.Code, languageCode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? LanguageOptions[0];
	}

	private sealed record HighPrecisionAudioSegment(
		int Sequence,
		byte[] Audio,
		string Reason,
		TimeSpan Duration,
		AppSettings Settings);

	private sealed class LanguageOption(string label, string code)
	{
		public string Label { get; } = label;

		public string Code { get; } = code;

		public override string ToString()
		{
			return Label;
		}
	}
}
