using WindowsAiTranscriber.Models;

namespace WindowsAiTranscriber.Services;

public sealed class AppSettingsService
{
	private const string ApiKeyName = "openai_api_key";
	private const string ModelName = "openai_transcription_model";
	private const string LanguageName = "transcription_language";
	private const string PromptName = "transcription_prompt";
	private const string SegmentationModeName = "segmentation_mode";
	private const string NoiseReductionModeName = "noise_reduction_mode";
	private const string AutoSaveTranscriptOnStopName = "auto_save_transcript_on_stop";
	private const string MaxSegmentSecondsName = "vad_max_segment_seconds";
	private const string FixedSegmentSecondsName = "fixed_segment_seconds";
	private const string CinemaEarlyCommitPercentName = "cinema_early_commit_percent";
	private const string HighPrecisionTargetWindowSecondsName = "high_precision_target_window_seconds";
	private const string HighPrecisionMaxWindowSecondsName = "high_precision_max_window_seconds";
	private const string HighPrecisionOverlapSecondsName = "high_precision_overlap_seconds";
	private const string VadPreRollMillisecondsName = "vad_pre_roll_milliseconds";
	private const string VadSilenceCommitMillisecondsName = "vad_silence_commit_milliseconds";
	private const string RealtimeSilenceCommitMillisecondsName = "realtime_silence_commit_milliseconds";
	private const string HighPrecisionSilenceCommitMillisecondsName = "high_precision_silence_commit_milliseconds";
	private const string VadMinimumSpeechRmsName = "vad_minimum_speech_rms";
	private const string VadNoiseMultiplierName = "vad_noise_multiplier";
	private const string SubtitleBackgroundOpacityName = "subtitle_background_opacity";
	private const string SubtitleFontSizeName = "subtitle_font_size";
	private const string SubtitleLineHoldSecondsName = "subtitle_line_hold_seconds";
	private const string SubtitleIdleClearSecondsName = "subtitle_idle_clear_seconds";

	public async Task<AppSettings> LoadAsync()
	{
		var hasLegacySilenceCommitMilliseconds = Preferences.Default.ContainsKey(VadSilenceCommitMillisecondsName);
		var legacySilenceCommitMilliseconds = Preferences.Default.Get(VadSilenceCommitMillisecondsName, 1200.0);
		var realtimeSilenceCommitDefault = hasLegacySilenceCommitMilliseconds
			? legacySilenceCommitMilliseconds
			: 800.0;
		var highPrecisionSilenceCommitDefault = hasLegacySilenceCommitMilliseconds
			? legacySilenceCommitMilliseconds
			: 4000.0;

		return new AppSettings
		{
			ApiKey = await SecureStorage.Default.GetAsync(ApiKeyName) ?? "",
			Model = Preferences.Default.Get(ModelName, AppSettings.Gpt4oTranscribeModel),
			Language = Preferences.Default.Get(LanguageName, "ja"),
			Prompt = Preferences.Default.Get(PromptName, ""),
			SegmentationMode = Preferences.Default.Get(SegmentationModeName, AppSettings.CinemaSubtitleMode),
			NoiseReductionMode = Preferences.Default.Get(NoiseReductionModeName, AppSettings.NoiseReductionOff),
			AutoSaveTranscriptOnStop = Preferences.Default.Get(AutoSaveTranscriptOnStopName, false),
			MaxSegmentSeconds = Preferences.Default.Get(MaxSegmentSecondsName, 12.0),
			FixedSegmentSeconds = Preferences.Default.Get(FixedSegmentSecondsName, 6.0),
			CinemaEarlyCommitPercent = Preferences.Default.Get(CinemaEarlyCommitPercentName, 80.0),
			HighPrecisionTargetWindowSeconds = Preferences.Default.Get(HighPrecisionTargetWindowSecondsName, 6.0),
			HighPrecisionMaxWindowSeconds = Preferences.Default.Get(HighPrecisionMaxWindowSecondsName, 12.0),
			HighPrecisionOverlapSeconds = Preferences.Default.Get(HighPrecisionOverlapSecondsName, 3.0),
			VadPreRollMilliseconds = Preferences.Default.Get(VadPreRollMillisecondsName, 300.0),
			RealtimeSilenceCommitMilliseconds = Preferences.Default.Get(
				RealtimeSilenceCommitMillisecondsName,
				realtimeSilenceCommitDefault),
			HighPrecisionSilenceCommitMilliseconds = Preferences.Default.Get(
				HighPrecisionSilenceCommitMillisecondsName,
				highPrecisionSilenceCommitDefault),
			VadMinimumSpeechRms = Preferences.Default.Get(VadMinimumSpeechRmsName, 0.02),
			VadNoiseMultiplier = Preferences.Default.Get(VadNoiseMultiplierName, 3.0),
			SubtitleBackgroundOpacity = Preferences.Default.Get(SubtitleBackgroundOpacityName, 0.44),
			SubtitleFontSize = Preferences.Default.Get(SubtitleFontSizeName, 34.0),
			SubtitleLineHoldSeconds = Preferences.Default.Get(SubtitleLineHoldSecondsName, 0.5),
			SubtitleIdleClearSeconds = Preferences.Default.Get(SubtitleIdleClearSecondsName, 2.0)
		};
	}

