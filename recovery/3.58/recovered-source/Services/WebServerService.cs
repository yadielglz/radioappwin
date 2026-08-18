using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RadioApp.Models;
using RadioApp.ViewModels;

namespace RadioApp.Services;

/// <summary>
/// Lightweight embedded HTTP & WebSocket server for headless web studio control.
/// Serves static web assets from the 'web/' directory and routes real-time commands to MainViewModel.
/// </summary>
public class WebServerService : IDisposable
{
	private HttpListener? _listener;
	private readonly MainViewModel _mainViewModel;
	private readonly string _webRoot;
	private CancellationTokenSource? _cts;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, WebSocket> _sockets = new();
	private bool _isDisposed;

	public int Port { get; private set; }

	public WebServerService(MainViewModel mainViewModel, int port = 5000, string? webRoot = null)
	{
		_mainViewModel = mainViewModel;
		Port = port;
		_webRoot = webRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");

		// Subscribe to MainViewModel & Library updates to broadcast changes to connected web clients
		_mainViewModel.PropertyChanged += (s, e) => _ = BroadcastStateAsync();
		_mainViewModel.Queue.CollectionChanged += (s, e) => _ = BroadcastStateAsync();
		if (_mainViewModel.Library != null)
		{
			_mainViewModel.Library.Items.CollectionChanged += (s, e) => _ = BroadcastStateAsync();
			_mainViewModel.Library.WatchedFolders.CollectionChanged += (s, e) => _ = BroadcastStateAsync();
		}
	}

	public void Start()
	{
		_cts = new CancellationTokenSource();

		// Strategy 1: Try Wildcard Binding
		bool started = TryStartListener(new[] { $"http://+:{Port}/" });

		if (!started)
		{
			started = TryStartListener(new[] { $"http://*:{Port}/" });
		}

		// Strategy 2: Fallback to Localhost & Local IP addresses (Non-Admin mode)
		if (!started)
		{
			var localPrefixes = new System.Collections.Generic.List<string>
			{
				$"http://localhost:{Port}/",
				$"http://127.0.0.1:{Port}/"
			};

			try
			{
				string hostName = Dns.GetHostName();
				IPHostEntry hostEntry = Dns.GetHostEntry(hostName);
				foreach (IPAddress ip in hostEntry.AddressList)
				{
					if (ip.AddressFamily == AddressFamily.InterNetwork)
					{
						localPrefixes.Add($"http://{ip}:{Port}/");
					}
				}
			}
			catch { }

			started = TryStartListener(localPrefixes.ToArray());
		}

		// Strategy 3: Alternate Port 5001 if 5000 is occupied
		if (!started && Port == 5000)
		{
			Port = 5001;
			started = TryStartListener(new[] { $"http://localhost:{Port}/", $"http://127.0.0.1:{Port}/" });
		}

		if (started && _listener != null)
		{
			Task.Run(() => AcceptLoopAsync(_cts.Token));
			Console.WriteLine($"[RadioApp WebServer] Studio Web UI active at port {Port}");
		}
		else
		{
			throw new InvalidOperationException($"Unable to start embedded web server on port {Port}.");
		}
	}

	private bool TryStartListener(string[] prefixes)
	{
		HttpListener? candidate = null;
		try
		{
			candidate = new HttpListener();
			foreach (string prefix in prefixes)
			{
				candidate.Prefixes.Add(prefix);
			}
			candidate.Start();
			_listener = candidate;
			return true;
		}
		catch
		{
			try
			{
				candidate?.Close();
			}
			catch { }
			return false;
		}
	}

