using System.Text;
using WindowsAiTranscriber.Models;
using WindowsAiTranscriber.Services;

namespace WindowsAiTranscriber;

public partial class MainPage : ContentPage
{
	private static readonly string[] ModelNames =
	[
		AppSettings.RealtimeWhisperModel,
		AppSettings.Gpt4oTranscribeModel
	];

	private static readonly List<LanguageOption> LanguageOptions =
	[
		new("中文", "zh"),
		new("英文", "en"),
		new("日语", "ja")
	];

	private const int VadSampleRate = 24000;
	private const int MinimumCommitBytes = VadSampleRate * 2 / 10;

	private readonly AppSettingsService _settingsService;
	private readonly TranscriptStore _transcriptStore;
	private readonly OpenAIRealtimeTranscriptionService _transcriptionService;
	private readonly IAudioCaptureService _audioCaptureService;
	private readonly SubtitleOverlayService _subtitleOverlayService;
	private readonly SemaphoreSlim _audioPipelineLock = new(1, 1);
	private readonly List<TranscriptSegment> _segments = [];
	private readonly List<byte[]> _cinemaSegmentChunks = [];
	private readonly StringBuilder _committedText = new();
	private AppSettings _settings = new();
	private LocalVoiceActivityDetector _voiceActivityDetector = CreateVoiceActivityDetector(new AppSettings());
	private CancellationTokenSource? _sessionCts;
	private int _cinemaSegmentBytes;
	private int _hasUncommittedAudio;
	private string _partialText = "";
	private bool _subtitleReceivedDeltaForCurrentSegment;

