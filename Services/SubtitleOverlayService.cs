using System.Globalization;
using System.Text;
using System.Windows.Input;
using WindowsAiTranscriber.Models;
using WpfBorder = System.Windows.Controls.Border;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCornerRadius = System.Windows.CornerRadius;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfFontWeights = System.Windows.FontWeights;
using WpfFormattedText = System.Windows.Media.FormattedText;
using WpfGrid = System.Windows.Controls.Grid;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfResizeMode = System.Windows.ResizeMode;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfSystemParameters = System.Windows.SystemParameters;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextAlignment = System.Windows.TextAlignment;
using WpfThickness = System.Windows.Thickness;
using WpfTypeface = System.Windows.Media.Typeface;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;
using WpfVisualTreeHelper = System.Windows.Media.VisualTreeHelper;
using WpfWindow = System.Windows.Window;
using WpfWindowStyle = System.Windows.WindowStyle;

namespace WindowsAiTranscriber.Services;

public sealed class SubtitleOverlayService
{
	private const double RootHorizontalMargin = 28;
	private const double TextWidthSafetyMargin = 14;
	private const double MinimumTextWidth = 120;

	private readonly Queue<char> _pendingChars = [];
	private readonly StringBuilder _visibleLine = new();
	private WpfWindow? _window;
	private WpfBorder? _container;
	private WpfTextBlock? _subtitleText;
	private AppSettings _settings = new();
	private bool _isHoldingFullLine;
	private bool _isProcessScheduled;
	private int _idleClearGeneration;

	public bool IsOpen => _window is not null;

	public void Open(AppSettings settings)
	{
		_settings = settings;

		if (_window is not null)
		{
			ApplySettings(settings);
			ScheduleProcessQueue();
			return;
		}

		_window = CreateWindow();
		PlaceNearBottom(_window);
		_window.Closed += (_, _) =>
		{
			_window = null;
			_container = null;
			_subtitleText = null;
			ResetBuffer();
			_idleClearGeneration++;
		};

		_window.Show();
		ApplySettings(settings);
		ScheduleProcessQueue();
	}

	public void Close()
	{
		var window = _window;
		if (window is null)
		{
			return;
		}

		ResetBuffer();
		window.Close();
	}

	public void AppendText(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		var window = _window;
		if (window is null)
		{
			return;
		}

		window.Dispatcher.BeginInvoke(() =>
		{
			foreach (var character in text)
			{
				if (character is '\r' or '\n')
				{
					continue;
				}

				_pendingChars.Enqueue(character);
			}

			ProcessQueue();
			ScheduleIdleClear();
		});
	}

	public void Clear()
	{
		var window = _window;
		if (window is null)
		{
			ResetBuffer();
			return;
		}

		window.Dispatcher.BeginInvoke(() =>
		{
			ResetBuffer();
			_idleClearGeneration++;
			SetDisplayedText("");
		});
	}

	public void ApplySettings(AppSettings settings)
	{
		_settings = settings;

		var window = _window;
		if (window is null)
		{
			return;
		}

		window.Dispatcher.BeginInvoke(() =>
		{
			if (_subtitleText is not null)
			{
				_subtitleText.FontSize = Math.Clamp(settings.SubtitleFontSize, 18, 96);
			}

			if (_container is not null)
			{
				var alpha = (byte)Math.Round(Math.Clamp(settings.SubtitleBackgroundOpacity, 0, 1) * 255);
				_container.Background = new WpfSolidColorBrush(WpfColor.FromArgb(alpha, 0, 0, 0));
			}

			ProcessQueue();
		});
	}

	private WpfWindow CreateWindow()
	{
		_subtitleText = new WpfTextBlock
		{
			Text = " ",
			Foreground = WpfBrushes.White,
			FontWeight = WpfFontWeights.Bold,
			FontSize = 34,
			TextAlignment = WpfTextAlignment.Center,
			VerticalAlignment = WpfVerticalAlignment.Center,
			HorizontalAlignment = WpfHorizontalAlignment.Center
		};

		_container = new WpfBorder
		{
			CornerRadius = new WpfCornerRadius(8),
			Padding = new WpfThickness(24, 12, 24, 12),
			Background = new WpfSolidColorBrush(WpfColor.FromArgb(184, 0, 0, 0)),
			Child = _subtitleText,
			VerticalAlignment = WpfVerticalAlignment.Center,
			HorizontalAlignment = WpfHorizontalAlignment.Stretch
		};

		var root = new WpfGrid
		{
			Background = WpfBrushes.Transparent,
			Margin = new WpfThickness(14, 8, 14, 8)
		};
		root.Children.Add(_container);

		var window = new WpfWindow
		{
			Title = "字幕条",
			Width = 1180,
			Height = 132,
			MinWidth = 520,
			MinHeight = 96,
			WindowStyle = WpfWindowStyle.None,
			AllowsTransparency = true,
			Background = WpfBrushes.Transparent,
			Topmost = true,
			ShowInTaskbar = false,
			ResizeMode = WpfResizeMode.CanResizeWithGrip,
			Content = root
		};

		root.MouseLeftButtonDown += (_, e) => DragWindow(window, e);
		_container.MouseLeftButtonDown += (_, e) => DragWindow(window, e);
		_subtitleText.MouseLeftButtonDown += (_, e) => DragWindow(window, e);

		return window;
	}

	private static void DragWindow(WpfWindow window, MouseButtonEventArgs e)
	{
		if (e.ButtonState != MouseButtonState.Pressed)
		{
			return;
		}

		try
		{
			window.DragMove();
		}
		catch
		{
		}
	}

