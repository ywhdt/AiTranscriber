using System.Text;
using WindowsAiTranscriber.Models;

namespace WindowsAiTranscriber.Services;

public sealed class TranscriptStore
{
	public async Task<string> SaveAsync(IReadOnlyCollection<TranscriptSegment> segments)
	{
		var folder = Path.Combine(FileSystem.AppDataDirectory, "Transcripts");
		Directory.CreateDirectory(folder);

		var path = Path.Combine(folder, $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
		var builder = new StringBuilder();

		foreach (var segment in segments)
		{
			builder.Append('[')
				.Append(segment.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
				.Append("] ")
				.AppendLine(segment.Text.Trim());
		}

		await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
		return path;
	}
}
