using NAudio.Wave;
using WindowsAiTranscriber.Services;

namespace WindowsAiTranscriber.Platforms.Windows.Audio;

public sealed class WindowsSystemAudioCaptureService : IAudioCaptureService, IDisposable
{
	private WasapiLoopbackCapture? _capture;

	public event EventHandler<AudioChunkEventArgs>? AudioAvailable;

	public event EventHandler<string>? CaptureError;

	public bool IsCapturing => _capture is not null;

	public void Start()
	{
		Stop();

		try
		{
			_capture = new WasapiLoopbackCapture
			{
				ShareMode = NAudio.CoreAudioApi.AudioClientShareMode.Shared
			};
			_capture.DataAvailable += OnDataAvailable;
			_capture.RecordingStopped += OnRecordingStopped;
			_capture.StartRecording();
		}
		catch (Exception ex)
		{
			Stop();
			CaptureError?.Invoke(this, ex.Message);
			throw;
		}
	}

	public void Stop()
	{
		if (_capture is null)
		{
			return;
		}

		try
		{
			_capture.StopRecording();
		}
		catch
		{
		}

		_capture.DataAvailable -= OnDataAvailable;
		_capture.RecordingStopped -= OnRecordingStopped;
		_capture.Dispose();
		_capture = null;
	}

	private void OnDataAvailable(object? sender, WaveInEventArgs e)
	{
		var capture = _capture;
		if (capture is null)
		{
			return;
		}

		try
		{
			var pcm16Audio = AudioResampler.To24KhzMonoPcm16(e.Buffer, e.BytesRecorded, capture.WaveFormat);
			AudioAvailable?.Invoke(this, new AudioChunkEventArgs(pcm16Audio));
		}
		catch (Exception ex)
		{
			CaptureError?.Invoke(this, ex.Message);
		}
	}

	private void OnRecordingStopped(object? sender, StoppedEventArgs e)
	{
		if (e.Exception is not null)
		{
			CaptureError?.Invoke(this, e.Exception.Message);
		}
	}

	public void Dispose()
	{
		Stop();
	}
}
