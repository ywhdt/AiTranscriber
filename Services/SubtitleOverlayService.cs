using System.Text;
using System.Windows.Input;
using WindowsAiTranscriber.Models;
using WpfBorder = System.Windows.Controls.Border;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCornerRadius = System.Windows.CornerRadius;
using WpfFontWeights = System.Windows.FontWeights;
using WpfGrid = System.Windows.Controls.Grid;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfResizeMode = System.Windows.ResizeMode;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfSystemParameters = System.Windows.SystemParameters;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextAlignment = System.Windows.TextAlignment;
using WpfThickness = System.Windows.Thickness;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;
using WpfWindow = System.Windows.Window;
using WpfWindowStyle = System.Windows.WindowStyle;

namespace WindowsAiTranscriber.Services;

public sealed class SubtitleOverlayService
{
	private readonly Queue<char> _pendingChars = [];
	private readonly StringBuilder _visibleLine = new();
	private WpfWindow? _window;
	private WpfBorder? _container;
	private WpfTextBlock? _subtitleText;
	private AppSettings _settings = new();
	private bool _isHoldingFullLine;
	private bool _isProcessScheduled;
	private int _idleClearGeneration;
	private double _visibleLineUnits;

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

		var maxUnits = EstimateMaxLineUnits();

		while (_pendingChars.Count > 0)
		{
			var next = _pendingChars.Peek();
			var nextUnits = EstimateCharacterUnits(next);

			if (_visibleLine.Length > 0 && _visibleLineUnits + nextUnits > maxUnits)
			{
				HoldCurrentLineThenAdvance();
				return;
			}

			_pendingChars.Dequeue();
			_visibleLine.Append(next);
			_visibleLineUnits += nextUnits;

			if (_visibleLineUnits >= maxUnits)
			{
				break;
			}
		}

		SetDisplayedText(_visibleLine.ToString());

		if (_visibleLineUnits >= maxUnits && _visibleLine.Length > 0)
		{
			HoldCurrentLineThenAdvance();
		}
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
			_visibleLineUnits = 0;
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

		_subtitleText.Text = string.IsNullOrWhiteSpace(text) ? " " : text.TrimStart();
	}

	private void ResetBuffer()
	{
		_pendingChars.Clear();
		_visibleLine.Clear();
		_visibleLineUnits = 0;
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

	private double EstimateMaxLineUnits()
	{
		var width = _window?.ActualWidth > 0 ? _window.ActualWidth : _window?.Width ?? 1180;
		var fontSize = Math.Clamp(_settings.SubtitleFontSize, 18, 96);
		var availableWidth = Math.Max(240, width - 96);
		return Math.Max(8, availableWidth / (fontSize * 0.92));
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
