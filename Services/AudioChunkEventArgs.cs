namespace WindowsAiTranscriber.Services;

public sealed class AudioChunkEventArgs(byte[] pcm16Audio) : EventArgs
{
	public byte[] Pcm16Audio { get; } = pcm16Audio;
}
