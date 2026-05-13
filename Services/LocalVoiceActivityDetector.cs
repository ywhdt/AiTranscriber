namespace WindowsAiTranscriber.Services;

public sealed class LocalVoiceActivityDetector(LocalVadOptions options)
{
	private readonly object _sensitivityLock = new();
	private readonly Queue<byte[]> _preRollChunks = [];
	private readonly int _preRollBytes = options.PreRollDuration.ToPcm16ByteCount(options.SampleRate);
	private readonly int _silenceCommitBytes = options.SilenceCommitDuration.ToPcm16ByteCount(options.SampleRate);
	private readonly int _minimumSilenceCommitBytes = options.MinimumSilenceCommitDuration.ToPcm16ByteCount(options.SampleRate);
	private readonly int _maxSegmentBytes = options.MaxSegmentDuration.ToPcm16ByteCount(options.SampleRate);
	private double _minimumSpeechRms = Math.Max(0, options.MinimumSpeechRms);
	private double _noiseMultiplier = Math.Max(0, options.NoiseMultiplier);
	private double _noiseFloorRms = options.MinimumSpeechRms / 2;
	private int _preRollTotalBytes;
	private int _currentSegmentBytes;
	private int _trailingSilenceBytes;
	private bool _isSpeechActive;

	public LocalVadResult Process(byte[] pcm16Audio)
	{
		if (pcm16Audio.Length == 0)
		{
			return LocalVadResult.Empty;
		}

		var rms = CalculateRms(pcm16Audio);
		var threshold = CalculateThreshold();
		var hasSpeech = rms >= threshold;

		if (!_isSpeechActive && !hasSpeech)
		{
			UpdateNoiseFloor(rms);
			AddPreRoll(pcm16Audio);
			return LocalVadResult.Empty with
			{
				State = LocalVadState.Silence,
				Rms = rms,
				Threshold = threshold
			};
		}

		if (!_isSpeechActive)
		{
			_isSpeechActive = true;
			_trailingSilenceBytes = 0;
			var audioToSend = DrainPreRollAndAppend(pcm16Audio);
			_currentSegmentBytes += audioToSend.Length;

			return new LocalVadResult(audioToSend, false, LocalVadState.SpeechStarted, rms, threshold);
		}

		_currentSegmentBytes += pcm16Audio.Length;

		if (hasSpeech)
		{
			_trailingSilenceBytes = 0;
		}
		else
		{
			_trailingSilenceBytes += pcm16Audio.Length;
		}

		var shouldCommit = _currentSegmentBytes >= _maxSegmentBytes ||
			options.CommitOnSilence &&
			_currentSegmentBytes >= _minimumSilenceCommitBytes &&
			_trailingSilenceBytes >= _silenceCommitBytes;
		var state = hasSpeech ? LocalVadState.Speech : LocalVadState.HangoverSilence;

		if (shouldCommit)
		{
			ResetSegment();
			state = LocalVadState.Commit;
		}

		return new LocalVadResult(pcm16Audio, shouldCommit, state, rms, threshold);
	}

	public bool HasActiveSegment => _isSpeechActive || _currentSegmentBytes > 0;

	public void UpdateSensitivity(double minimumSpeechRms, double noiseMultiplier)
	{
		lock (_sensitivityLock)
		{
			_minimumSpeechRms = Math.Max(0, minimumSpeechRms);
			_noiseMultiplier = Math.Max(0, noiseMultiplier);
			_noiseFloorRms = Math.Min(_noiseFloorRms, _minimumSpeechRms);
		}
	}

	public void Reset()
	{
		_preRollChunks.Clear();
		_preRollTotalBytes = 0;
		_currentSegmentBytes = 0;
		_trailingSilenceBytes = 0;
		_isSpeechActive = false;
	}

	private void ResetSegment()
	{
		_currentSegmentBytes = 0;
		_trailingSilenceBytes = 0;
		_isSpeechActive = false;
		_preRollChunks.Clear();
		_preRollTotalBytes = 0;
	}

	private void AddPreRoll(byte[] pcm16Audio)
	{
		var copy = new byte[pcm16Audio.Length];
		Array.Copy(pcm16Audio, copy, pcm16Audio.Length);
		_preRollChunks.Enqueue(copy);
		_preRollTotalBytes += copy.Length;

		while (_preRollTotalBytes > _preRollBytes && _preRollChunks.TryDequeue(out var removed))
		{
			_preRollTotalBytes -= removed.Length;
		}
	}

	private byte[] DrainPreRollAndAppend(byte[] pcm16Audio)
	{
		var totalLength = _preRollTotalBytes + pcm16Audio.Length;
		var output = new byte[totalLength];
		var offset = 0;

		while (_preRollChunks.TryDequeue(out var chunk))
		{
			Array.Copy(chunk, 0, output, offset, chunk.Length);
			offset += chunk.Length;
		}

		Array.Copy(pcm16Audio, 0, output, offset, pcm16Audio.Length);
		_preRollTotalBytes = 0;
		return output;
	}

	private void UpdateNoiseFloor(double rms)
	{
		lock (_sensitivityLock)
		{
			var clampedRms = Math.Min(rms, _minimumSpeechRms);
			_noiseFloorRms = (_noiseFloorRms * 0.95) + (clampedRms * 0.05);
		}
	}

	private double CalculateThreshold()
	{
		lock (_sensitivityLock)
		{
			return Math.Max(_minimumSpeechRms, _noiseFloorRms * _noiseMultiplier);
		}
	}

	private static double CalculateRms(byte[] pcm16Audio)
	{
		if (pcm16Audio.Length < 2)
		{
			return 0;
		}

		double sumSquares = 0;
		var sampleCount = pcm16Audio.Length / 2;

		for (var i = 0; i < pcm16Audio.Length - 1; i += 2)
		{
			var sample = (short)(pcm16Audio[i] | (pcm16Audio[i + 1] << 8));
			var normalized = sample / 32768.0;
			sumSquares += normalized * normalized;
		}

		return Math.Sqrt(sumSquares / sampleCount);
	}
}

public sealed record LocalVadOptions(
	int SampleRate,
	double MinimumSpeechRms,
	double NoiseMultiplier,
	TimeSpan PreRollDuration,
	TimeSpan SilenceCommitDuration,
	TimeSpan MinimumSilenceCommitDuration,
	TimeSpan MaxSegmentDuration,
	bool CommitOnSilence);

public sealed record LocalVadResult(
	byte[] AudioToSend,
	bool ShouldCommit,
	LocalVadState State,
	double Rms,
	double Threshold)
{
	public static LocalVadResult Empty { get; } = new([], false, LocalVadState.Silence, 0, 0);
}

public enum LocalVadState
{
	Silence,
	SpeechStarted,
	Speech,
	HangoverSilence,
	Commit
}

internal static class TimeSpanAudioExtensions
{
	public static int ToPcm16ByteCount(this TimeSpan duration, int sampleRate)
	{
		return Math.Max(0, (int)Math.Round(duration.TotalSeconds * sampleRate * 2));
	}
}
