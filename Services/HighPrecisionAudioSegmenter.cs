namespace WindowsAiTranscriber.Services;

public sealed class HighPrecisionAudioSegmenter(HighPrecisionAudioSegmenterOptions options)
{
	private readonly Queue<byte[]> _preRollChunks = [];
	private readonly List<byte[]> _segmentChunks = [];
	private readonly int _silenceCommitBytes = options.SilenceCommitDuration.ToPcm16ByteCount(options.SampleRate);
	private readonly int _targetWindowBytes = options.TargetWindowDuration.ToPcm16ByteCount(options.SampleRate);
	private readonly int _maxWindowBytes = options.MaxWindowDuration.ToPcm16ByteCount(options.SampleRate);
	private readonly int _overlapBytes = options.OverlapDuration.ToPcm16ByteCount(options.SampleRate);
	private readonly int _preRollCapacityBytes = Math.Max(
		options.PreRollDuration.ToPcm16ByteCount(options.SampleRate),
		options.OverlapDuration.ToPcm16ByteCount(options.SampleRate));
	private double _noiseFloorRms = options.MinimumSpeechRms / 2;
	private int _preRollTotalBytes;
	private int _segmentBytes;
	private int _trailingSilenceBytes;
	private bool _isSpeechActive;

	public HighPrecisionSegmenterResult Process(byte[] pcm16Audio)
	{
		if (pcm16Audio.Length == 0)
		{
			return HighPrecisionSegmenterResult.Empty;
		}

		var rms = CalculateRms(pcm16Audio);
		var threshold = Math.Max(options.MinimumSpeechRms, _noiseFloorRms * options.NoiseMultiplier);
		var hasSpeech = rms >= threshold;

		if (!_isSpeechActive && !hasSpeech)
		{
			UpdateNoiseFloor(rms);
			AddPreRoll(pcm16Audio);
			return HighPrecisionSegmenterResult.Empty with
			{
				State = HighPrecisionSegmenterState.Silence,
				Rms = rms,
				Threshold = threshold
			};
		}

		if (!_isSpeechActive)
		{
			_isSpeechActive = true;
			_trailingSilenceBytes = 0;
			AppendAudio(DrainPreRollAndAppend(pcm16Audio));
			return HighPrecisionSegmenterResult.Empty with
			{
				State = HighPrecisionSegmenterState.SpeechStarted,
				Rms = rms,
				Threshold = threshold
			};
		}

		AppendAudio(pcm16Audio);

		if (hasSpeech)
		{
			_trailingSilenceBytes = 0;
		}
		else
		{
			_trailingSilenceBytes += pcm16Audio.Length;
		}

		var shouldFlushForSilence = _trailingSilenceBytes >= _silenceCommitBytes;
		var shouldFlushForMaxWindow = _segmentBytes >= _maxWindowBytes;
		if (!shouldFlushForSilence && !shouldFlushForMaxWindow)
		{
			return HighPrecisionSegmenterResult.Empty with
			{
				State = hasSpeech ? HighPrecisionSegmenterState.Speech : HighPrecisionSegmenterState.HangoverSilence,
				Rms = rms,
				Threshold = threshold
			};
		}

		var audio = BuildSegmentAudio();
		var reason = shouldFlushForMaxWindow
			? "max_window"
			: _segmentBytes >= _targetWindowBytes ? "silence" : "short_silence";
		ResetAfterFlush(audio, keepOverlapActive: shouldFlushForMaxWindow);
		return new HighPrecisionSegmenterResult(
			audio,
			reason,
			HighPrecisionSegmenterState.Commit,
			rms,
			threshold,
			Pcm16BytesToDuration(audio.Length));
	}

	public HighPrecisionSegmenterResult Flush(string reason)
	{
		if (_segmentBytes == 0)
		{
			return HighPrecisionSegmenterResult.Empty;
		}

		var audio = BuildSegmentAudio();
		Reset();
		return new HighPrecisionSegmenterResult(
			audio,
			reason,
			HighPrecisionSegmenterState.Commit,
			0,
			0,
			Pcm16BytesToDuration(audio.Length));
	}

