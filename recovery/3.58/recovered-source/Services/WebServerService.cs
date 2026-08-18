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
/// Serves static web assets from the 'web/' directory and provides real file browsing,
/// live settings mutation, and real-time state synchronization with MainViewModel.
/// </summary>
public class WebServerService : IDisposable
{
	private HttpListener? _listener;
	private readonly MainViewModel _mainViewModel;
	private readonly string _webRoot;
	private CancellationTokenSource? _cts;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, WebSocket> _sockets = new();
	private bool _isDisposed;

	private static readonly string[] AudioExtensions = new[] { ".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".wma" };

	public int Port { get; private set; }

	public WebServerService(MainViewModel mainViewModel, int port = 5000, string? webRoot = null)
	{
		_mainViewModel = mainViewModel;
		Port = port;
		_webRoot = webRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");

		// Debounced lightweight timer for playback updates (500ms interval, only runs when clients are connected)
		_playerUpdateTimer = new Timer(OnPlayerUpdateTimerTick, null, 500, 500);

		// Full state broadcasts only on structural changes (Library, Watched Folders, Queue)
		_mainViewModel.Queue.CollectionChanged += (s, e) => _ = RequestPlayerUpdateAsync();
		if (_mainViewModel.Library != null)
		{
			_mainViewModel.Library.Items.CollectionChanged += (s, e) => _ = BroadcastFullStateAsync();
			_mainViewModel.Library.WatchedFolders.CollectionChanged += (s, e) => _ = BroadcastFullStateAsync();
		}
	}

	private readonly Timer? _playerUpdateTimer;
	private volatile bool _playerUpdatePending;

	private void OnPlayerUpdateTimerTick(object? state)
	{
		if (_sockets.IsEmpty || _isDisposed) return;
		_ = BroadcastPlayerUpdateAsync();
	}

	private Task RequestPlayerUpdateAsync()
	{
		if (_sockets.IsEmpty || _isDisposed) return Task.CompletedTask;
		return BroadcastPlayerUpdateAsync();
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
			context.Response.StatusCode = 200;
			// Enable CORS for LAN & Cloudflare access
			context.Response.AppendHeader("Access-Control-Allow-Origin", "*");
			context.Response.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
			context.Response.AppendHeader("Access-Control-Allow-Headers", "Content-Type");

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

			if (rawPath.Equals("api", StringComparison.OrdinalIgnoreCase) || rawPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
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
				if (!context.Response.OutputStream.CanWrite) return;
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
				streamConfig = new
				{
					serverType = _mainViewModel.StreamConfig.ServerType.ToString(),
					host = _mainViewModel.StreamConfig.Host,
					port = _mainViewModel.StreamConfig.Port,
					mount = _mainViewModel.StreamConfig.Mount,
					username = _mainViewModel.StreamConfig.Username,
					password = _mainViewModel.StreamConfig.Password,
					bitrate = _mainViewModel.StreamConfig.Bitrate,
					shoutcastStreamId = _mainViewModel.StreamConfig.ShoutcastStreamId,
					autoConnect = _mainViewModel.AutoConnectStreamOnStartup,
					endpointText = _mainViewModel.StreamEndpointText
				},
				queue = queueItems,
				library = libraryItems,
				watchedFolders = watchedFolders,
				categories = categories
			}
		};
	}

