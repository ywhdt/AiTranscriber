using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAiTranscriber.Models;

namespace WindowsAiTranscriber.Services;

public sealed class OpenAIRealtimeTranscriptionService : IAsyncDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly HashSet<string> _completedItemIds = [];
	private ClientWebSocket? _socket;
	private CancellationTokenSource? _receiveCts;
	private Task? _receiveTask;
	private string? _eventLogPath;
	private string _rawPartialText = "";
	private string _publishedPartialText = "";

	public event EventHandler<string>? StatusChanged;

	public event EventHandler<string>? ErrorOccurred;

	public event EventHandler<string>? PartialTranscriptReceived;

	public event EventHandler<TranscriptSegment>? TranscriptCompleted;

	public bool IsConnected => _socket?.State == WebSocketState.Open;

	public string EventLogPath
	{
		get
		{
			_eventLogPath ??= Path.Combine(FileSystem.AppDataDirectory, "realtime-events.log");
			return _eventLogPath;
		}
	}

	public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
	{
		await StopAsync();

		if (string.IsNullOrWhiteSpace(settings.ApiKey))
		{
			throw new InvalidOperationException("OpenAI API Key 不能为空。");
		}

		_completedItemIds.Clear();
		ResetPartialState();
		ResetEventLog();
		_socket = new ClientWebSocket();
		_socket.Options.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
		_socket.Options.SetRequestHeader("User-Agent", "WindowsAiTranscriber/0.1");

		await _socket.ConnectAsync(BuildRealtimeUri(), cancellationToken);

		_receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);

		await SendSessionUpdateAsync(settings, cancellationToken);
		StatusChanged?.Invoke(this, "实时转写连接已建立。");
	}

	public async Task SendAudioAsync(byte[] pcm16Audio, CancellationToken cancellationToken)
	{
		if (pcm16Audio.Length == 0 || !IsConnected)
		{
			return;
		}

		await SendJsonAsync(new
		{
			type = "input_audio_buffer.append",
			audio = Convert.ToBase64String(pcm16Audio)
		}, cancellationToken);
	}

	public void LogClientEvent(string type, object payload)
	{
		try
		{
			var json = JsonSerializer.Serialize(payload, JsonOptions);
			File.AppendAllText(EventLogPath, $"[{DateTimeOffset.Now:O}] {type}: {json}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	public Task CommitAudioAsync(CancellationToken cancellationToken)
	{
		return SendJsonAsync(new
		{
			type = "input_audio_buffer.commit"
		}, cancellationToken);
	}

	public async Task StopAsync()
	{
		_receiveCts?.Cancel();

		if (_socket is { State: WebSocketState.Open } socket)
		{
			try
			{
				await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopped", CancellationToken.None);
			}
			catch
			{
			}
		}

		if (_receiveTask is not null)
		{
			try
			{
				await _receiveTask;
			}
			catch
			{
			}
		}

		_receiveCts?.Dispose();
		_receiveCts = null;
		_receiveTask = null;
		_socket?.Dispose();
		_socket = null;
	}

	private Task SendSessionUpdateAsync(AppSettings settings, CancellationToken cancellationToken)
	{
		var transcription = BuildTranscriptionConfig(settings);

		return SendJsonAsync(new
		{
			type = "session.update",
			session = new
			{
				type = "transcription",
				audio = new
				{
					input = new
					{
						format = new
						{
							type = "audio/pcm",
							rate = 24000
						},
						transcription,
						turn_detection = (object?)null,
						noise_reduction = new
						{
							type = "near_field"
						}
					}
				}
			}
		}, cancellationToken);
	}

	private static Dictionary<string, object?> BuildTranscriptionConfig(AppSettings settings)
	{
		var transcription = new Dictionary<string, object?>
		{
			["model"] = settings.Model
		};

		if (!string.IsNullOrWhiteSpace(settings.Language))
		{
			transcription["language"] = settings.Language.Trim();
		}

		if (!string.IsNullOrWhiteSpace(settings.Prompt))
		{
			transcription["prompt"] = settings.Prompt.Trim();
		}

		return transcription;
	}

	private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
	{
		var socket = _socket;
		if (socket is null || socket.State != WebSocketState.Open)
		{
			return;
		}

		var json = JsonSerializer.Serialize(payload, JsonOptions);
		var bytes = Encoding.UTF8.GetBytes(json);

		await _sendLock.WaitAsync(cancellationToken);
		try
		{
			await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
	{
		var buffer = new byte[16 * 1024];

		while (!cancellationToken.IsCancellationRequested && _socket is { State: WebSocketState.Open } socket)
		{
			using var message = new MemoryStream();
			WebSocketReceiveResult result;

			do
			{
				result = await socket.ReceiveAsync(buffer, cancellationToken);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					return;
				}

				message.Write(buffer, 0, result.Count);
			}
			while (!result.EndOfMessage);

			var json = Encoding.UTF8.GetString(message.ToArray());
			LogServerEvent(json);
			HandleServerEvent(json);
		}
	}

	private void HandleServerEvent(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (!root.TryGetProperty("type", out var typeProperty))
			{
				return;
			}

			var type = typeProperty.GetString();
			switch (type)
			{
				case "session.created":
				case "transcription_session.created":
					StatusChanged?.Invoke(this, "转写会话已创建。");
					break;
				case "session.updated":
					StatusChanged?.Invoke(this, "转写参数已生效。");
					break;
				case "input_audio_buffer.speech_started":
					StatusChanged?.Invoke(this, "检测到声音。");
					break;
				case "input_audio_buffer.speech_stopped":
					StatusChanged?.Invoke(this, "正在整理这一段文字。");
					break;
				case "input_audio_buffer.committed":
					StatusChanged?.Invoke(this, "正在转写最近一小段音频。");
					break;
				case "conversation.item.input_audio_transcription.delta":
					RaisePartial(ReadString(root, "delta"));
					break;
				case "conversation.item.input_audio_transcription.completed":
					RaiseCompleted(ReadString(root, "transcript"), ReadString(root, "item_id"));
					break;
				case "conversation.item.input_audio_transcription.failed":
					ResetPartialState();
					ErrorOccurred?.Invoke(this, ReadNestedError(root));
					break;
				case "conversation.item.input_audio_transcription.segment":
					RaiseCompleted(ReadString(root, "text"), BuildSegmentId(root));
					break;
				case "conversation.item.done":
					RaiseCompleted(ReadTranscriptFromItem(root), ReadNestedItemId(root));
					break;
				case "transcript.text.delta":
					RaisePartial(ReadString(root, "delta"));
					break;
				case "transcript.text.done":
				case "transcript.text.segment":
					RaiseCompleted(ReadString(root, "text"), BuildSegmentId(root));
					break;
				case "error":
					ErrorOccurred?.Invoke(this, ReadError(root));
					break;
			}
		}
		catch (JsonException ex)
		{
			ErrorOccurred?.Invoke(this, $"无法解析服务端消息：{ex.Message}");
		}
	}

	private void RaisePartial(string delta)
	{
		if (string.IsNullOrEmpty(delta))
		{
			return;
		}

		_rawPartialText += delta;
		var cleanedPartialText = TranscriptionTextCleaner.CleanPartial(_rawPartialText);
		if (string.IsNullOrEmpty(cleanedPartialText))
		{
			return;
		}

		if (!cleanedPartialText.StartsWith(_publishedPartialText, StringComparison.Ordinal))
		{
			var cleanedDelta = TranscriptionTextCleaner.CleanCompleted(delta);
			if (string.IsNullOrEmpty(cleanedDelta))
			{
				return;
			}

			_publishedPartialText += cleanedDelta;
			PartialTranscriptReceived?.Invoke(this, cleanedDelta);
			return;
		}

		var visibleDelta = cleanedPartialText[_publishedPartialText.Length..];
		if (string.IsNullOrEmpty(visibleDelta))
		{
			return;
		}

		_publishedPartialText = cleanedPartialText;
		PartialTranscriptReceived?.Invoke(this, visibleDelta);
	}

	private void RaiseCompleted(string transcript, string itemId)
	{
		var cleanedTranscript = TranscriptionTextCleaner.CleanCompleted(transcript).Trim();
		if (string.IsNullOrWhiteSpace(cleanedTranscript))
		{
			ResetPartialState();
			return;
		}

		if (!string.IsNullOrWhiteSpace(itemId) && !_completedItemIds.Add(itemId))
		{
			ResetPartialState();
			return;
		}

		TranscriptCompleted?.Invoke(this, new TranscriptSegment
		{
			StartedAt = DateTimeOffset.Now,
			Text = cleanedTranscript
		});
		ResetPartialState();
	}

	private void LogServerEvent(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var type = document.RootElement.TryGetProperty("type", out var typeProperty)
				? typeProperty.GetString()
				: "unknown";
			File.AppendAllText(EventLogPath, $"[{DateTimeOffset.Now:O}] {type}: {json}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private static string ReadString(JsonElement root, string propertyName)
	{
		return root.TryGetProperty(propertyName, out var property)
			? property.GetString() ?? ""
			: "";
	}

	private static string ReadError(JsonElement root)
	{
		if (root.ValueKind == JsonValueKind.Object &&
			root.TryGetProperty("message", out var directMessage))
		{
			return directMessage.GetString() ?? "未知错误";
		}

		if (root.TryGetProperty("error", out var error) &&
			error.ValueKind == JsonValueKind.Object &&
			error.TryGetProperty("message", out var message))
		{
			return message.GetString() ?? "未知错误";
		}

		return root.ToString();
	}

	private void ResetEventLog()
	{
		try
		{
			var folder = Path.GetDirectoryName(EventLogPath);
			if (!string.IsNullOrWhiteSpace(folder))
			{
				Directory.CreateDirectory(folder);
			}

			File.WriteAllText(EventLogPath, "");
		}
		catch
		{
		}
	}

	private void ResetPartialState()
	{
		_rawPartialText = "";
		_publishedPartialText = "";
	}

	private static string ReadNestedError(JsonElement root)
	{
		return root.TryGetProperty("error", out var error)
			? ReadError(error)
			: root.ToString();
	}

	private static Uri BuildRealtimeUri()
	{
		return new Uri("wss://api.openai.com/v1/realtime?intent=transcription");
	}

	private static string BuildSegmentId(JsonElement root)
	{
		var itemId = ReadString(root, "item_id");
		var segmentId = ReadString(root, "id");
		return string.IsNullOrWhiteSpace(segmentId) ? itemId : $"{itemId}:{segmentId}";
	}

	private static string ReadNestedItemId(JsonElement root)
	{
		if (root.TryGetProperty("item", out var item) &&
			item.ValueKind == JsonValueKind.Object &&
			item.TryGetProperty("id", out var id))
		{
			return id.GetString() ?? "";
		}

		return ReadString(root, "item_id");
	}

	private static string ReadTranscriptFromItem(JsonElement root)
	{
		if (!root.TryGetProperty("item", out var item) ||
			item.ValueKind != JsonValueKind.Object ||
			!item.TryGetProperty("content", out var content) ||
			content.ValueKind != JsonValueKind.Array)
		{
			return "";
		}

		foreach (var part in content.EnumerateArray())
		{
			if (part.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			if (part.TryGetProperty("transcript", out var transcript))
			{
				var value = transcript.GetString();
				if (!string.IsNullOrWhiteSpace(value))
				{
					return value;
				}
			}
		}

		return "";
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
		_sendLock.Dispose();
	}
}