	public MainPage(
		AppSettingsService settingsService,
		TranscriptStore transcriptStore,
		OpenAIRealtimeTranscriptionService transcriptionService,
		IAudioCaptureService audioCaptureService,
		SubtitleOverlayService subtitleOverlayService)
	{
		InitializeComponent();
		_settingsService = settingsService;
		_transcriptStore = transcriptStore;
		_transcriptionService = transcriptionService;
		_audioCaptureService = audioCaptureService;
		_subtitleOverlayService = subtitleOverlayService;

		ModelPicker.ItemsSource = ModelNames;
		ModelPicker.SelectedIndex = 0;
		LanguagePicker.ItemsSource = LanguageOptions;
		LanguagePicker.SelectedItem = LanguageOptions[0];

		_transcriptionService.StatusChanged += OnTranscriptionStatusChanged;
		_transcriptionService.ErrorOccurred += OnTranscriptionError;
		_transcriptionService.PartialTranscriptReceived += OnPartialTranscriptReceived;
		_transcriptionService.TranscriptCompleted += OnTranscriptCompleted;
		_audioCaptureService.CaptureError += OnAudioCaptureError;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		_settings = await _settingsService.LoadAsync();
		ApiKeyEntry.Text = _settings.ApiKey;
		LanguagePicker.SelectedItem = FindLanguageOption(_settings.Language);
		PromptEditor.Text = _settings.Prompt;
		ModelPicker.SelectedItem = ModelNames.Contains(_settings.Model) ? _settings.Model : ModelNames[0];
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
			_settings = settings;
			_subtitleOverlayService.ApplySettings(settings);
			_ = ApplyRuntimeVadSettingsAsync(settings);
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
			await _settingsService.SaveAsync(_settings);
			_sessionCts = new CancellationTokenSource();
			SetRunningState(isRunning: true);
			SetStatus("正在连接 OpenAI 实时转写服务...");

			await _transcriptionService.StartAsync(_settings, _sessionCts.Token);
			LogSessionStrategy(_settings);

			_voiceActivityDetector = CreateVoiceActivityDetector(_settings);
			_voiceActivityDetector.Reset();
			ClearCinemaSegmentBuffer();
			Interlocked.Exchange(ref _hasUncommittedAudio, 0);
			_audioCaptureService.AudioAvailable += OnAudioAvailable;
			_audioCaptureService.Start();
			SetStatus(BuildListeningStatus(_settings));
			OutputPathLabel.Text = $"事件日志：{_transcriptionService.EventLogPath}";
		}
		catch (Exception ex)
		{
			await StopSessionAsync(saveTranscript: false);
			await DisplayAlertAsync("启动失败", ex.Message, "确定");
			SetStatus("启动失败。请检查 API Key、网络和系统音频设备。");
		}
	}

	private async Task StopSessionAsync(bool saveTranscript)
	{
		if (_sessionCts is null)
		{
			return;
		}

		var cts = _sessionCts;
		_sessionCts = null;

		_audioCaptureService.AudioAvailable -= OnAudioAvailable;
		_audioCaptureService.Stop();
		await CommitPendingAudioAsync(CancellationToken.None);
		_voiceActivityDetector.Reset();
		ClearCinemaSegmentBuffer();

		cts.Cancel();
		await _transcriptionService.StopAsync();
		cts.Dispose();

		if (saveTranscript && _settings.AutoSaveTranscriptOnStop)
		{
			await SaveTranscriptAsync();
		}

		SetRunningState(isRunning: false);
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
		_settings.Model = ModelPicker.SelectedItem?.ToString() ?? ModelNames[0];
		_settings.Language = (LanguagePicker.SelectedItem as LanguageOption)?.Code ?? LanguageOptions[0].Code;
		_settings.Prompt = PromptEditor.Text?.Trim() ?? "";
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

			if (_settings.UsesFixedSegmentMode)
			{
				await ProcessCinemaSubtitleAudioAsync(pcm16Audio, cancellationToken);
				return;
			}

			await ProcessRealtimeConversationAudioAsync(pcm16Audio, cancellationToken);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SetStatus($"发送音频失败：{ex.Message}");
		}
		finally
		{
			if (lockTaken)
			{
				_audioPipelineLock.Release();
			}
		}
	}

	private async Task ProcessRealtimeConversationAudioAsync(byte[] pcm16Audio, CancellationToken cancellationToken)
	{
		var vadResult = _voiceActivityDetector.Process(pcm16Audio);
		UpdateVadStatus(vadResult);

		if (vadResult.AudioToSend.Length == 0)
		{
			return;
		}

		await _transcriptionService.SendAudioAsync(vadResult.AudioToSend, cancellationToken);
		Interlocked.Exchange(ref _hasUncommittedAudio, 1);

		if (vadResult.ShouldCommit)
		{
			await CommitPendingAudioCoreAsync(cancellationToken);
		}
	}

	private async Task ProcessCinemaSubtitleAudioAsync(byte[] pcm16Audio, CancellationToken cancellationToken)
	{
		var vadResult = _voiceActivityDetector.Process(pcm16Audio);
		UpdateVadStatus(vadResult);

		if (vadResult.AudioToSend.Length == 0)
		{
			return;
		}

		var wasEmpty = _cinemaSegmentBytes == 0;
		AppendCinemaSegmentAudio(vadResult.AudioToSend);

		if (wasEmpty)
		{
			LogCinemaEvent("local.cinema_segment.started", vadResult, new
			{
				buffered_ms = Pcm16BytesToMilliseconds(_cinemaSegmentBytes),
				target_ms = (int)Math.Round(_settings.FixedSegmentSeconds * 1000),
				early_commit_percent = _settings.CinemaEarlyCommitPercent,
				early_commit_after_ms = (int)Math.Round(_settings.FixedSegmentSeconds * _settings.CinemaEarlyCommitPercent * 10)
			});
		}

		if (vadResult.ShouldCommit)
		{
			var reason = _cinemaSegmentBytes >= FixedSegmentByteCount(_settings)
				? "fixed_interval"
				: "silence_after_early_threshold";
			await FlushCinemaSegmentCoreAsync(cancellationToken, reason, vadResult);
		}
	}

	private void AppendCinemaSegmentAudio(byte[] pcm16Audio)
	{
		var copy = new byte[pcm16Audio.Length];
		Array.Copy(pcm16Audio, copy, pcm16Audio.Length);
		_cinemaSegmentChunks.Add(copy);
		_cinemaSegmentBytes += copy.Length;
	}

	private async Task FlushCinemaSegmentCoreAsync(
		CancellationToken cancellationToken,
		string reason,
		LocalVadResult? vadResult = null)
	{
		if (_cinemaSegmentBytes == 0)
		{
			return;
		}

		if (_cinemaSegmentBytes < MinimumCommitBytes)
		{
			_transcriptionService.LogClientEvent("local.cinema_segment.discarded", new
			{
				reason,
				buffered_ms = Pcm16BytesToMilliseconds(_cinemaSegmentBytes),
				minimum_ms = 100
			});
			ClearCinemaSegmentBuffer();
			return;
		}

		var audio = BuildCinemaSegmentAudio();
		_transcriptionService.LogClientEvent("local.cinema_segment.sending", new
		{
			reason,
			bytes = audio.Length,
			duration_ms = Pcm16BytesToMilliseconds(audio.Length),
			model = _settings.Model,
			segmentation_mode = _settings.SegmentationMode,
			target_ms = (int)Math.Round(_settings.FixedSegmentSeconds * 1000),
			early_commit_percent = _settings.CinemaEarlyCommitPercent,
			rms = vadResult?.Rms,
			threshold = vadResult?.Threshold
		});
		SetStatus($"影视字幕片段已缓存 {Pcm16BytesToMilliseconds(audio.Length) / 1000.0:0.0} 秒，正在发送给模型。");

		await _transcriptionService.SendAudioAsync(audio, cancellationToken);
		Interlocked.Exchange(ref _hasUncommittedAudio, 1);
		await CommitPendingAudioCoreAsync(cancellationToken);
		_transcriptionService.LogClientEvent("local.cinema_segment.committed", new
		{
			reason,
			bytes = audio.Length,
			duration_ms = Pcm16BytesToMilliseconds(audio.Length)
		});
		ClearCinemaSegmentBuffer();
	}

	private byte[] BuildCinemaSegmentAudio()
	{
		var audio = new byte[_cinemaSegmentBytes];
		var offset = 0;
		foreach (var chunk in _cinemaSegmentChunks)
		{
			Array.Copy(chunk, 0, audio, offset, chunk.Length);
			offset += chunk.Length;
		}

		return audio;
	}

	private void ClearCinemaSegmentBuffer()
	{
		_cinemaSegmentChunks.Clear();
		_cinemaSegmentBytes = 0;
	}

	private void LogCinemaEvent(string type, LocalVadResult vadResult, object payload)
	{
		_transcriptionService.LogClientEvent(type, new
		{
			model = _settings.Model,
			segmentation_mode = _settings.SegmentationMode,
			rms = vadResult.Rms,
			threshold = vadResult.Threshold,
			payload
		});
	}

	private void LogSessionStrategy(AppSettings settings)
	{
		_transcriptionService.LogClientEvent("local.session.strategy", new
		{
			model = settings.Model,
			language = settings.Language,
			segmentation_mode = settings.SegmentationMode,
			turn_detection = "null",
			realtime_max_segment_seconds = settings.MaxSegmentSeconds,
			cinema_fixed_segment_seconds = settings.FixedSegmentSeconds,
			cinema_early_commit_percent = settings.CinemaEarlyCommitPercent,
			auto_save_transcript_on_stop = settings.AutoSaveTranscriptOnStop
		});
	}

	private static int Pcm16BytesToMilliseconds(int byteCount)
	{
		return (int)Math.Round(byteCount / (VadSampleRate * 2.0) * 1000);
	}

	private static int FixedSegmentByteCount(AppSettings settings)
	{
		return TimeSpan.FromSeconds(settings.FixedSegmentSeconds).ToPcm16ByteCount(VadSampleRate);
	}

	private async Task CommitPendingAudioAsync(CancellationToken cancellationToken)
	{
		var lockTaken = false;
		try
		{
			await _audioPipelineLock.WaitAsync(cancellationToken);
			lockTaken = true;

			if (_settings.UsesFixedSegmentMode)
			{
				await FlushCinemaSegmentCoreAsync(cancellationToken, "stop");
			}

			await CommitPendingAudioCoreAsync(cancellationToken);
		}
		finally
		{
			if (lockTaken)
			{
				_audioPipelineLock.Release();
			}
		}
	}

	private async Task CommitPendingAudioCoreAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _hasUncommittedAudio, 0) == 0)
		{
			return;
		}

		try
		{
			await _transcriptionService.CommitAudioAsync(cancellationToken);
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

	private void UpdateVadStatus(LocalVadResult vadResult)
	{
		switch (vadResult.State)
		{
			case LocalVadState.SpeechStarted:
				SetStatus(_settings.UsesFixedSegmentMode
					? "本地 VAD 检测到声音，开始缓存影视字幕音频。"
					: "本地 VAD 检测到声音，开始发送音频。");
				break;
			case LocalVadState.Commit:
				var message = _settings.UsesFixedSegmentMode
					? $"影视字幕片段已缓存满。音量 {vadResult.Rms:0.000} / 阈值 {vadResult.Threshold:0.000}"
					: $"本地 VAD 已提交一段音频。音量 {vadResult.Rms:0.000} / 阈值 {vadResult.Threshold:0.000}";
				SetStatus(message);
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

		_segments.Add(segment);
		if (!_subtitleReceivedDeltaForCurrentSegment)
		{
			_subtitleOverlayService.AppendText(segment.Text.Trim());
		}

		_committedText.Append('[')
			.Append(segment.StartedAt.ToLocalTime().ToString("HH:mm:ss"))
			.Append("] ")
			.AppendLine(segment.Text.Trim());
		_partialText = "";
		_subtitleReceivedDeltaForCurrentSegment = false;

		MainThread.BeginInvokeOnMainThread(() =>
		{
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
		ModelPicker.IsEnabled = !isRunning;
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

	private async Task ApplyRuntimeVadSettingsAsync(AppSettings settings)
	{
		var lockTaken = false;
		try
		{
			await _audioPipelineLock.WaitAsync();
			lockTaken = true;
			if (_cinemaSegmentBytes > 0)
			{
				await FlushCinemaSegmentCoreAsync(CancellationToken.None, "settings_changed");
			}

			await CommitPendingAudioCoreAsync(CancellationToken.None);
			_voiceActivityDetector = CreateVoiceActivityDetector(settings);
		}
		catch (Exception ex)
		{
			SetStatus($"应用 VAD 设置失败：{ex.Message}");
		}
		finally
		{
			if (lockTaken)
			{
				_audioPipelineLock.Release();
			}
		}
	}

	private string LastCompletedSubtitleText()
	{
		return _segments.Count == 0 ? "" : _segments[^1].Text;
	}

	private static LocalVoiceActivityDetector CreateVoiceActivityDetector(AppSettings settings)
	{
		var maxSegmentSeconds = settings.UsesFixedSegmentMode
			? settings.FixedSegmentSeconds
			: settings.MaxSegmentSeconds;
		var minimumSilenceCommitSeconds = settings.UsesFixedSegmentMode
			? settings.FixedSegmentSeconds * Math.Clamp(settings.CinemaEarlyCommitPercent, 50, 100) / 100.0
			: 0;

		return new LocalVoiceActivityDetector(new LocalVadOptions(
			VadSampleRate,
			settings.VadMinimumSpeechRms,
			settings.VadNoiseMultiplier,
			TimeSpan.FromMilliseconds(settings.VadPreRollMilliseconds),
			TimeSpan.FromMilliseconds(settings.VadSilenceCommitMilliseconds),
			TimeSpan.FromSeconds(minimumSilenceCommitSeconds),
			TimeSpan.FromSeconds(maxSegmentSeconds),
			true));
	}

	private static string BuildListeningStatus(AppSettings settings)
	{
		var modeName = settings.UsesFixedSegmentMode ? "影视字幕" : "实时对话";
		if (settings.UsesFixedSegmentMode)
		{
			return $"正在监听电脑系统音频。模型 {settings.Model}；{modeName}；turn_detection=null；本地缓存最多 {settings.FixedSegmentSeconds:0.#} 秒，{settings.CinemaEarlyCommitPercent:0}% 后遇到静音会提前发送。";
		}

		return $"正在监听电脑系统音频。模型 {settings.Model}；{modeName}；turn_detection=null；静音断句，最长 {settings.MaxSegmentSeconds:0.#} 秒提交。";
	}

	private static LanguageOption FindLanguageOption(string? languageCode)
	{
		return LanguageOptions.FirstOrDefault(option =>
			string.Equals(option.Code, languageCode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? LanguageOptions[0];
	}

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
