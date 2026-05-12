using WindowsAiTranscriber.Models;
using WindowsAiTranscriber.Services;

namespace WindowsAiTranscriber;

public partial class SettingsPage : ContentPage
{
	private static readonly List<SegmentationModeOption> SegmentationModeOptions =
	[
		new("实时对话", AppSettings.RealtimeConversationMode),
		new("影视字幕", AppSettings.CinemaSubtitleMode)
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
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		_isLoaded = false;
		_settings = await _settingsService.LoadAsync();
		LoadSlidersFromSettings();
		SegmentationModePicker.SelectedItem = FindSegmentationModeOption(_settings.SegmentationMode);
		AutoSaveCheckBox.IsChecked = _settings.AutoSaveTranscriptOnStop;
		_isLoaded = true;
		UpdateLabels();
	}

	private void LoadSlidersFromSettings()
	{
		MaxSegmentSlider.Value = _settings.MaxSegmentSeconds;
		FixedSegmentSlider.Value = _settings.FixedSegmentSeconds;
		CinemaEarlyCommitSlider.Value = _settings.CinemaEarlyCommitPercent;
		PreRollSlider.Value = _settings.VadPreRollMilliseconds;
		SilenceCommitSlider.Value = _settings.VadSilenceCommitMilliseconds;
		MinimumRmsSlider.Value = _settings.VadMinimumSpeechRms;
		NoiseMultiplierSlider.Value = _settings.VadNoiseMultiplier;
		SubtitleFontSizeSlider.Value = _settings.SubtitleFontSize;
		SubtitleOpacitySlider.Value = _settings.SubtitleBackgroundOpacity;
		SubtitleHoldSlider.Value = _settings.SubtitleLineHoldSeconds;
		SubtitleIdleClearSlider.Value = _settings.SubtitleIdleClearSeconds;
	}

	private void ReadSettingsFromSliders()
	{
		_settings.SegmentationMode = (SegmentationModePicker.SelectedItem as SegmentationModeOption)?.Code ??
			AppSettings.RealtimeConversationMode;
		_settings.AutoSaveTranscriptOnStop = AutoSaveCheckBox.IsChecked;
		_settings.MaxSegmentSeconds = Math.Round(MaxSegmentSlider.Value, 1);
		_settings.FixedSegmentSeconds = Math.Round(FixedSegmentSlider.Value, 1);
		_settings.CinemaEarlyCommitPercent = Math.Round(CinemaEarlyCommitSlider.Value);
		_settings.VadPreRollMilliseconds = Math.Round(PreRollSlider.Value / 10) * 10;
		_settings.VadSilenceCommitMilliseconds = Math.Round(SilenceCommitSlider.Value / 10) * 10;
		_settings.VadMinimumSpeechRms = Math.Round(MinimumRmsSlider.Value, 3);
		_settings.VadNoiseMultiplier = Math.Round(NoiseMultiplierSlider.Value, 1);
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
		MaxSegmentLabel.Text = $"实时最长分段：{_settings.MaxSegmentSeconds:0.0} 秒";
		FixedSegmentLabel.Text = $"影视提交间隔：{_settings.FixedSegmentSeconds:0.0} 秒";
		CinemaEarlyCommitLabel.Text = $"影视提前提交：{_settings.CinemaEarlyCommitPercent:0}% 后遇到静音";
		PreRollLabel.Text = $"前置音频：{_settings.VadPreRollMilliseconds:0} ms";
		SilenceCommitLabel.Text = $"静音提交：{_settings.VadSilenceCommitMilliseconds:0} ms";
		MinimumRmsLabel.Text = $"最低语音音量：{_settings.VadMinimumSpeechRms:0.000}";
		NoiseMultiplierLabel.Text = $"噪声倍率：{_settings.VadNoiseMultiplier:0.0}";
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

	private sealed class SegmentationModeOption(string label, string code)
	{
		public string Label { get; } = label;

		public string Code { get; } = code;

		public override string ToString()
		{
			return Label;
		}
	}
}
