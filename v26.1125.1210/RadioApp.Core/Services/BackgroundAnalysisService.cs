using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RadioApp.Audio;
using RadioApp.Data;
using RadioApp.Models;

namespace RadioApp.Services;

public sealed record AnalysisProgressSnapshot(int Completed, int Total, string CurrentTrackTitle, bool IsRunning);

/// <summary>
/// Non-blocking low-priority background worker that runs AudioTrackAnalyzer (EBU R128 LUFS,
/// BPM, Beat-Phase, RMS Energy Cues, Edge Profiles, Waveform) on library items and caches results in SQLite.
/// </summary>
public sealed class BackgroundAnalysisService : IDisposable
{
	private readonly RadioDbContextFactory _dbFactory;
	private readonly ConcurrentQueue<LibraryItem> _queue = new ConcurrentQueue<LibraryItem>();
	private readonly ConcurrentDictionary<int, byte> _enqueuedIds = new ConcurrentDictionary<int, byte>();
	private readonly CancellationTokenSource _cts = new CancellationTokenSource();
	private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
	private readonly Task _workerTask;

	private int _totalQueued;
	private int _completedCount;
	private volatile bool _isRunning;
	private double _silenceThresholdDb = -44.0;

	public event Action<LibraryItem, TrackAnalysisResult>? TrackAnalyzed;
	public event Action<AnalysisProgressSnapshot>? ProgressChanged;

	public BackgroundAnalysisService(RadioDbContextFactory dbFactory)
	{
		_dbFactory = dbFactory;
		_workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
	}

	public void ConfigureSilenceThreshold(double thresholdDb)
	{
		_silenceThresholdDb = Math.Clamp(thresholdDb, -66.0, -24.0);
	}

	public void EnqueueUnanalyzed(IEnumerable<LibraryItem> items, bool forceReanalyze = false)
	{
		int added = 0;
		foreach (LibraryItem item in items)
		{
			if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.FilePath)) continue;
			if (!forceReanalyze && item.IsAnalyzed) continue;
			if (_enqueuedIds.TryAdd(item.Id, 0))
			{
				_queue.Enqueue(item);
				Interlocked.Increment(ref _totalQueued);
				added++;
			}
		}
		if (added > 0)
		{
			_signal.Release();
		}
	}

	private async Task WorkerLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await _signal.WaitAsync(ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			while (!ct.IsCancellationRequested && _queue.TryDequeue(out LibraryItem? item))
			{
				_isRunning = true;
				try
				{
					ProgressChanged?.Invoke(new AnalysisProgressSnapshot(
						Volatile.Read(ref _completedCount),
						Volatile.Read(ref _totalQueued),
						item.Title,
						true));

					if (File.Exists(item.FilePath))
					{
						TrackAnalysisResult result = AudioFileProbeCore.AnalyzeFile(item.FilePath, _silenceThresholdDb);
						PersistResult(item.Id, result);
						TrackAnalyzed?.Invoke(item, result);
					}
				}
				catch (Exception ex)
				{
					LogService.Warn("AudioAnalyzer", $"Failed analyzing '{item.Title}': {ex.Message}");
				}
				finally
				{
					_enqueuedIds.TryRemove(item.Id, out _);
					int done = Interlocked.Increment(ref _completedCount);
					int total = Volatile.Read(ref _totalQueued);
					bool stillMore = !_queue.IsEmpty;
					_isRunning = stillMore;
					ProgressChanged?.Invoke(new AnalysisProgressSnapshot(done, total, stillMore ? item.Title : string.Empty, stillMore));
					if (!stillMore)
					{
						Interlocked.Exchange(ref _completedCount, 0);
						Interlocked.Exchange(ref _totalQueued, 0);
					}
				}
			}
		}
	}

	private void PersistResult(int itemId, TrackAnalysisResult result)
	{
		try
		{
			using RadioLibraryContext ctx = _dbFactory.CreateContext();
			LibraryItem? dbItem = ctx.LibraryItems.Find(itemId);
			if (dbItem != null)
			{
				result.ApplyTo(dbItem);
				dbItem.FileExistsCached = true;
				ctx.SaveChanges();
			}
		}
		catch (Exception ex)
		{
			LogService.Warn("AudioAnalyzer", $"Could not persist analysis for item {itemId}: {ex.Message}");
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		try { _workerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
		_cts.Dispose();
		_signal.Dispose();
	}
}
