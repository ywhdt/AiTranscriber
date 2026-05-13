using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WindowsAiTranscriber.Models;

namespace WindowsAiTranscriber.Services;

public sealed class OpenAIAudioTranscriptionService
{
	private static readonly Uri TranscriptionsUri = new("https://api.openai.com/v1/audio/transcriptions");
	private static readonly HttpClient HttpClient = new()
	{
		Timeout = TimeSpan.FromSeconds(15)
	};

	public async Task<string> TranscribeAsync(
		byte[] pcm16Audio,
		AppSettings settings,
		CancellationToken cancellationToken)
	{
		if (pcm16Audio.Length == 0)
		{
			return "";
		}

		using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUri);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
		request.Headers.UserAgent.ParseAdd("WindowsAiTranscriber/0.1");

		using var form = new MultipartFormDataContent();
		form.Add(new StringContent(AppSettings.Gpt4oTranscribeModel, Encoding.UTF8), "model");
		form.Add(new StringContent("json", Encoding.UTF8), "response_format");

		if (!string.IsNullOrWhiteSpace(settings.Language))
		{
			form.Add(new StringContent(settings.Language.Trim(), Encoding.UTF8), "language");
		}

		if (!string.IsNullOrWhiteSpace(settings.Prompt))
		{
			form.Add(new StringContent(settings.Prompt.Trim(), Encoding.UTF8), "prompt");
		}

		var wavBytes = BuildPcm16Wav(pcm16Audio, 24000, channels: 1);
		using var audioContent = new ByteArrayContent(wavBytes);
		audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
		form.Add(audioContent, "file", $"segment-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.wav");
		request.Content = form;

		using var response = await HttpClient.SendAsync(request, cancellationToken);
		var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"Audio Transcriptions 请求失败：{(int)response.StatusCode} {responseText}");
		}

		using var document = JsonDocument.Parse(responseText);
		return document.RootElement.TryGetProperty("text", out var text)
			? TranscriptionTextCleaner.CleanCompleted(text.GetString()).Trim()
			: "";
	}

	private static byte[] BuildPcm16Wav(byte[] pcm16Audio, int sampleRate, short channels)
	{
		const short bitsPerSample = 16;
		const short audioFormatPcm = 1;
		var byteRate = sampleRate * channels * bitsPerSample / 8;
		var blockAlign = (short)(channels * bitsPerSample / 8);
		var fileSizeMinusEight = 36 + pcm16Audio.Length;

		using var stream = new MemoryStream(44 + pcm16Audio.Length);
		using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
		writer.Write("RIFF"u8.ToArray());
		writer.Write(fileSizeMinusEight);
		writer.Write("WAVE"u8.ToArray());
		writer.Write("fmt "u8.ToArray());
		writer.Write(16);
		writer.Write(audioFormatPcm);
		writer.Write(channels);
		writer.Write(sampleRate);
		writer.Write(byteRate);
		writer.Write(blockAlign);
		writer.Write(bitsPerSample);
		writer.Write("data"u8.ToArray());
		writer.Write(pcm16Audio.Length);
		writer.Write(pcm16Audio);
		writer.Flush();
		return stream.ToArray();
	}
}
