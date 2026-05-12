namespace WindowsAiTranscriber.Models;

public sealed class TranscriptSegment
{
	public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

	public string Text { get; set; } = "";
}
