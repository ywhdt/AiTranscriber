using WindowsAiTranscriber.Models;
using WindowsAiTranscriber.Services;

namespace WindowsAiTranscriber;

public partial class SettingsPage : ContentPage
{
	private const double RealtimeMaxSegmentMinSeconds = 5.0;
	private const double RealtimeMaxSegmentMaxSeconds = 60.0;

	private static readonly List<SegmentationModeOption> SegmentationModeOptions =
	[
		new("实时对话", AppSettings.RealtimeConversationMode),
		new("高精度字幕", AppSettings.CinemaSubtitleMode)
	];

	private static readonly List<NoiseReductionOption> NoiseReductionOptions =
	[
		new("关闭（系统音频推荐）", AppSettings.NoiseReductionOff),
		new("近场麦克风", AppSettings.NoiseReductionNearField),
		new("远场麦克风", AppSettings.NoiseReductionFarField)
	];

	private readonly AppSettingsService _settingsService;
	private readonly SubtitleOverlayService _subtitleOverlayService;
	private AppSettings _settings = new();
	private bool _isLoaded;

	public event EventHandler<AppSettings>? SettingsSaved;

	public SettingsPage(AppSettingsService settingsService, SubtitleOverlayService subtitleOverlayService)
	{
		InitializeComponent();
		_settingsService = settingsService;
		_subtitleOverlayService = subtitleOverlayService;
		SegmentationModePicker.ItemsSource = SegmentationModeOptions;
		NoiseReductionPicker.ItemsSource = NoiseReductionOptions;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		_isLoaded = false;
		_settings = await _settingsService.LoadAsync();
		LoadSlidersFromSettings();
		SegmentationModePicker.SelectedItem = FindSegmentationModeOption(_settings.SegmentationMode);
		NoiseReductionPicker.SelectedItem = FindNoiseReductionOption(_settings.NoiseReductionMode);
		AutoSaveCheckBox.IsChecked = _settings.AutoSaveTranscriptOnStop;
		_isLoaded = true;
		UpdateLabels();
	}

	private void LoadSlidersFromSettings()
	{
		HighPrecisionTargetSlider.Value = _settings.HighPrecisionTargetWindowSeconds;
		HighPrecisionMaxSlider.Value = _settings.HighPrecisionMaxWindowSeconds;
		HighPrecisionOverlapSlider.Value = _settings.HighPrecisionOverlapSeconds;
		PreRollSlider.Value = _settings.VadPreRollMilliseconds;
		RealtimeSilenceCommitSlider.Value = _settings.RealtimeSilenceCommitMilliseconds;
		HighPrecisionSilenceCommitSlider.Value = _settings.HighPrecisionSilenceCommitMilliseconds;
		_settings.MaxSegmentSeconds = Math.Clamp(
			_settings.MaxSegmentSeconds,
			RealtimeMaxSegmentMinSeconds,
			RealtimeMaxSegmentMaxSeconds);
		RealtimeMaxSegmentSlider.Value = _settings.MaxSegmentSeconds;
		SubtitleFontSizeSlider.Value = _settings.SubtitleFontSize;
		SubtitleOpacitySlider.Value = _settings.SubtitleBackgroundOpacity;
		SubtitleHoldSlider.Value = _settings.SubtitleLineHoldSeconds;
		SubtitleIdleClearSlider.Value = _settings.SubtitleIdleClearSeconds;
	}

	private void ReadSettingsFromSliders()
	{
		_settings.SegmentationMode = (SegmentationModePicker.SelectedItem as SegmentationModeOption)?.Code ??
			AppSettings.RealtimeConversationMode;
		_settings.NoiseReductionMode = (NoiseReductionPicker.SelectedItem as NoiseReductionOption)?.Code ??
			AppSettings.NoiseReductionOff;
		_settings.AutoSaveTranscriptOnStop = AutoSaveCheckBox.IsChecked;
		_settings.HighPrecisionTargetWindowSeconds = Math.Round(HighPrecisionTargetSlider.Value, 1);
		_settings.HighPrecisionMaxWindowSeconds = Math.Round(Math.Max(
			HighPrecisionMaxSlider.Value,
			_settings.HighPrecisionTargetWindowSeconds + 0.5), 1);
		_settings.HighPrecisionOverlapSeconds = Math.Round(Math.Min(
			HighPrecisionOverlapSlider.Value,
			Math.Max(0, _settings.HighPrecisionTargetWindowSeconds - 0.5)), 1);
		_settings.VadPreRollMilliseconds = Math.Round(PreRollSlider.Value / 10) * 10;
		_settings.RealtimeSilenceCommitMilliseconds = Math.Round(RealtimeSilenceCommitSlider.Value / 10) * 10;
		_settings.HighPrecisionSilenceCommitMilliseconds = Math.Round(HighPrecisionSilenceCommitSlider.Value / 10) * 10;
		_settings.MaxSegmentSeconds = Math.Round(Math.Clamp(
			RealtimeMaxSegmentSlider.Value,
			RealtimeMaxSegmentMinSeconds,
			RealtimeMaxSegmentMaxSeconds));
		_settings.SubtitleFontSize = Math.Round(SubtitleFontSizeSlider.Value);
		_settings.SubtitleBackgroundOpacity = Math.Round(SubtitleOpacitySlider.Value, 2);
		_settings.SubtitleLineHoldSeconds = Math.Round(SubtitleHoldSlider.Value, 1);
		_settings.SubtitleIdleClearSeconds = Math.Round(SubtitleIdleClearSlider.Value, 1);
	}

