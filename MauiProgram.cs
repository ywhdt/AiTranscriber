using Microsoft.Extensions.Logging;
using WindowsAiTranscriber.Platforms.Windows.Audio;
using WindowsAiTranscriber.Services;

namespace WindowsAiTranscriber;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif
		builder.Services.AddSingleton<AppSettingsService>();
		builder.Services.AddSingleton<TranscriptStore>();
		builder.Services.AddSingleton<SubtitleOverlayService>();
		builder.Services.AddSingleton<OpenAIRealtimeTranscriptionService>();
		builder.Services.AddSingleton<IAudioCaptureService, WindowsSystemAudioCaptureService>();
		builder.Services.AddSingleton<MainPage>();

		return builder.Build();
	}
}