	public object GetPlayerStateObject()
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
				queue = queueItems
			}
		};
	}

	private async Task BroadcastPlayerUpdateAsync()
	{
		if (_sockets.IsEmpty) return;

		try
		{
			var stateObj = GetPlayerStateObject();
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

	private async Task BroadcastFullStateAsync()
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
		string subPath = path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
			? path.Substring(4).Trim('/')
			: (path.Equals("api", StringComparison.OrdinalIgnoreCase) ? string.Empty : path);

		// 1. GET /api/drives -> Enumerate all physical and mapped network drives
		if (subPath.Equals("drives", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET")
		{
			var drives = DriveInfo.GetDrives().Select(d => new
			{
				name = d.Name,
				driveType = d.DriveType.ToString(),
				isReady = d.IsReady,
				label = d.IsReady ? d.VolumeLabel : string.Empty,
				totalSize = d.IsReady ? d.TotalSize : 0,
				availableFreeSpace = d.IsReady ? d.AvailableFreeSpace : 0
			}).ToList();

			string jsonDrives = JsonSerializer.Serialize(new { success = true, drives });
			await WriteJsonResponseAsync(context, jsonDrives);
			return;
		}

		// 2. GET /api/browse?path=... -> Real directory browser on Windows Host / TrueNAS mount
		if (subPath.Equals("browse", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET")
		{
			string targetPath = context.Request.QueryString["path"] ?? string.Empty;
			if (string.IsNullOrWhiteSpace(targetPath))
			{
				var firstDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);
				targetPath = firstDrive?.Name ?? @"C:\";
			}

			try
			{
				if (!Directory.Exists(targetPath))
				{
					context.Response.StatusCode = 404;
					await WriteJsonResponseAsync(context, JsonSerializer.Serialize(new { success = false, message = "Directory not found." }));
					return;
				}

				var dirInfo = new DirectoryInfo(targetPath);
				var subDirs = dirInfo.GetDirectories()
					.Where(d => (d.Attributes & FileAttributes.Hidden) == 0 && (d.Attributes & FileAttributes.System) == 0)
					.Select(d => new { name = d.Name, fullPath = d.FullName })
					.OrderBy(d => d.name)
					.ToList();

				var files = dirInfo.GetFiles()
					.Where(f => AudioExtensions.Contains(f.Extension.ToLowerInvariant()))
					.Select(f => new { name = f.Name, fullPath = f.FullName, size = f.Length })
					.OrderBy(f => f.name)
					.ToList();

				var result = new
				{
					success = true,
					currentPath = dirInfo.FullName,
					parentPath = dirInfo.Parent?.FullName,
					directories = subDirs,
					audioFiles = files
				};

				await WriteJsonResponseAsync(context, JsonSerializer.Serialize(result));
			}
			catch (UnauthorizedAccessException)
			{
				await WriteJsonResponseAsync(context, JsonSerializer.Serialize(new { success = false, message = "Access Denied by Windows Security." }));
			}
			catch (Exception ex)
			{
				await WriteJsonResponseAsync(context, JsonSerializer.Serialize(new { success = false, message = ex.Message }));
			}
			return;
		}

		// 3. POST /api/settings/update -> Persist settings changes
		if (subPath.Equals("settings/update", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
		{
			using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
			string body = await reader.ReadToEndAsync();
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (root.TryGetProperty("sweeperFolder", out var sf)) _mainViewModel.SweeperFolder = sf.GetString() ?? _mainViewModel.SweeperFolder;
			if (root.TryGetProperty("radioIdFolder", out var rf)) _mainViewModel.RadioIdFolder = rf.GetString() ?? _mainViewModel.RadioIdFolder;
			if (root.TryGetProperty("sweeperMin", out var smin)) _mainViewModel.SweeperMinSongs = smin.GetInt32();
			if (root.TryGetProperty("sweeperMax", out var smax)) _mainViewModel.SweeperMaxSongs = smax.GetInt32();
			if (root.TryGetProperty("sweepersEnabled", out var se)) _mainViewModel.SweepersEnabled = se.GetBoolean();
			if (root.TryGetProperty("radioIdMinute", out var rm)) _mainViewModel.RadioIdMinute = rm.GetInt32();
			if (root.TryGetProperty("toleranceBefore", out var tb)) _mainViewModel.RadioIdToleranceBeforeMinutes = tb.GetInt32();
			if (root.TryGetProperty("toleranceAfter", out var ta)) _mainViewModel.RadioIdToleranceAfterMinutes = ta.GetInt32();
			if (root.TryGetProperty("radioIdsEnabled", out var re)) _mainViewModel.RadioIdsEnabled = re.GetBoolean();
			if (root.TryGetProperty("hourlyTagRules", out var hr)) _mainViewModel.AutoDjHourlyTagRules = hr.GetString() ?? _mainViewModel.AutoDjHourlyTagRules;
			if (root.TryGetProperty("autoDjDefaultTag", out var dt)) _mainViewModel.AutoDjDefaultTag = dt.GetString() ?? _mainViewModel.AutoDjDefaultTag;

			// Stream Server Details (SHOUTcast / Icecast)
			if (root.TryGetProperty("streamServerType", out var st))
			{
				if (Enum.TryParse<StreamServerType>(st.GetString(), true, out var parsedType))
					_mainViewModel.StreamConfig.ServerType = parsedType;
			}
			if (root.TryGetProperty("streamHost", out var sh)) _mainViewModel.StreamConfig.Host = sh.GetString() ?? _mainViewModel.StreamConfig.Host;
			if (root.TryGetProperty("streamPort", out var sp)) _mainViewModel.StreamConfig.Port = sp.GetInt32();
			if (root.TryGetProperty("streamMount", out var sm)) _mainViewModel.StreamConfig.Mount = sm.GetString() ?? _mainViewModel.StreamConfig.Mount;
			if (root.TryGetProperty("streamUsername", out var su)) _mainViewModel.StreamConfig.Username = su.GetString() ?? _mainViewModel.StreamConfig.Username;
			if (root.TryGetProperty("streamPassword", out var spw)) _mainViewModel.StreamConfig.Password = spw.GetString() ?? _mainViewModel.StreamConfig.Password;
			if (root.TryGetProperty("streamBitrate", out var sb)) _mainViewModel.StreamConfig.Bitrate = sb.GetInt32();
			if (root.TryGetProperty("streamShoutcastStreamId", out var ssid)) _mainViewModel.StreamConfig.ShoutcastStreamId = ssid.GetInt32();
			if (root.TryGetProperty("autoConnectStreamOnStartup", out var ac)) _mainViewModel.AutoConnectStreamOnStartup = ac.GetBoolean();

			await BroadcastFullStateAsync();
			await WriteJsonResponseAsync(context, JsonSerializer.Serialize(new { success = true }));
			return;
		}

		// 4. POST /api/folders/add -> Add watched folder to SQLite library
		if (subPath.Equals("folders/add", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
		{
			using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
			string body = await reader.ReadToEndAsync();
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("folder", out var folderEl))
			{
				string folder = folderEl.GetString() ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
				{
					if (_mainViewModel.Library != null && !_mainViewModel.Library.WatchedFolders.Contains(folder))
					{
						_mainViewModel.Library.WatchedFolders.Add(folder);
						_mainViewModel.Library.RescanWatchedFoldersCommand?.Execute(null);
					}
				}
			}
			await BroadcastFullStateAsync();
			await WriteJsonResponseAsync(context, JsonSerializer.Serialize(new { success = true }));
			return;
		}

		// 5. POST /api/folders/remove
		if (subPath.Equals("folders/remove", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
		{
			using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
			string body = await reader.ReadToEndAsync();
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("folder", out var folderEl))
			{
				string folder = folderEl.GetString() ?? string.Empty;
				if (_mainViewModel.Library != null && _mainViewModel.Library.WatchedFolders.Contains(folder))
				{
					_mainViewModel.Library.WatchedFolders.Remove(folder);
				}
			}
			await BroadcastFullStateAsync();
			await WriteJsonResponseAsync(context, JsonSerializer.Serialize(new { success = true }));
			return;
		}

		// Default /api/state fallback
		var stateObj = GetFullStateObject();
		string json = JsonSerializer.Serialize(stateObj);
		await WriteJsonResponseAsync(context, json);
	}

	private static async Task WriteJsonResponseAsync(HttpListenerContext context, string json)
	{
		byte[] data = Encoding.UTF8.GetBytes(json);
		context.Response.ContentType = "application/json; charset=utf-8";
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
