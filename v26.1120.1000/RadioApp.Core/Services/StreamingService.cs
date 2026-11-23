using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Lame;
using NAudio.Wave;
using RadioApp.Models;

namespace RadioApp.Services;

public class StreamingService : IDisposable
{
	private sealed class NonClosingStream : Stream
	{
		private readonly Stream _inner;
		private readonly Action<int> _onBytesWritten;

		public override bool CanRead => _inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => _inner.CanWrite;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public NonClosingStream(Stream inner, Action<int> onBytesWritten)
		{
			_inner = inner;
			_onBytesWritten = onBytesWritten;
		}

		public override void Flush() => _inner.Flush();
		public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
		public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count)
		{
			_inner.Write(buffer, offset, count);
			_onBytesWritten(count);
		}

		public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
			_onBytesWritten(count);
		}

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
			_onBytesWritten(buffer.Length);
		}

		protected override void Dispose(bool disposing) { }
	}

	private static readonly HttpClient MetadataHttpClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(5.0)
	};

	private TcpClient? _client;
	private Stream? _networkStream;
	private StreamConfig? _config;
	private Channel<byte[]>? _programOutputChannel;
	private CancellationTokenSource? _programOutputCancellation;
	private Task? _programOutputTask;
	private volatile bool _connected;
	private long _bytesSent;
	private long _lastSuccessfulWriteUtcTicks;

	public bool IsConnected => _connected;
	public long BytesSent => Interlocked.Read(ref _bytesSent);

	public DateTimeOffset? LastSuccessfulWriteUtc
	{
		get
		{
			long ticks = Interlocked.Read(ref _lastSuccessfulWriteUtcTicks);
			return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
		}
	}

	public event Action<string>? StreamFailed;

	public async Task<bool> ConnectAsync(StreamConfig config)
	{
		Disconnect();
		_config = config;
		try
		{
			_client = new TcpClient();
			ConfigureClient(_client);
			using CancellationTokenSource connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
			try
			{
				await _client.ConnectAsync(config.Host, config.Port, connectTimeout.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (connectTimeout.IsCancellationRequested)
			{
				throw new TimeoutException("The streaming server did not accept a connection within 15 seconds.");
			}

			_networkStream = _client.GetStream();
			_networkStream.ReadTimeout = 5000;
			_networkStream.WriteTimeout = 10000;

			if (config.ServerType != StreamServerType.Shoutcast)
			{
				await ConnectIcecastAsync(config, _networkStream).ConfigureAwait(false);
			}
			else
			{
				await ConnectShoutcastAsync(config).ConfigureAwait(false);
			}

			Interlocked.Exchange(ref _bytesSent, 0L);
			Interlocked.Exchange(ref _lastSuccessfulWriteUtcTicks, 0L);
			_connected = true;
			LogService.Info("Stream", $"Connected to {config.ServerType} {config.Host}:{config.Port}{config.FullPath} @ {config.Bitrate} kbps");
			return true;
		}
		catch (Exception ex)
		{
			LogService.Error("Stream", $"Connection failed to {config.Host}:{config.Port}", ex);
			Disconnect();
			throw;
		}
	}

	/// <summary>
	/// Diagnostic probe: tests TCP reachability and server handshake without disrupting an active broadcast.
	/// </summary>
	public static async Task<(bool Ok, string Message)> TestConnectionAsync(StreamConfig config)
	{
		try
		{
			using TcpClient testClient = new TcpClient();
			ConfigureClient(testClient);
			using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
			await testClient.ConnectAsync(config.Host, config.Port, cts.Token).ConfigureAwait(false);
			return (true, $"TCP reachable on {config.Host}:{config.Port} ({config.ServerType}). Ready to broadcast.");
		}
		catch (OperationCanceledException)
		{
			return (false, $"Timed out connecting to {config.Host}:{config.Port} after 8s.");
		}
		catch (Exception ex)
		{
			return (false, $"Connection test failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Pushes live "Artist - Title" metadata to Icecast2 (/admin/metadata) or SHOUTcast (/admin.cgi).
	/// </summary>
	public async Task UpdateMetadataAsync(StreamConfig config, string songText)
	{
		if (!_connected || !config.PushTrackMetadata || string.IsNullOrWhiteSpace(songText))
		{
			return;
		}

		try
		{
			string encodedSong = Uri.EscapeDataString(songText.Trim());
			HttpRequestMessage req;

			if (config.ServerType == StreamServerType.Icecast)
			{
				string mount = Uri.EscapeDataString(config.FullPath);
				string url = $"http://{config.Host}:{config.Port}/admin/metadata?mode=updinfo&mount={mount}&song={encodedSong}";
				req = new HttpRequestMessage(HttpMethod.Get, url);
				string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
				req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
			}
			else
			{
				string pass = Uri.EscapeDataString(config.Password ?? string.Empty);
				string sidPart = config.ShoutcastStreamId > 0 ? $"&sid={config.ShoutcastStreamId}" : string.Empty;
				string url = $"http://{config.Host}:{config.Port}/admin.cgi?mode=updinfo&pass={pass}{sidPart}&song={encodedSong}";
				req = new HttpRequestMessage(HttpMethod.Get, url);
			}

			req.Headers.UserAgent.ParseAdd($"RadioApp/{AppVersionInfo.Version} (Mozilla)");
			using HttpResponseMessage response = await MetadataHttpClient.SendAsync(req).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				LogService.Warn("StreamMeta", $"Metadata update returned HTTP {(int)response.StatusCode}");
			}
		}
		catch (Exception ex)
		{
			LogService.Warn("StreamMeta", $"Metadata update skipped: {ex.Message}");
		}
	}

	public void StartProgramOutputStream()
	{
		if (!_connected || _networkStream == null || _config == null)
		{
			throw new InvalidOperationException("Stream is not connected.");
		}
		Task? existing = _programOutputTask;
		if (existing == null || existing.IsCompleted)
		{
			_programOutputChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.DropOldest,
				AllowSynchronousContinuations = false
			});
			_programOutputCancellation = new CancellationTokenSource();
			Stream networkStream = _networkStream;
			StreamConfig config = _config;
			Channel<byte[]> channel = _programOutputChannel;
			_programOutputTask = Task.Run(() => PublishProgramOutputAsync(networkStream, config, channel, _programOutputCancellation.Token));
		}
	}

	public void EnqueueProgramOutput(byte[] pcmBytes)
	{
		if (_connected && pcmBytes.Length != 0)
		{
			_programOutputChannel?.Writer.TryWrite(pcmBytes);
		}
	}

	public string GetConnectionSummary()
	{
		if (!_connected) return "Disconnected";
		DateTimeOffset? lastWrite = LastSuccessfulWriteUtc;
		if (lastWrite == null) return "Connected; starting encoder...";

		double secondsSinceWrite = Math.Max(0.0, (DateTimeOffset.UtcNow - lastWrite.Value).TotalSeconds);
		double megabytesSent = BytesSent / 1048576.0;
		if (secondsSinceWrite >= 15.0)
		{
			return $"Stalled — no write for {secondsSinceWrite:0}s ({megabytesSent:0.0} MB)";
		}
		return $"LIVE • {megabytesSent:0.0} MB sent ({_config?.Bitrate ?? 128} kbps)";
	}

	private void RecordSuccessfulWrite(int byteCount)
	{
		Interlocked.Add(ref _bytesSent, byteCount);
		Interlocked.Exchange(ref _lastSuccessfulWriteUtcTicks, DateTimeOffset.UtcNow.Ticks);
	}

	private void RaiseStreamFailed(string message)
	{
		try { StreamFailed?.Invoke(message); } catch { }
	}

	private static void ConfigureClient(TcpClient client)
	{
		client.NoDelay = true;
		client.SendTimeout = 10000;
		client.ReceiveTimeout = 5000;
		client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
		try
		{
			client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
			client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
			client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
		}
		catch (SocketException)
		{
		}
	}

	private async Task PublishProgramOutputAsync(Stream networkStream, StreamConfig config, Channel<byte[]> channel, CancellationToken cancellationToken)
	{
		WaveFormat pcmFormat = new WaveFormat(44100, 16, 2);
		byte[] silenceBuffer = new byte[pcmFormat.AverageBytesPerSecond / 10];
		try
		{
			using LameMP3FileWriter mp3Writer = new LameMP3FileWriter(
				new NonClosingStream(networkStream, RecordSuccessfulWrite),
				pcmFormat,
				config.Bitrate);
			while (!cancellationToken.IsCancellationRequested)
			{
				byte[] pcmBytes = await ReadProgramOutputAsync(channel.Reader, silenceBuffer, cancellationToken).ConfigureAwait(false);
				mp3Writer.Write(pcmBytes, 0, pcmBytes.Length);
			}
		}
		catch (OperationCanceledException) { }
		catch (ChannelClosedException) when (cancellationToken.IsCancellationRequested || !_connected) { }
		catch (Exception) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex)
		{
			LogService.Error("Stream", "Encoder/publish loop terminated unexpectedly", ex);
			Disconnect();
			RaiseStreamFailed(ex.Message);
		}
	}

	private static async Task<byte[]> ReadProgramOutputAsync(ChannelReader<byte[]> reader, byte[] silence, CancellationToken cancellationToken)
	{
		if (reader.TryRead(out byte[]? queuedBytes))
		{
			return queuedBytes;
		}
		using CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		readTimeout.CancelAfter(TimeSpan.FromMilliseconds(100.0));
		try
		{
			return await reader.ReadAsync(readTimeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return silence;
		}
	}

	private static async Task ConnectIcecastAsync(StreamConfig config, Stream stream)
	{
		string authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
		string mount = config.FullPath;
		if (string.IsNullOrWhiteSpace(mount) || mount == "/")
		{
			throw new InvalidOperationException("Icecast mount cannot be blank. Use a mount such as /radio.");
		}

		string stationName = string.IsNullOrWhiteSpace(config.StationName) ? "RadioApp Studio" : SanitizeHeader(config.StationName);
		string stationGenre = string.IsNullOrWhiteSpace(config.StationGenre) ? "Radio" : SanitizeHeader(config.StationGenre);
		string stationUrl = string.IsNullOrWhiteSpace(config.StationUrl) ? "https://radioapp.local" : SanitizeHeader(config.StationUrl);
		string stationDesc = string.IsNullOrWhiteSpace(config.StationDescription) ? "Live Broadcast" : SanitizeHeader(config.StationDescription);

		var request = new StringBuilder();
		request.Append($"SOURCE {mount} ICE/1.0\r\n");
		request.Append($"Authorization: Basic {authToken}\r\n");
		request.Append("Content-Type: audio/mpeg\r\n");
		request.Append("Ice-Public: 1\r\n");
		request.Append($"Ice-Name: {stationName}\r\n");
		request.Append($"Ice-Genre: {stationGenre}\r\n");
		request.Append($"Ice-URL: {stationUrl}\r\n");
		request.Append($"Ice-Description: {stationDesc}\r\n");
		request.Append($"Ice-Bitrate: {config.Bitrate}\r\n");
		request.Append($"User-Agent: RadioApp/{AppVersionInfo.Version}\r\n\r\n");

		await WriteAsciiAsync(stream, request.ToString()).ConfigureAwait(false);
		string response = await ReadLoginResponseAsync(stream, "Icecast").ConfigureAwait(false);
		if (!IsAcceptedResponse(response))
		{
			throw new InvalidOperationException("Icecast rejected the source connection: " + FormatResponse(response));
		}
	}

	private async Task ConnectShoutcastAsync(StreamConfig config)
	{
		if (_networkStream == null || _client == null)
		{
			throw new InvalidOperationException("SHOUTcast connection was not opened.");
		}

		(bool Accepted, string Response) response;
		if (config.ShoutcastStreamId <= 0)
		{
			response = await TryShoutcastLoginAsync(_networkStream, config.Password, config).ConfigureAwait(false);
		}
		else
		{
			string v2Password = $"{config.Password}#{config.ShoutcastStreamId}";
			response = await TryShoutcastLoginAsync(_networkStream, v2Password, config).ConfigureAwait(false);
			if (!response.Accepted)
			{
				Disconnect();
				_client = new TcpClient();
				ConfigureClient(_client);
				using CancellationTokenSource connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
				await _client.ConnectAsync(config.Host, config.Port, connectTimeout.Token).ConfigureAwait(false);
				_networkStream = _client.GetStream();
				_networkStream.ReadTimeout = 5000;
				_networkStream.WriteTimeout = 10000;
				response = await TryShoutcastLoginAsync(_networkStream, config.Password, config).ConfigureAwait(false);
			}
		}

		if (!response.Accepted)
		{
			string tried = config.ShoutcastStreamId <= 0 ? "plain password" : $"stream ID #{config.ShoutcastStreamId} and plain password";
			throw new InvalidOperationException($"SHOUTcast rejected the source login. Tried {tried}. Last response: {FormatResponse(response.Response)}");
		}
	}

	private static async Task<(bool Accepted, string Response)> TryShoutcastLoginAsync(Stream stream, string loginPassword, StreamConfig config)
	{
		await WriteAsciiAsync(stream, loginPassword + "\r\n").ConfigureAwait(false);
		string response = await ReadShoutcastLoginResponseAsync(stream).ConfigureAwait(false);
		if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
		{
			return (false, response);
		}

		string stationName = string.IsNullOrWhiteSpace(config.StationName) ? "RadioApp Studio" : SanitizeHeader(config.StationName);
		string stationGenre = string.IsNullOrWhiteSpace(config.StationGenre) ? "Radio" : SanitizeHeader(config.StationGenre);
		string stationUrl = string.IsNullOrWhiteSpace(config.StationUrl) ? "https://radioapp.local" : SanitizeHeader(config.StationUrl);

		var headers = new StringBuilder();
		headers.Append($"icy-name:{stationName}\r\n");
		headers.Append($"icy-genre:{stationGenre}\r\n");
		headers.Append("icy-pub:1\r\n");
		headers.Append($"icy-br:{config.Bitrate}\r\n");
		headers.Append($"icy-url:{stationUrl}\r\n");
		headers.Append("content-type:audio/mpeg\r\n\r\n");
		await WriteAsciiAsync(stream, headers.ToString()).ConfigureAwait(false);
		return (true, response);
	}

	private static string SanitizeHeader(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();

	private static async Task<string> ReadShoutcastLoginResponseAsync(Stream stream)
	{
		byte[] buffer = new byte[1];
		var builder = new StringBuilder();
		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
		while (builder.Length < 1024 && await stream.ReadAsync(buffer.AsMemory(0, 1), timeout.Token).ConfigureAwait(false) > 0)
		{
			builder.Append((char)buffer[0]);
			string resp = builder.ToString();
			if (resp.Contains('\n') || resp.Contains("OK", StringComparison.OrdinalIgnoreCase) ||
				resp.Contains("invalid", StringComparison.OrdinalIgnoreCase) || resp.Contains("bad", StringComparison.OrdinalIgnoreCase))
			{
				break;
			}
		}
		return builder.ToString();
	}

	private static async Task WriteAsciiAsync(Stream stream, string text)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(text);
		await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
		await stream.FlushAsync().ConfigureAwait(false);
	}

	private static async Task<string> ReadLoginResponseAsync(Stream stream, string serverName)
	{
		try
		{
			return await ReadServerResponseAsync(stream).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw new InvalidOperationException($"{serverName} did not respond to the source login within 5 seconds.");
		}
	}

	private static async Task<string> ReadServerResponseAsync(Stream stream)
	{
		byte[] buffer = new byte[1024];
		var builder = new StringBuilder();
		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
		while (builder.Length < 4096)
		{
			int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
			if (bytesRead <= 0) break;
			builder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
			string current = builder.ToString();
			if (current.Contains("\r\n\r\n", StringComparison.Ordinal) || current.Contains("\n\n", StringComparison.Ordinal))
			{
				break;
			}
		}
		return builder.ToString();
	}

	private static bool IsAcceptedResponse(string response)
	{
		if (string.IsNullOrWhiteSpace(response)) return false;
		string firstLine = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? response;
		return firstLine.Contains(" 200 ", StringComparison.Ordinal) ||
			   firstLine.EndsWith(" 200", StringComparison.Ordinal) ||
			   firstLine.Contains("OK", StringComparison.OrdinalIgnoreCase);
	}

	private static string FormatResponse(string response)
	{
		if (string.IsNullOrWhiteSpace(response)) return "no response from server";
		return response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? response.Trim();
	}

	public void Disconnect()
	{
		_programOutputCancellation?.Cancel();
		_programOutputChannel?.Writer.TryComplete();
		_networkStream?.Close();
		_client?.Close();
		_programOutputCancellation?.Dispose();
		_programOutputCancellation = null;
		_programOutputChannel = null;
		_programOutputTask = null;
		_networkStream = null;
		_client = null;
		_connected = false;
	}

	public void Dispose() => Disconnect();
}
