namespace WindowsAiTranscriber.Services;

public interface IAudioCaptureService
{
	event EventHandler<AudioChunkEventArgs>? AudioAvailable;

	event EventHandler<string>? CaptureError;

	bool IsCapturing { get; }

	void Start();

	void Stop();
}