	private void ScheduleProcessQueue()
	{
		var window = _window;
		if (window is null || _isProcessScheduled)
		{
			return;
		}

		_isProcessScheduled = true;
		window.Dispatcher.BeginInvoke(() =>
		{
			_isProcessScheduled = false;
			ProcessQueue();
		});
	}

	private void ProcessQueue()
	{
		if (_window is null || _isHoldingFullLine)
		{
			return;
		}

		var availableWidth = GetAvailableTextWidth();

		while (_pendingChars.Count > 0)
		{
			var next = _pendingChars.Peek();
			_visibleLine.Append(next);

			if (!DoesTextFit(_visibleLine.ToString(), availableWidth))
			{
				_visibleLine.Length--;

				if (_visibleLine.Length > 0)
				{
					HoldCurrentLineThenAdvance();
					return;
				}

				_pendingChars.Dequeue();
				_visibleLine.Append(next);
				break;
			}

			_pendingChars.Dequeue();
		}

		SetDisplayedText(_visibleLine.ToString());
	}

	private async void HoldCurrentLineThenAdvance()
	{
		if (_window is null || _isHoldingFullLine || _visibleLine.Length == 0)
		{
			return;
		}

		_isHoldingFullLine = true;
		SetDisplayedText(_visibleLine.ToString());

		try
		{
			await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_settings.SubtitleLineHoldSeconds, 0.2, 10)));
		}
		finally
		{
			_visibleLine.Clear();
			_isHoldingFullLine = false;
			SetDisplayedText("");
			ProcessQueue();
		}
	}

	private void SetDisplayedText(string text)
	{
		if (_subtitleText is null)
		{
			return;
		}

		_subtitleText.Text = NormalizeDisplayedText(text);
	}

	private void ResetBuffer()
	{
		_pendingChars.Clear();
		_visibleLine.Clear();
		_isHoldingFullLine = false;
	}

	private async void ScheduleIdleClear()
	{
		var window = _window;
		if (window is null)
		{
			return;
		}

		var generation = ++_idleClearGeneration;
		var delay = TimeSpan.FromSeconds(Math.Clamp(_settings.SubtitleIdleClearSeconds, 1, 60));
		await Task.Delay(delay);

		var currentWindow = _window;
		if (currentWindow is null || generation != _idleClearGeneration)
		{
			return;
		}

		_ = currentWindow.Dispatcher.BeginInvoke(() =>
		{
			if (generation != _idleClearGeneration)
			{
				return;
			}

			ResetBuffer();
			SetDisplayedText("");
		});
	}

	private double GetAvailableTextWidth()
	{
		var containerWidth = _container?.ActualWidth > 0 ? _container.ActualWidth : 0;
		if (containerWidth <= 0)
		{
			var windowWidth = _window?.ActualWidth > 0 ? _window.ActualWidth : _window?.Width ?? 1180;
			containerWidth = Math.Max(0, windowWidth - RootHorizontalMargin);
		}

		var horizontalPadding = _container is null
			? 48
			: _container.Padding.Left + _container.Padding.Right;

		return Math.Max(MinimumTextWidth, containerWidth - horizontalPadding - TextWidthSafetyMargin);
	}

	private bool DoesTextFit(string text, double availableWidth)
	{
		var measuredWidth = MeasureTextWidth(text);
		if (measuredWidth is not null)
		{
			return measuredWidth.Value <= availableWidth;
		}

		return EstimateTextWidth(text) <= availableWidth;
	}

	private double? MeasureTextWidth(string text)
	{
		var subtitleText = _subtitleText;
		if (subtitleText is null)
		{
			return null;
		}

		try
		{
			var displayedText = NormalizeDisplayedText(text);
			var pixelsPerDip = WpfVisualTreeHelper.GetDpi(subtitleText).PixelsPerDip;
			var typeface = new WpfTypeface(
				subtitleText.FontFamily,
				subtitleText.FontStyle,
				subtitleText.FontWeight,
				subtitleText.FontStretch);
			var formattedText = new WpfFormattedText(
				displayedText,
				CultureInfo.CurrentUICulture,
				WpfFlowDirection.LeftToRight,
				typeface,
				subtitleText.FontSize,
				subtitleText.Foreground,
				pixelsPerDip);

			return formattedText.WidthIncludingTrailingWhitespace;
		}
		catch
		{
			return null;
		}
	}

	private double EstimateTextWidth(string text)
	{
		var fontSize = Math.Clamp(_settings.SubtitleFontSize, 18, 96);
		var units = 0.0;

		foreach (var character in NormalizeDisplayedText(text))
		{
			units += EstimateCharacterUnits(character);
		}

		return units * fontSize * 0.92;
	}

	private static double EstimateCharacterUnits(char character)
	{
		if (char.IsWhiteSpace(character))
		{
			return 0.35;
		}

		return IsCjk(character) ? 1.0 : 0.55;
	}

	private static bool IsCjk(char character)
	{
		return character is >= '\u4E00' and <= '\u9FFF'
			or >= '\u3400' and <= '\u4DBF'
			or >= '\u3040' and <= '\u30FF'
			or >= '\uAC00' and <= '\uD7AF'
			or >= '\uFF00' and <= '\uFFEF';
	}

	private static string NormalizeDisplayedText(string text)
	{
		return string.IsNullOrWhiteSpace(text) ? " " : text.TrimStart();
	}

	private static void PlaceNearBottom(WpfWindow window)
	{
		try
		{
			var screenWidth = WpfSystemParameters.PrimaryScreenWidth;
			var screenHeight = WpfSystemParameters.PrimaryScreenHeight;

			window.Left = Math.Max(0, (screenWidth - window.Width) / 2);
			window.Top = Math.Max(0, screenHeight - window.Height - 96);
		}
		catch
		{
		}
	}
}