	public async Task SaveAsync(AppSettings settings)
	{
		if (string.IsNullOrWhiteSpace(settings.ApiKey))
		{
			SecureStorage.Default.Remove(ApiKeyName);
		}
		else
		{
			await SecureStorage.Default.SetAsync(ApiKeyName, settings.ApiKey);
		}

		Preferences.Default.Set(ModelName, settings.Model);
		Preferences.Default.Set(LanguageName, settings.Language);
		Preferences.Default.Set(PromptName, settings.Prompt);
		Preferences.Default.Set(SegmentationModeName, settings.SegmentationMode);
		Preferences.Default.Set(NoiseReductionModeName, settings.NoiseReductionMode);
		Preferences.Default.Set(AutoSaveTranscriptOnStopName, settings.AutoSaveTranscriptOnStop);
		Preferences.Default.Set(MaxSegmentSecondsName, settings.MaxSegmentSeconds);
		Preferences.Default.Set(FixedSegmentSecondsName, settings.FixedSegmentSeconds);
		Preferences.Default.Set(CinemaEarlyCommitPercentName, settings.CinemaEarlyCommitPercent);
		Preferences.Default.Set(HighPrecisionTargetWindowSecondsName, settings.HighPrecisionTargetWindowSeconds);
		Preferences.Default.Set(HighPrecisionMaxWindowSecondsName, settings.HighPrecisionMaxWindowSeconds);
		Preferences.Default.Set(HighPrecisionOverlapSecondsName, settings.HighPrecisionOverlapSeconds);
		Preferences.Default.Set(VadPreRollMillisecondsName, settings.VadPreRollMilliseconds);
		Preferences.Default.Set(RealtimeSilenceCommitMillisecondsName, settings.RealtimeSilenceCommitMilliseconds);
		Preferences.Default.Set(HighPrecisionSilenceCommitMillisecondsName, settings.HighPrecisionSilenceCommitMilliseconds);
		Preferences.Default.Set(VadMinimumSpeechRmsName, settings.VadMinimumSpeechRms);
		Preferences.Default.Set(VadNoiseMultiplierName, settings.VadNoiseMultiplier);
		Preferences.Default.Set(SubtitleBackgroundOpacityName, settings.SubtitleBackgroundOpacity);
		Preferences.Default.Set(SubtitleFontSizeName, settings.SubtitleFontSize);
		Preferences.Default.Set(SubtitleLineHoldSecondsName, settings.SubtitleLineHoldSeconds);
		Preferences.Default.Set(SubtitleIdleClearSecondsName, settings.SubtitleIdleClearSeconds);
	}

	public Task SaveVadSensitivityAsync(double minimumSpeechRms, double noiseMultiplier)
	{
		Preferences.Default.Set(VadMinimumSpeechRmsName, minimumSpeechRms);
		Preferences.Default.Set(VadNoiseMultiplierName, noiseMultiplier);
		return Task.CompletedTask;
	}
}
