using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Lame;
using NAudio.Wave;
using RadioApp.Models;

namespace RadioApp.Services;

public sealed class StreamingService : IDisposable
{
	private sealed class NonClosingStream : Stream
	{
		private readonly Stream _inner; private readonly Action<int> _written;
		public NonClosingStream(Stream inner, Action<int> written) { _inner = inner; _written = written; }
		public override bool CanRead { get { return _inner.CanRead; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return _inner.CanWrite; } }
		public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
		public override void Flush() { _inner.Flush(); } public override int Read(byte[] b, int o, int c) { return _inner.Read(b, o, c); }
		public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); } public override void SetLength(long v) { throw new NotSupportedException(); }
		public override void Write(byte[] b, int o, int c) { _inner.Write(b, o, c); _written(c); }
		protected override void Dispose(bool disposing) { }
	}

	private TcpClient _client; private Stream _networkStream; private StreamConfig _config;
	private BlockingCollection<byte[]> _programQueue; private CancellationTokenSource _programCancellation; private Task _programTask;
	private volatile bool _connected; private long _bytesSent; private long _lastWriteTicks;
	public bool IsConnected { get { return _connected; } } public long BytesSent { get { return Interlocked.Read(ref _bytesSent); } }
	public DateTimeOffset? LastSuccessfulWriteUtc { get { long ticks = Interlocked.Read(ref _lastWriteTicks); return ticks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(ticks, TimeSpan.Zero); } }
	public event Action<string> StreamFailed;

	public Task<bool> ConnectAsync(StreamConfig config)
	{
		return Task.Factory.StartNew<bool>(() => Connect(config), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
	}

	private bool Connect(StreamConfig config)
	{
		Disconnect(); _config = config;
		try
		{
			_client = new TcpClient(); ConfigureClient(_client);
			IAsyncResult pending = _client.BeginConnect(config.Host, config.Port, null, null);
			if (!pending.AsyncWaitHandle.WaitOne(15000)) throw new TimeoutException("The streaming server did not accept a connection within 15 seconds.");
			_client.EndConnect(pending); _networkStream = _client.GetStream(); _networkStream.ReadTimeout = 5000; _networkStream.WriteTimeout = 10000;
			if (config.ServerType == StreamServerType.Shoutcast) ConnectShoutcast(config); else ConnectIcecast(config, _networkStream);
			Interlocked.Exchange(ref _bytesSent, 0); Interlocked.Exchange(ref _lastWriteTicks, 0); _connected = true; return true;
		}
		catch { Disconnect(); throw; }
	}

	public void StartProgramOutputStream()
	{
		if (!_connected || _networkStream == null || _config == null) throw new InvalidOperationException("Stream is not connected.");
		if (_programTask != null && !_programTask.IsCompleted) return;
		_programQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 64); _programCancellation = new CancellationTokenSource();
		Stream stream = _networkStream; StreamConfig config = _config; BlockingCollection<byte[]> queue = _programQueue; CancellationToken token = _programCancellation.Token;
		_programTask = Task.Factory.StartNew(() => Publish(stream, config, queue, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
	}

	public void EnqueueProgramOutput(byte[] pcmBytes)
	{
		if (!_connected || pcmBytes == null || pcmBytes.Length == 0 || _programQueue == null) return;
		if (!_programQueue.TryAdd(pcmBytes)) { byte[] discarded; _programQueue.TryTake(out discarded); _programQueue.TryAdd(pcmBytes); }
	}

	private void Publish(Stream stream, StreamConfig config, BlockingCollection<byte[]> queue, CancellationToken token)
	{
		WaveFormat format = new WaveFormat(44100, 16, 2); byte[] silence = new byte[format.AverageBytesPerSecond / 10];
		try
		{
			using (LameMP3FileWriter writer = new LameMP3FileWriter(new NonClosingStream(stream, RecordWrite), format, config.Bitrate))
			{
				while (!token.IsCancellationRequested) { byte[] data; if (!queue.TryTake(out data, 100, token)) data = silence; writer.Write(data, 0, data.Length); }
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { if (!token.IsCancellationRequested) { Disconnect(); RaiseFailed(ex.Message); } }
	}

	public string GetConnectionSummary()
	{
		if (!_connected) return "Disconnected"; DateTimeOffset? last = LastSuccessfulWriteUtc; if (!last.HasValue) return "Connected; starting encoder...";
		double seconds = Math.Max(0, (DateTimeOffset.UtcNow - last.Value).TotalSeconds); double mb = BytesSent / 1048576.0;
		return seconds >= 15 ? string.Format("Connection stalled - no successful server write for {0:0}s ({1:0.0} MB sent)", seconds, mb) : string.Format("Connected and publishing ({0:0.0} MB sent; last server write {1:0}s ago)", mb, seconds);
	}

	private void RecordWrite(int count) { Interlocked.Add(ref _bytesSent, count); Interlocked.Exchange(ref _lastWriteTicks, DateTimeOffset.UtcNow.Ticks); }
	private void RaiseFailed(string message) { try { Action<string> handler = StreamFailed; if (handler != null) handler(message); } catch { } }
	private static void ConfigureClient(TcpClient client) { client.NoDelay = true; client.SendTimeout = 10000; client.ReceiveTimeout = 5000; client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }

	private static void ConnectIcecast(StreamConfig config, Stream stream)
	{
		string mount = config.FullPath; if (string.IsNullOrWhiteSpace(mount) || mount == "/") throw new InvalidOperationException("Icecast mount cannot be blank. Use a mount such as /radio.");
		string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Username + ":" + config.Password));
		string request = "SOURCE " + mount + " ICE/1.0\r\nAuthorization: Basic " + token + "\r\nContent-Type: audio/mpeg\r\nIce-Public: 1\r\nIce-Name: RadioApp\r\nIce-Genre: Radio\r\nIce-Bitrate: " + config.Bitrate + "\r\nUser-Agent: RadioApp/XP\r\n\r\n";
		WriteAscii(stream, request); string response = ReadResponse(stream, 4096); if (!Accepted(response)) throw new InvalidOperationException("Icecast rejected the source connection: " + FormatResponse(response));
	}

	private void ConnectShoutcast(StreamConfig config)
	{
		string password = config.ShoutcastStreamId > 0 ? config.Password + "#" + config.ShoutcastStreamId : config.Password;
		string response = TryShoutcast(_networkStream, password, config); if (response == null && config.ShoutcastStreamId > 0) response = TryShoutcast(_networkStream, config.Password, config);
		if (response == null) throw new InvalidOperationException("SHOUTcast rejected the source login.");
	}

	private static string TryShoutcast(Stream stream, string password, StreamConfig config)
	{
		WriteAscii(stream, password + "\r\n"); string response = ReadResponse(stream, 1024); if (response.IndexOf("OK", StringComparison.OrdinalIgnoreCase) < 0) return null;
		WriteAscii(stream, "icy-name:RadioApp\r\nicy-genre:Radio\r\nicy-pub:1\r\nicy-br:" + config.Bitrate + "\r\nicy-url:http://localhost/\r\ncontent-type:audio/mpeg\r\n\r\n"); return response;
	}

	private static void WriteAscii(Stream stream, string text) { byte[] data = Encoding.ASCII.GetBytes(text); stream.Write(data, 0, data.Length); stream.Flush(); }
	private static string ReadResponse(Stream stream, int limit) { byte[] one = new byte[1]; StringBuilder result = new StringBuilder(); while (result.Length < limit && stream.Read(one, 0, 1) > 0) { result.Append((char)one[0]); string value = result.ToString(); if (value.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0 || value.IndexOf("\n\n", StringComparison.Ordinal) >= 0 || value.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0) break; } return result.ToString(); }
	private static bool Accepted(string response) { if (string.IsNullOrWhiteSpace(response)) return false; string first = response.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? response; return first.IndexOf(" 200 ", StringComparison.Ordinal) >= 0 || first.EndsWith(" 200", StringComparison.Ordinal) || first.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0; }
	private static string FormatResponse(string response) { if (string.IsNullOrWhiteSpace(response)) return "no response from server"; return response.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? response.Trim(); }

	public void Disconnect()
	{
		if (_programCancellation != null) _programCancellation.Cancel(); if (_programQueue != null) _programQueue.CompleteAdding();
		if (_networkStream != null) _networkStream.Close(); if (_client != null) _client.Close(); if (_programCancellation != null) _programCancellation.Dispose();
		_programCancellation = null; _programQueue = null; _programTask = null; _networkStream = null; _client = null; _connected = false;
	}
	public void Dispose() { Disconnect(); }
}