	private void OnSliderChanged(object? sender, ValueChangedEventArgs e)
	{
		if (!_isLoaded)
		{
			return;
		}

		ReadSettingsFromSliders();
		UpdateLabels();
		_subtitleOverlayService.ApplySettings(_settings);
	}

	private void OnSegmentationModeChanged(object? sender, EventArgs e)
	{
		if (!_isLoaded)
		{
			return;
		}

		ReadSettingsFromSliders();
		UpdateLabels();
	}

	private void OnNoiseReductionChanged(object? sender, EventArgs e)
	{
		if (!_isLoaded)
		{
			return;
		}

		ReadSettingsFromSliders();
	}

	private void OnAutoSaveChanged(object? sender, CheckedChangedEventArgs e)
	{
		if (!_isLoaded)
		{
			return;
		}

		ReadSettingsFromSliders();
	}

	private async void OnSaveClicked(object? sender, EventArgs e)
	{
		ReadSettingsFromSliders();
		await _settingsService.SaveAsync(_settings);
		_subtitleOverlayService.ApplySettings(_settings);
		SettingsSaved?.Invoke(this, _settings);
		await Navigation.PopModalAsync();
	}

	private async void OnResetClicked(object? sender, EventArgs e)
	{
		var current = await _settingsService.LoadAsync();
		_settings = new AppSettings
		{
			ApiKey = current.ApiKey,
			Model = current.Model,
			Language = current.Language,
			Prompt = current.Prompt
		};
		LoadSlidersFromSettings();
		SegmentationModePicker.SelectedItem = FindSegmentationModeOption(_settings.SegmentationMode);
		NoiseReductionPicker.SelectedItem = FindNoiseReductionOption(_settings.NoiseReductionMode);
		AutoSaveCheckBox.IsChecked = _settings.AutoSaveTranscriptOnStop;
		ReadSettingsFromSliders();
		UpdateLabels();
		_subtitleOverlayService.ApplySettings(_settings);
	}

	private async void OnCloseClicked(object? sender, EventArgs e)
	{
		var savedSettings = await _settingsService.LoadAsync();
		_subtitleOverlayService.ApplySettings(savedSettings);
		await Navigation.PopModalAsync();
	}

	private void UpdateLabels()
	{
		HighPrecisionTargetLabel.Text = $"高精度目标窗口：{_settings.HighPrecisionTargetWindowSeconds:0.0} 秒";
		HighPrecisionMaxLabel.Text = $"高精度最长窗口：{_settings.HighPrecisionMaxWindowSeconds:0.0} 秒";
		HighPrecisionOverlapLabel.Text = $"高精度重叠：{_settings.HighPrecisionOverlapSeconds:0.0} 秒";
		PreRollLabel.Text = $"前置音频：{_settings.VadPreRollMilliseconds:0} ms";
		RealtimeSilenceCommitLabel.Text = $"实时长静音提交：{_settings.RealtimeSilenceCommitMilliseconds:0} ms";
		HighPrecisionSilenceCommitLabel.Text = $"高精度长静音提交：{_settings.HighPrecisionSilenceCommitMilliseconds:0} ms";
		RealtimeMaxSegmentLabel.Text = $"实时最长片段：{_settings.MaxSegmentSeconds:0} 秒";
		SubtitleFontSizeLabel.Text = $"字幕字号：{_settings.SubtitleFontSize:0}";
		SubtitleOpacityLabel.Text = $"字幕背景不透明度：{_settings.SubtitleBackgroundOpacity:P0}";
		SubtitleHoldLabel.Text = $"满行停留：{_settings.SubtitleLineHoldSeconds:0.0} 秒";
		SubtitleIdleClearLabel.Text = $"空闲清空：{_settings.SubtitleIdleClearSeconds:0.0} 秒";
	}

	private static SegmentationModeOption FindSegmentationModeOption(string? mode)
	{
		return SegmentationModeOptions.FirstOrDefault(option =>
			string.Equals(option.Code, mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? SegmentationModeOptions[0];
	}

	private static NoiseReductionOption FindNoiseReductionOption(string? mode)
	{
		return NoiseReductionOptions.FirstOrDefault(option =>
			string.Equals(option.Code, mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? NoiseReductionOptions[0];
	}

	private sealed class SegmentationModeOption(string label, string code)
	{
		public string Label { get; } = label;

		public string Code { get; } = code;

		public override string ToString()
		{
			return Label;
		}
	}

	private sealed class NoiseReductionOption(string label, string code)
	{
		public string Label { get; } = label;

		public string Code { get; } = code;

		public override string ToString()
		{
			return Label;
		}
	}
}
