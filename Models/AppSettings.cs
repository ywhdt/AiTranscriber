namespace WindowsAiTranscriber.Models;

public sealed class AppSettings
{
	public const string RealtimeWhisperModel = "gpt-realtime-whisper";

	public const string Gpt4oTranscribeModel = "gpt-4o-transcribe";

	public const string RealtimeConversationMode = "realtime_conversation";

	public const string CinemaSubtitleMode = "cinema_subtitle";

	public const string NoiseReductionOff = "off";

	public const string NoiseReductionNearField = "near_field";

	public const string NoiseReductionFarField = "far_field";

	public string ApiKey { get; set; } = "";

	public string Model { get; set; } = RealtimeWhisperModel;

	public string Language { get; set; } = "zh";

	public string Prompt { get; set; } = "";

	public string SegmentationMode { get; set; } = RealtimeConversationMode;

	public string NoiseReductionMode { get; set; } = NoiseReductionOff;

	public bool AutoSaveTranscriptOnStop { get; set; } = true;

	public double MaxSegmentSeconds { get; set; } = 12.0;

	public double FixedSegmentSeconds { get; set; } = 6.0;

	public double CinemaEarlyCommitPercent { get; set; } = 80.0;

	public double HighPrecisionTargetWindowSeconds { get; set; } = 6.0;

	public double HighPrecisionMaxWindowSeconds { get; set; } = 8.0;

	public double HighPrecisionOverlapSeconds { get; set; } = 1.2;

	public double VadPreRollMilliseconds { get; set; } = 300;

	public double RealtimeSilenceCommitMilliseconds { get; set; } = 1200;

	public double HighPrecisionSilenceCommitMilliseconds { get; set; } = 1200;

	public double VadMinimumSpeechRms { get; set; } = 0.012;

	public double VadNoiseMultiplier { get; set; } = 3.0;

	public double SubtitleBackgroundOpacity { get; set; } = 0.72;

	public double SubtitleFontSize { get; set; } = 34;

	public double SubtitleLineHoldSeconds { get; set; } = 1.0;

	public double SubtitleIdleClearSeconds { get; set; } = 3.0;

	public bool UsesFixedSegmentMode =>
		string.Equals(SegmentationMode, CinemaSubtitleMode, StringComparison.OrdinalIgnoreCase);

	public bool UsesHighPrecisionSubtitleMode =>
		string.Equals(SegmentationMode, CinemaSubtitleMode, StringComparison.OrdinalIgnoreCase);
}