	private async Task AcceptLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
		{
			try
			{
				HttpListenerContext context = await _listener.GetContextAsync();
				_ = ProcessRequestAsync(context, ct);
			}
			catch (HttpListenerException) when (ct.IsCancellationRequested || _isDisposed)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				if (!ct.IsCancellationRequested && !_isDisposed)
				{
					Console.WriteLine($"[WebServer Error] {ex.Message}");
				}
			}
		}
	}

	private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken ct)
	{
		try
		{
			// Enable CORS for external access
			context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
			context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
			context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

			if (context.Request.HttpMethod == "OPTIONS")
			{
				context.Response.StatusCode = 204;
				return;
			}

			if (context.Request.IsWebSocketRequest)
			{
				HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
				await HandleWebSocketClientAsync(wsContext.WebSocket, ct);
				return;
			}

			string rawPath = context.Request.Url?.AbsolutePath.Trim('/') ?? string.Empty;

			if (rawPath.StartsWith("api", StringComparison.OrdinalIgnoreCase))
			{
				await HandleApiRequestAsync(context, rawPath);
				return;
			}

			// Serve static web assets
			await ServeStaticFileAsync(context, rawPath);
		}
		catch (Exception ex)
		{
			try
			{
				context.Response.StatusCode = 500;
				using StreamWriter writer = new StreamWriter(context.Response.OutputStream);
				await writer.WriteAsync($"Internal Server Error: {ex.Message}");
			}
			catch { }
		}
		finally
		{
			try { context.Response.Close(); } catch { }
		}
	}

	private async Task HandleWebSocketClientAsync(WebSocket socket, CancellationToken ct)
	{
		Guid clientId = Guid.NewGuid();
		_sockets[clientId] = socket;
		byte[] buffer = new byte[8192];

		try
		{
			// Send full initial state sync immediately on connect
			await SendStateToSocketAsync(socket);

			while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested && !_isDisposed)
			{
				WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					break;
				}
				else if (result.MessageType == WebSocketMessageType.Text)
				{
					string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
					HandleClientCommand(json);
				}
			}
		}
		finally
		{
			_sockets.TryRemove(clientId, out _);
			if (socket.State == WebSocketState.Open)
			{
				try
				{
					await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
				}
				catch { }
			}
		}
	}

	private void HandleClientCommand(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			if (!root.TryGetProperty("action", out JsonElement actionEl)) return;

			string action = actionEl.GetString() ?? string.Empty;
			switch (action.ToUpperInvariant())
			{
				case "PLAY":
					_mainViewModel.DeckA?.PlayCommand?.Execute(null);
					break;
				case "PAUSE":
					_mainViewModel.DeckA?.PauseCommand?.Execute(null);
					break;
				case "STOP":
					_mainViewModel.DeckA?.StopCommand?.Execute(null);
					break;
				case "PLAY_NEXT":
				case "SKIP":
					_mainViewModel.PlayNextCommand?.Execute(null);
					break;
				case "TOGGLE_STREAM":
					_mainViewModel.ConnectStreamCommand?.Execute(null);
					break;
				case "CLEAR_QUEUE":
					_mainViewModel.ClearQueueCommand?.Execute(null);
					break;
				case "AUTO_SCHEDULE_NOW":
					_mainViewModel.AutoScheduleNowCommand?.Execute(null);
					break;
				case "RESCAN_LIBRARY":
					_mainViewModel.Library?.RescanWatchedFoldersCommand?.Execute(null);
					break;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[WS Command Error] {ex.Message}");
		}
	}

	public object GetFullStateObject()
	{
		bool isLive = _mainViewModel.StreamStatus.Contains("Live", StringComparison.OrdinalIgnoreCase) ||
		              _mainViewModel.StreamStatus.Contains("Connected", StringComparison.OrdinalIgnoreCase);

		var queueItems = _mainViewModel.Queue.Select((item, idx) => new
		{
			id = item.Id,
			title = item.Title,
			artist = item.Artist,
			album = item.Album,
			duration = item.Duration.TotalSeconds > 0 ? item.Duration.TotalSeconds : 180,
			category = item.Category,
			type = item.IsStationIdCue ? "radioid" : (item.IsSweeperCue ? "sweeper" : "song")
		}).ToList();

		var libraryItems = _mainViewModel.Library?.Items.Select(item => new
		{
			id = item.Id,
			title = item.Title,
			artist = item.Artist,
			album = item.Album,
			duration = item.Duration.TotalHours >= 1.0 ? item.Duration.ToString(@"h\:mm\:ss") : item.Duration.ToString(@"m\:ss"),
			category = item.Category,
			tags = item.Tags,
			filePath = item.FilePath
		}).ToList() ?? new();

		var watchedFolders = _mainViewModel.Library?.WatchedFolders.ToList() ?? new();
		var categories = _mainViewModel.Library?.CategoryOptions.ToList() ?? new();

		return new
		{
			type = "STATE_SYNC",
			data = new
			{
				isLive = isLive,
				streamStatus = _mainViewModel.StreamStatus,
				activityStatus = _mainViewModel.ActivityStatus,
				headerStatus = _mainViewModel.HeaderStatus,
				crossfade = _mainViewModel.Crossfade,
				deckA = new
				{
					title = _mainViewModel.DeckA?.TrackTitle ?? "No track loaded",
					artist = _mainViewModel.DeckA?.TrackArtist ?? string.Empty,
					album = _mainViewModel.DeckA?.TrackAlbum ?? string.Empty,
					state = _mainViewModel.DeckA?.PlaybackStateText ?? "Stopped",
					position = _mainViewModel.DeckA?.Position.TotalSeconds ?? 0,
					duration = _mainViewModel.DeckA?.Duration.TotalSeconds ?? 0
				},
				settings = new
				{
					sweepersEnabled = _mainViewModel.SweepersEnabled,
					sweeperFolder = _mainViewModel.SweeperFolder,
					sweeperMin = _mainViewModel.SweeperMinSongs,
					sweeperMax = _mainViewModel.SweeperMaxSongs,
					radioIdsEnabled = _mainViewModel.RadioIdsEnabled,
					radioIdFolder = _mainViewModel.RadioIdFolder,
					radioIdMinute = _mainViewModel.RadioIdMinute,
					toleranceBefore = _mainViewModel.RadioIdToleranceBeforeMinutes,
					toleranceAfter = _mainViewModel.RadioIdToleranceAfterMinutes,
					autoDjFillCurrentHour = _mainViewModel.AutoDjFillCurrentHour,
					autoDjDefaultTag = _mainViewModel.AutoDjDefaultTag,
					hourlyTagRules = _mainViewModel.AutoDjHourlyTagRules
				},
				queue = queueItems,
				library = libraryItems,
				watchedFolders = watchedFolders,
				categories = categories
			}
		};
	}

	private async Task BroadcastStateAsync()
	{
		if (_sockets.IsEmpty) return;

		try
		{
			var stateObj = GetFullStateObject();
			string json = JsonSerializer.Serialize(stateObj);
			byte[] bytes = Encoding.UTF8.GetBytes(json);

			foreach (var (id, ws) in _sockets)
			{
				if (ws.State == WebSocketState.Open)
				{
					try
					{
						await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
					}
					catch { }
				}
			}
		}
		catch { }
	}

	private async Task SendStateToSocketAsync(WebSocket socket)
	{
		try
		{
			var stateObj = GetFullStateObject();
			string json = JsonSerializer.Serialize(stateObj);
			byte[] bytes = Encoding.UTF8.GetBytes(json);
			await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
		}
		catch { }
	}

	private async Task HandleApiRequestAsync(HttpListenerContext context, string path)
	{
		context.Response.ContentType = "application/json";
		var stateObj = GetFullStateObject();
		string json = JsonSerializer.Serialize(stateObj);
		byte[] data = Encoding.UTF8.GetBytes(json);
		context.Response.ContentLength64 = data.Length;
		await context.Response.OutputStream.WriteAsync(data, 0, data.Length);
	}

	private async Task ServeStaticFileAsync(HttpListenerContext context, string relativePath)
	{
		if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
		{
			relativePath = "index.html";
		}

		string filePath = Path.Combine(_webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

		if (!File.Exists(filePath))
		{
			context.Response.StatusCode = 404;
			using StreamWriter writer = new StreamWriter(context.Response.OutputStream);
			await writer.WriteAsync("404 - Not Found");
			return;
		}

		string ext = Path.GetExtension(filePath).ToLowerInvariant();
		context.Response.ContentType = ext switch
		{
			".html" => "text/html; charset=utf-8",
			".css" => "text/css; charset=utf-8",
			".js" => "application/javascript; charset=utf-8",
			".json" => "application/json; charset=utf-8",
			".png" => "image/png",
			".ico" => "image/x-icon",
			".svg" => "image/svg+xml",
			_ => "application/octet-stream"
		};

		byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
		context.Response.ContentLength64 = fileBytes.Length;
		await context.Response.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
	}

	public void Dispose()
	{
		if (_isDisposed) return;
		_isDisposed = true;

		_cts?.Cancel();
		try
		{
			if (_listener != null && _listener.IsListening)
			{
				_listener.Stop();
			}
			_listener?.Close();
		}
		catch { }

		_sockets.Clear();
	}
}
