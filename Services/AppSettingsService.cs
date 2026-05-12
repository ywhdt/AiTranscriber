using WindowsAiTranscriber.Models;

namespace WindowsAiTranscriber.Services;

public sealed class AppSettingsService
{
	private const string ApiKeyName = "openai_api_key";
	private const string ModelName = "openai_transcription_model";
	private const string LanguageName = "transcription_language";
	private const string PromptName = "transcription_prompt";
	private const string SegmentationModeName = "segmentation_mode";
	private const string AutoSaveTranscriptOnStopName = "auto_save_transcript_on_stop";
	private const string MaxSegmentSecondsName = "vad_max_segment_seconds";
	private const string FixedSegmentSecondsName = "fixed_segment_seconds";
	private const string CinemaEarlyCommitPercentName = "cinema_early_commit_percent";
	private const string VadPreRollMillisecondsName = "vad_pre_roll_milliseconds";
	private const string VadSilenceCommitMillisecondsName = "vad_silence_commit_milliseconds";
	private const string VadMinimumSpeechRmsName = "vad_minimum_speech_rms";
	private const string VadNoiseMultiplierName = "vad_noise_multiplier";
	private const string SubtitleBackgroundOpacityName = "subtitle_background_opacity";
	private const string SubtitleFontSizeName = "subtitle_font_size";
	private const string SubtitleLineHoldSecondsName = "subtitle_line_hold_seconds";
	private const string SubtitleIdleClearSecondsName = "subtitle_idle_clear_seconds";

	public async Task<AppSettings> LoadAsync()
	{
		return new AppSettings
		{
			ApiKey = await SecureStorage.Default.GetAsync(ApiKeyName) ?? "",
			Model = Preferences.Default.Get(ModelName, AppSettings.RealtimeWhisperModel),
			Language = Preferences.Default.Get(LanguageName, "zh"),
			Prompt = Preferences.Default.Get(PromptName, ""),
			SegmentationMode = Preferences.Default.Get(SegmentationModeName, AppSettings.RealtimeConversationMode),
			AutoSaveTranscriptOnStop = Preferences.Default.Get(AutoSaveTranscriptOnStopName, true),
			MaxSegmentSeconds = Preferences.Default.Get(MaxSegmentSecondsName, 2.0),
			FixedSegmentSeconds = Preferences.Default.Get(FixedSegmentSecondsName, 6.0),
			CinemaEarlyCommitPercent = Preferences.Default.Get(CinemaEarlyCommitPercentName, 80.0),
			VadPreRollMilliseconds = Preferences.Default.Get(VadPreRollMillisecondsName, 300.0),
			VadSilenceCommitMilliseconds = Preferences.Default.Get(VadSilenceCommitMillisecondsName, 650.0),
			VadMinimumSpeechRms = Preferences.Default.Get(VadMinimumSpeechRmsName, 0.012),
			VadNoiseMultiplier = Preferences.Default.Get(VadNoiseMultiplierName, 3.0),
			SubtitleBackgroundOpacity = Preferences.Default.Get(SubtitleBackgroundOpacityName, 0.72),
			SubtitleFontSize = Preferences.Default.Get(SubtitleFontSizeName, 34.0),
			SubtitleLineHoldSeconds = Preferences.Default.Get(SubtitleLineHoldSecondsName, 1.0),
			SubtitleIdleClearSeconds = Preferences.Default.Get(SubtitleIdleClearSecondsName, 3.0)
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
		Preferences.Default.Set(AutoSaveTranscriptOnStopName, settings.AutoSaveTranscriptOnStop);
		Preferences.Default.Set(MaxSegmentSecondsName, settings.MaxSegmentSeconds);
		Preferences.Default.Set(FixedSegmentSecondsName, settings.FixedSegmentSeconds);
		Preferences.Default.Set(CinemaEarlyCommitPercentName, settings.CinemaEarlyCommitPercent);
		Preferences.Default.Set(VadPreRollMillisecondsName, settings.VadPreRollMilliseconds);
		Preferences.Default.Set(VadSilenceCommitMillisecondsName, settings.VadSilenceCommitMilliseconds);
		Preferences.Default.Set(VadMinimumSpeechRmsName, settings.VadMinimumSpeechRms);
		Preferences.Default.Set(VadNoiseMultiplierName, settings.VadNoiseMultiplier);
		Preferences.Default.Set(SubtitleBackgroundOpacityName, settings.SubtitleBackgroundOpacity);
		Preferences.Default.Set(SubtitleFontSizeName, settings.SubtitleFontSize);
		Preferences.Default.Set(SubtitleLineHoldSecondsName, settings.SubtitleLineHoldSeconds);
		Preferences.Default.Set(SubtitleIdleClearSecondsName, settings.SubtitleIdleClearSeconds);
	}
}