	public void Reset()
	{
		_preRollChunks.Clear();
		_segmentChunks.Clear();
		_preRollTotalBytes = 0;
		_segmentBytes = 0;
		_trailingSilenceBytes = 0;
		_isSpeechActive = false;
	}

	private void AppendAudio(byte[] pcm16Audio)
	{
		var copy = new byte[pcm16Audio.Length];
		Array.Copy(pcm16Audio, copy, pcm16Audio.Length);
		_segmentChunks.Add(copy);
		_segmentBytes += copy.Length;
	}

	private byte[] BuildSegmentAudio()
	{
		var audio = new byte[_segmentBytes];
		var offset = 0;
		foreach (var chunk in _segmentChunks)
		{
			Array.Copy(chunk, 0, audio, offset, chunk.Length);
			offset += chunk.Length;
		}

		return audio;
	}

	private void ResetAfterFlush(byte[] flushedAudio, bool keepOverlapActive)
	{
		_segmentChunks.Clear();
		_segmentBytes = 0;
		_trailingSilenceBytes = 0;
		_isSpeechActive = keepOverlapActive;
		_preRollChunks.Clear();
		_preRollTotalBytes = 0;

		var overlap = Tail(flushedAudio, _overlapBytes);
		if (overlap.Length == 0)
		{
			return;
		}

		if (keepOverlapActive)
		{
			_segmentChunks.Add(overlap);
			_segmentBytes = overlap.Length;
			return;
		}

		AddPreRoll(overlap);
	}

	private void AddPreRoll(byte[] pcm16Audio)
	{
		var copy = pcm16Audio.Length > _preRollCapacityBytes
			? Tail(pcm16Audio, _preRollCapacityBytes)
			: new byte[pcm16Audio.Length];
		if (copy.Length != pcm16Audio.Length)
		{
			_preRollChunks.Enqueue(copy);
			_preRollTotalBytes += copy.Length;
		}
		else
		{
			Array.Copy(pcm16Audio, copy, pcm16Audio.Length);
			_preRollChunks.Enqueue(copy);
			_preRollTotalBytes += copy.Length;
		}

		while (_preRollTotalBytes > _preRollCapacityBytes && _preRollChunks.TryDequeue(out var removed))
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
		var clampedRms = Math.Min(rms, options.MinimumSpeechRms);
		_noiseFloorRms = (_noiseFloorRms * 0.95) + (clampedRms * 0.05);
	}

	private TimeSpan Pcm16BytesToDuration(int byteCount)
	{
		return TimeSpan.FromSeconds(byteCount / (options.SampleRate * 2.0));
	}

	private static byte[] Tail(byte[] audio, int byteCount)
	{
		if (byteCount <= 0 || audio.Length == 0)
		{
			return [];
		}

		var length = Math.Min(byteCount, audio.Length);
		var output = new byte[length];
		Array.Copy(audio, audio.Length - length, output, 0, length);
		return output;
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

public sealed record HighPrecisionAudioSegmenterOptions(
	int SampleRate,
	double MinimumSpeechRms,
	double NoiseMultiplier,
	TimeSpan PreRollDuration,
	TimeSpan SilenceCommitDuration,
	TimeSpan TargetWindowDuration,
	TimeSpan MaxWindowDuration,
	TimeSpan OverlapDuration);

public sealed record HighPrecisionSegmenterResult(
	byte[] AudioToSubmit,
	string Reason,
	HighPrecisionSegmenterState State,
	double Rms,
	double Threshold,
	TimeSpan Duration)
{
	public static HighPrecisionSegmenterResult Empty { get; } =
		new([], "", HighPrecisionSegmenterState.Silence, 0, 0, TimeSpan.Zero);
}

public enum HighPrecisionSegmenterState
{
	Silence,
	SpeechStarted,
	Speech,
	HangoverSilence,
	Commit
}
