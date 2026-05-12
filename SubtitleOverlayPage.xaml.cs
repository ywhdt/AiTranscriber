namespace WindowsAiTranscriber;

public partial class SubtitleOverlayPage : ContentPage
{
	public SubtitleOverlayPage()
	{
		InitializeComponent();
	}

	public void SetText(string text)
	{
		SubtitleLabel.Text = string.IsNullOrWhiteSpace(text)
			? " "
			: text.TrimStart();
	}

	public void ApplyStyle(double fontSize, double backgroundOpacity)
	{
		SubtitleLabel.FontSize = Math.Clamp(fontSize, 18, 96);
		var opacity = Math.Clamp(backgroundOpacity, 0, 1);
		SubtitleContainer.Background = new SolidColorBrush(Color.FromRgba(0, 0, 0, opacity));
	}
}
