using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
	}

	public void Start()
	{
		_cts = new CancellationTokenSource();

		// Strategy 1: Try Wildcard Binding (All Network Interfaces)
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

			// Add local LAN IPs if available
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
			throw new InvalidOperationException($"Unable to start embedded web server on port {Port}. Please check port availability.");
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
			if (context.Request.IsWebSocketRequest)
			{
				HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
				await HandleWebSocketClientAsync(wsContext.WebSocket, ct);
				return;
			}

			string rawPath = context.Request.Url?.AbsolutePath.Trim('/') ?? string.Empty;

			if (rawPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
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
		byte[] buffer = new byte[4096];

		try
		{
			// Send initial state sync
			await BroadcastStateAsync();

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
				case "TOGGLE_STREAM":
					_mainViewModel.ConnectStreamCommand?.Execute(null);
					break;
				case "PLAY_NEXT":
					_mainViewModel.PlayNextCommand?.Execute(null);
					break;
				case "SET_CROSSFADE":
					if (root.TryGetProperty("value", out JsonElement cfVal))
					{
						_mainViewModel.Crossfade = cfVal.GetDouble();
					}
					break;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[WS Command Error] {ex.Message}");
		}
	}

	private async Task BroadcastStateAsync()
	{
		var stateObj = new
		{
			type = "STATE_SYNC",
			data = new
			{
				streamStatus = _mainViewModel.StreamStatus,
				activityStatus = _mainViewModel.ActivityStatus,
				crossfade = _mainViewModel.Crossfade,
				deckA = new
				{
					title = _mainViewModel.DeckA?.TrackTitle ?? string.Empty,
					artist = _mainViewModel.DeckA?.TrackArtist ?? string.Empty,
					album = _mainViewModel.DeckA?.TrackAlbum ?? string.Empty,
					state = _mainViewModel.DeckA?.PlaybackStateText ?? "Stopped",
					position = _mainViewModel.DeckA?.Position.TotalSeconds ?? 0,
					duration = _mainViewModel.DeckA?.Duration.TotalSeconds ?? 0
				},
				deckB = new
				{
					title = _mainViewModel.DeckB?.TrackTitle ?? string.Empty,
					artist = _mainViewModel.DeckB?.TrackArtist ?? string.Empty,
					album = _mainViewModel.DeckB?.TrackAlbum ?? string.Empty,
					state = _mainViewModel.DeckB?.PlaybackStateText ?? "Stopped",
					position = _mainViewModel.DeckB?.Position.TotalSeconds ?? 0,
					duration = _mainViewModel.DeckB?.Duration.TotalSeconds ?? 0
				}
			}
		};

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

	private async Task HandleApiRequestAsync(HttpListenerContext context, string path)
	{
		context.Response.ContentType = "application/json";
		var response = new { success = true, port = Port, timestamp = DateTime.UtcNow };
		string json = JsonSerializer.Serialize(response);
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
