using NAudio.Wave;

namespace WindowsAiTranscriber.Platforms.Windows.Audio;

public static class AudioResampler
{
	private static readonly WaveFormat TargetFormat = new(24000, 16, 1);

	public static byte[] To24KhzMonoPcm16(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
	{
		if (bytesRecorded <= 0)
		{
			return [];
		}

		if (sourceFormat.SampleRate == TargetFormat.SampleRate &&
			sourceFormat.Channels == TargetFormat.Channels &&
			sourceFormat.BitsPerSample == TargetFormat.BitsPerSample &&
			sourceFormat.Encoding == WaveFormatEncoding.Pcm)
		{
			var exactCopy = new byte[bytesRecorded];
			Array.Copy(buffer, exactCopy, bytesRecorded);
			return exactCopy;
		}

		using var inputMemory = new MemoryStream(buffer, 0, bytesRecorded, writable: false);
		using var inputStream = new RawSourceWaveStream(inputMemory, sourceFormat);
		using var resampler = new MediaFoundationResampler(inputStream, TargetFormat)
		{
			ResamplerQuality = 60
		};
		using var output = new MemoryStream();
		var temp = new byte[8192];
		int read;

		while ((read = resampler.Read(temp, 0, temp.Length)) > 0)
		{
			output.Write(temp, 0, read);
		}

		return output.ToArray();
	}
}
