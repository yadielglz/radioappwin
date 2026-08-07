using System;
using System.IO;
using System.Linq;
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

		public override long Length
		{
			get
			{
				throw new NotSupportedException();
			}
		}

		public override long Position
		{
			get
			{
				throw new NotSupportedException();
			}
			set
			{
				throw new NotSupportedException();
			}
		}

		public NonClosingStream(Stream inner, Action<int> onBytesWritten)
		{
			_inner = inner;
			_onBytesWritten = onBytesWritten;
		}

		public override void Flush()
		{
			_inner.Flush();
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			return _inner.FlushAsync(cancellationToken);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return _inner.Read(buffer, offset, count);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

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

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default(CancellationToken))
		{
			await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
			_onBytesWritten(buffer.Length);
		}

		protected override void Dispose(bool disposing)
		{
		}
	}

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
				await _client.ConnectAsync(config.Host, config.Port, connectTimeout.Token).ConfigureAwait(continueOnCapturedContext: false);
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
				await ConnectIcecastAsync(config, _networkStream).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				await ConnectShoutcastAsync(config).ConfigureAwait(continueOnCapturedContext: false);
			}
			Interlocked.Exchange(ref _bytesSent, 0L);
			Interlocked.Exchange(ref _lastSuccessfulWriteUtcTicks, 0L);
			_connected = true;
			return true;
		}
		catch
		{
			Disconnect();
			throw;
		}
	}

	public void StartProgramOutputStream()
	{
		if (!_connected || _networkStream == null || _config == null)
		{
			throw new InvalidOperationException("Stream is not connected.");
		}
		Task programOutputTask = _programOutputTask;
		if (programOutputTask == null || programOutputTask.IsCompleted)
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
		if (!_connected)
		{
			return "Disconnected";
		}
		DateTimeOffset? lastWrite = LastSuccessfulWriteUtc;
		if (lastWrite == null)
		{
			return "Connected; starting encoder...";
		}
		double secondsSinceWrite = Math.Max(0.0, (DateTimeOffset.UtcNow - lastWrite.Value).TotalSeconds);
		double megabytesSent = BytesSent / 1048576.0;
		if (secondsSinceWrite >= 15.0)
		{
			return $"Connection stalled — no successful server write for {secondsSinceWrite:0}s ({megabytesSent:0.0} MB sent)";
		}
		return $"Connected and publishing ({megabytesSent:0.0} MB sent; last server write {secondsSinceWrite:0}s ago)";
	}

	private void RecordSuccessfulWrite(int byteCount)
	{
		Interlocked.Add(ref _bytesSent, byteCount);
		Interlocked.Exchange(ref _lastSuccessfulWriteUtcTicks, DateTimeOffset.UtcNow.Ticks);
	}

	private void RaiseStreamFailed(string message)
	{
		try
		{
			this.StreamFailed?.Invoke(message);
		}
		catch
		{
		}
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
			// Basic keepalive remains enabled on platforms that do not expose tuning.
		}
	}

	private async Task PublishProgramOutputAsync(Stream networkStream, StreamConfig config, Channel<byte[]> channel, CancellationToken cancellationToken)
	{
		WaveFormat pcmFormat = new WaveFormat(44100, 16, 2);
		byte[] silenceBuffer = new byte[pcmFormat.AverageBytesPerSecond / 10];
		try
		{
			using LameMP3FileWriter mp3Writer = new LameMP3FileWriter(new NonClosingStream(networkStream, RecordSuccessfulWrite), pcmFormat, config.Bitrate);
			while (!cancellationToken.IsCancellationRequested)
			{
				byte[] pcmBytes = await ReadProgramOutputAsync(channel.Reader, silenceBuffer, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				mp3Writer.Write(pcmBytes, 0, pcmBytes.Length);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (ChannelClosedException) when (cancellationToken.IsCancellationRequested || !_connected)
		{
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			Disconnect();
			RaiseStreamFailed(ex2.Message);
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
			return await reader.ReadAsync(readTimeout.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return silence;
		}
	}

	private static async Task ConnectIcecastAsync(StreamConfig config, Stream stream)
	{
		string authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Username + ":" + config.Password));
		string mount = config.FullPath;
		if (string.IsNullOrWhiteSpace(mount) || mount == "/")
		{
			throw new InvalidOperationException("Icecast mount cannot be blank. Use a mount such as /radio.");
		}
		StringBuilder request = new StringBuilder();
		StringBuilder stringBuilder = request;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder);
		handler.AppendLiteral("SOURCE ");
		handler.AppendFormatted(mount);
		handler.AppendLiteral(" ICE/1.0\r\n");
		stringBuilder2.Append(ref handler);
		stringBuilder = request;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
		handler.AppendLiteral("Authorization: Basic ");
		handler.AppendFormatted(authToken);
		handler.AppendLiteral("\r\n");
		stringBuilder3.Append(ref handler);
		request.Append("Content-Type: audio/mpeg\r\n");
		request.Append("Ice-Public: 1\r\n");
		request.Append("Ice-Name: RadioApp\r\n");
		request.Append("Ice-Genre: Radio\r\n");
		stringBuilder = request;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder);
		handler.AppendLiteral("Ice-Bitrate: ");
		handler.AppendFormatted(config.Bitrate);
		handler.AppendLiteral("\r\n");
		stringBuilder4.Append(ref handler);
		request.Append("User-Agent: RadioApp/1.0\r\n");
		request.Append("\r\n");
		await WriteAsciiAsync(stream, request.ToString()).ConfigureAwait(continueOnCapturedContext: false);
		string response = await ReadLoginResponseAsync(stream, "Icecast").ConfigureAwait(continueOnCapturedContext: false);
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
		(bool, string) response;
		if (config.ShoutcastStreamId <= 0)
		{
			response = await TryShoutcastLoginAsync(_networkStream, config.Password, config).ConfigureAwait(continueOnCapturedContext: false);
		}
		else
		{
			string v2Password = $"{config.Password}#{config.ShoutcastStreamId}";
			response = await TryShoutcastLoginAsync(_networkStream, v2Password, config).ConfigureAwait(continueOnCapturedContext: false);
			if (!response.Item1)
			{
				Disconnect();
				_client = new TcpClient();
				ConfigureClient(_client);
				using CancellationTokenSource connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
				await _client.ConnectAsync(config.Host, config.Port, connectTimeout.Token).ConfigureAwait(continueOnCapturedContext: false);
				_networkStream = _client.GetStream();
				_networkStream.ReadTimeout = 5000;
				_networkStream.WriteTimeout = 10000;
				response = await TryShoutcastLoginAsync(_networkStream, config.Password, config).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		if (!response.Item1)
		{
			string tried = ((config.ShoutcastStreamId <= 0) ? "plain password" : $"stream ID #{config.ShoutcastStreamId} and plain password");
			throw new InvalidOperationException("SHOUTcast rejected the source login. Tried " + tried + ". Last response: " + FormatResponse(response.Item2));
		}
	}

	private static async Task<(bool Accepted, string Response)> TryShoutcastLoginAsync(Stream stream, string loginPassword, StreamConfig config)
	{
		await WriteAsciiAsync(stream, loginPassword + "\r\n").ConfigureAwait(continueOnCapturedContext: false);
		string response = await ReadShoutcastLoginResponseAsync(stream).ConfigureAwait(continueOnCapturedContext: false);
		if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
		{
			return (Accepted: false, Response: response);
		}
		StringBuilder headers = new StringBuilder();
		headers.Append("icy-name:RadioApp\r\n");
		headers.Append("icy-genre:Radio\r\n");
		headers.Append("icy-pub:1\r\n");
		StringBuilder stringBuilder = headers;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder);
		handler.AppendLiteral("icy-br:");
		handler.AppendFormatted(config.Bitrate);
		handler.AppendLiteral("\r\n");
		stringBuilder.Append(ref handler);
		headers.Append("icy-url:http://localhost/\r\n");
		headers.Append("content-type:audio/mpeg\r\n");
		headers.Append("\r\n");
		await WriteAsciiAsync(stream, headers.ToString()).ConfigureAwait(continueOnCapturedContext: false);
		return (Accepted: true, Response: response);
	}

	private static async Task<string> ReadShoutcastLoginResponseAsync(Stream stream)
	{
		byte[] buffer = new byte[1];
		StringBuilder builder = new StringBuilder();
		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
		while (builder.Length < 1024 && await stream.ReadAsync(buffer.AsMemory(0, 1), timeout.Token).ConfigureAwait(continueOnCapturedContext: false) > 0)
		{
			builder.Append((char)buffer[0]);
			string response = builder.ToString();
			if (response.Contains('\n') || response.Contains("OK", StringComparison.OrdinalIgnoreCase) || response.Contains("invalid", StringComparison.OrdinalIgnoreCase) || response.Contains("bad", StringComparison.OrdinalIgnoreCase))
			{
				break;
			}
		}
		return builder.ToString();
	}

	private static async Task WriteAsciiAsync(Stream stream, string text)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(text);
		await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(continueOnCapturedContext: false);
		await stream.FlushAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task<string> ReadLoginResponseAsync(Stream stream, string serverName)
	{
		try
		{
			return await ReadServerResponseAsync(stream).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
			throw new InvalidOperationException(serverName + " did not respond to the source login within 5 seconds.");
		}
	}

	private static async Task<string> ReadServerResponseAsync(Stream stream)
	{
		byte[] buffer = new byte[1024];
		StringBuilder builder = new StringBuilder();
		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
		while (builder.Length < 4096)
		{
			int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (bytesRead <= 0)
			{
				break;
			}
			builder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
			if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) || builder.ToString().Contains("\n\n", StringComparison.Ordinal))
			{
				break;
			}
		}
		return builder.ToString();
	}

	private static bool IsAcceptedResponse(string response)
	{
		if (string.IsNullOrWhiteSpace(response))
		{
			return false;
		}
		string firstLine = response.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? response;
		if (!firstLine.Contains(" 200 ", StringComparison.Ordinal) && !firstLine.EndsWith(" 200", StringComparison.Ordinal))
		{
			return firstLine.Contains("OK", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static string FormatResponse(string response)
	{
		if (string.IsNullOrWhiteSpace(response))
		{
			return "no response from server";
		}
		return response.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? response.Trim();
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

	public void Dispose()
	{
		Disconnect();
	}
}
