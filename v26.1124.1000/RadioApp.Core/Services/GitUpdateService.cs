using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RadioApp.Models;

namespace RadioApp.Services;

public sealed record GitHubReleaseAssetInfo(
	string Name,
	string DownloadUrl,
	long SizeBytes,
	string ContentType,
	string ApiUrl = "");

public sealed record UpdateCheckResult(
	bool Success,
	bool UpdateAvailable,
	string CurrentVersion,
	string LatestVersion,
	string TagName,
	string ReleaseTitle,
	string ReleaseNotes,
	string HtmlUrl,
	DateTimeOffset? PublishedAt,
	GitHubReleaseAssetInfo? MsiAsset,
	GitHubReleaseAssetInfo? PortableZipAsset,
	string Message);

public sealed record UpdateDownloadProgress(
	long BytesReceived,
	long TotalBytes,
	double Percent,
	string StatusText);

public sealed record LocalGitSyncResult(
	bool Success,
	bool IsGitRepo,
	string RepoRoot,
	string Branch,
	int CommitsBehind,
	string LatestRemoteTag,
	string Summary);

/// <summary>
/// Fetches updates from GitHub Releases (both public and private repos via PAT / GITHUB_TOKEN / gh CLI)
/// and supports direct git fetch/pull when running inside a local Git checkout.
/// </summary>
public sealed class GitUpdateService
{
	private readonly HttpClient _http;

	public GitUpdateService(HttpClient? httpClient = null)
	{
		_http = httpClient ?? CreateDefaultHttpClient();
	}

	public static HttpClient CreateDefaultHttpClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
		client.DefaultRequestHeaders.UserAgent.Clear();
		client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RadioAppStudio", AppVersionInfo.Version));
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		return client;
	}

	public static bool TryParseVersion(string? raw, out Version version)
	{
		version = new Version(0, 0, 0, 0);
		if (string.IsNullOrWhiteSpace(raw)) return false;

		string cleaned = raw.Trim();
		if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = cleaned.Substring(1);
		}
		// Strip pre-release suffixes like -win7, -beta1
		int dash = cleaned.IndexOf('-');
		if (dash > 0) cleaned = cleaned.Substring(0, dash);

		string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2 || parts.Length > 4) return false;
		int[] nums = new int[4];
		for (int i = 0; i < parts.Length; i++)
		{
			if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return false;
		}
		version = new Version(nums[0], nums[1], nums[2], nums[3]);
		return true;
	}

	public static bool IsNewerVersion(string currentVersionText, string candidateVersionText)
	{
		if (!TryParseVersion(currentVersionText, out Version cur) ||
			!TryParseVersion(candidateVersionText, out Version cand))
		{
			return false;
		}
		return cand > cur;
	}

	/// <summary>
	/// Resolves a GitHub token in priority order:
	/// 1. Explicitly configured PAT from Settings (DPAPI encrypted)
	/// 2. Environment variable GITHUB_TOKEN or GH_TOKEN
	/// 3. Signed-in GitHub CLI (`gh auth token`) if installed on the Cloud PC
	/// </summary>
	public static async Task<string?> ResolveEffectiveTokenAsync(string? configuredToken, CancellationToken ct = default)
	{
		if (!string.IsNullOrWhiteSpace(configuredToken))
		{
			return configuredToken.Trim();
		}

		string? envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
						?? Environment.GetEnvironmentVariable("GH_TOKEN");
		if (!string.IsNullOrWhiteSpace(envToken))
		{
			return envToken.Trim();
		}

		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromSeconds(2));
			using var proc = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "gh",
					Arguments = "auth token",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			proc.Start();
			string output = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
			await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
			if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
			{
				return output.Trim();
			}
		}
		catch
		{
			// gh CLI not installed or not authenticated — fall back to unauthenticated request
		}

		return null;
	}

	public async Task<UpdateCheckResult> CheckForGitHubUpdateAsync(
		string repository,
		string? configuredToken = null,
		string? currentVersionOverride = null,
		CancellationToken ct = default)
	{
		string currentVersion = string.IsNullOrWhiteSpace(currentVersionOverride) ? AppVersionInfo.Version : currentVersionOverride;
		string repo = NormalizeRepoSlug(repository);
		if (string.IsNullOrWhiteSpace(repo))
		{
			return FailedCheck(currentVersion, "Repository must be in 'owner/repo' format (e.g. yadielglz/radioappwin).");
		}

		try
		{
			string? token = await ResolveEffectiveTokenAsync(configuredToken, ct).ConfigureAwait(false);
			string apiUrl = $"https://api.github.com/repos/{repo}/releases?per_page=15";
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
			req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
			if (!string.IsNullOrWhiteSpace(token))
			{
				req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			}

			using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				int code = (int)resp.StatusCode;
				if (code == 404 && string.IsNullOrWhiteSpace(token))
				{
					return FailedCheck(currentVersion,
						$"GitHub returned 404 (Private repo). Enter a read-only GitHub Personal Access Token below (Contents: Read), or sign in with 'gh auth login' on this PC.");
				}
				if (code == 401 || code == 403)
				{
					return FailedCheck(currentVersion,
						$"GitHub authentication failed (HTTP {code}). Check that your GitHub Token is valid and has 'Contents: Read-only' permission for {repo}.");
				}
				return FailedCheck(currentVersion, $"GitHub API returned HTTP {code} ({resp.ReasonPhrase}).");
			}

			string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			return ParseReleasesJson(json, currentVersion);
		}
		catch (OperationCanceledException)
		{
			return FailedCheck(currentVersion, "Update check canceled.");
		}
		catch (Exception ex)
		{
			LogService.Warn("Updater", $"GitHub update check failed: {ex.Message}");
			return FailedCheck(currentVersion, $"Could not reach GitHub: {ex.Message}");
		}
	}

	public static UpdateCheckResult ParseReleasesJson(string releasesJson, string currentVersion)
	{
		using JsonDocument doc = JsonDocument.Parse(releasesJson);
		if (doc.RootElement.ValueKind != JsonValueKind.Array)
		{
			return FailedCheck(currentVersion, "Unexpected GitHub Releases response.");
		}

		UpdateCheckResult? bestRelease = null;
		Version? bestVersion = null;

		foreach (JsonElement rel in doc.RootElement.EnumerateArray())
		{
			bool isDraft = rel.TryGetProperty("draft", out JsonElement d) && d.ValueKind == JsonValueKind.True;
			if (isDraft) continue;

			string tag = rel.TryGetProperty("tag_name", out JsonElement t) ? (t.GetString() ?? string.Empty) : string.Empty;
			// Skip legacy Win7-only tags so Win10/11 users never downgrade to Win7 builds
			if (tag.Contains("win7", StringComparison.OrdinalIgnoreCase)) continue;

			if (!TryParseVersion(tag, out Version parsedVer)) continue;

			var assets = new List<GitHubReleaseAssetInfo>();
			if (rel.TryGetProperty("assets", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement a in arr.EnumerateArray())
				{
					string name = a.TryGetProperty("name", out JsonElement an) ? (an.GetString() ?? string.Empty) : string.Empty;
					string url = a.TryGetProperty("browser_download_url", out JsonElement au) ? (au.GetString() ?? string.Empty) : string.Empty;
					string apiUrl = a.TryGetProperty("url", out JsonElement aapi) ? (aapi.GetString() ?? string.Empty) : string.Empty;
					long size = a.TryGetProperty("size", out JsonElement asz) && asz.TryGetInt64(out long s) ? s : 0;
					string ctype = a.TryGetProperty("content_type", out JsonElement ac) ? (ac.GetString() ?? string.Empty) : string.Empty;
					if (!string.IsNullOrWhiteSpace(name) && (!string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(apiUrl)))
					{
						assets.Add(new GitHubReleaseAssetInfo(name, url, size, ctype, apiUrl));
					}
				}
			}

			GitHubReleaseAssetInfo? msi = assets.FirstOrDefault(a =>
				a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
				!a.Name.Contains("Windows7", StringComparison.OrdinalIgnoreCase));
			GitHubReleaseAssetInfo? zip = assets.FirstOrDefault(a =>
				a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
				!a.Name.Contains("Windows7", StringComparison.OrdinalIgnoreCase));

			if (bestVersion == null || parsedVer > bestVersion)
			{
				bestVersion = parsedVer;
				string title = rel.TryGetProperty("name", out JsonElement n) ? (n.GetString() ?? tag) : tag;
				string body = rel.TryGetProperty("body", out JsonElement b) ? (b.GetString() ?? string.Empty) : string.Empty;
				string htmlUrl = rel.TryGetProperty("html_url", out JsonElement h) ? (h.GetString() ?? string.Empty) : string.Empty;
				DateTimeOffset? pub = rel.TryGetProperty("published_at", out JsonElement p) &&
									  DateTimeOffset.TryParse(p.GetString(), out DateTimeOffset dto) ? dto : null;

				string cleanVer = tag.TrimStart('v', 'V');
				bool isNewer = IsNewerVersion(currentVersion, cleanVer);
				string msg = isNewer
					? $"Update available: v{cleanVer} (current: v{currentVersion})"
					: $"You are up to date (v{currentVersion}; latest on GitHub: v{cleanVer}).";

				bestRelease = new UpdateCheckResult(
					Success: true,
					UpdateAvailable: isNewer,
					CurrentVersion: currentVersion,
					LatestVersion: cleanVer,
					TagName: tag,
					ReleaseTitle: title,
					ReleaseNotes: body,
					HtmlUrl: htmlUrl,
					PublishedAt: pub,
					MsiAsset: msi,
					PortableZipAsset: zip,
					Message: msg);
			}
		}

		return bestRelease ?? FailedCheck(currentVersion, "No compatible Windows 10/11 releases found on GitHub yet.");
	}

	public async Task<string> DownloadAssetAsync(
		GitHubReleaseAssetInfo asset,
		IProgress<UpdateDownloadProgress>? progress = null,
		string? configuredToken = null,
		CancellationToken ct = default)
	{
		string updatesDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"RadioApp",
			"updates");
		Directory.CreateDirectory(updatesDir);

		string safeFileName = Path.GetFileName(asset.Name);
		string destinationPath = Path.Combine(updatesDir, safeFileName);
		string tempPath = destinationPath + ".downloading";

		progress?.Report(new UpdateDownloadProgress(0, asset.SizeBytes, 0, $"Connecting to GitHub for {safeFileName}..."));

		string? token = await ResolveEffectiveTokenAsync(configuredToken, ct).ConfigureAwait(false);
		bool usePrivateAssetApi = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(asset.ApiUrl);
		string targetUrl = usePrivateAssetApi ? asset.ApiUrl : asset.DownloadUrl;

		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
		if (usePrivateAssetApi)
		{
			req.Headers.Accept.Clear();
			req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		using HttpResponseMessage response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();

		long totalBytes = response.Content.Headers.ContentLength ?? asset.SizeBytes;
		await using Stream remoteStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		await using (FileStream localStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
		{
			byte[] buffer = new byte[81920];
			long received = 0;
			int read;
			while ((read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
			{
				await localStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
				received += read;
				double pct = totalBytes > 0 ? Math.Clamp(received * 100.0 / totalBytes, 0.0, 100.0) : 0.0;
				progress?.Report(new UpdateDownloadProgress(
					received,
					totalBytes,
					pct,
					$"Downloading {safeFileName}: {received / 1048576.0:0.0} / {Math.Max(received, totalBytes) / 1048576.0:0.0} MB ({pct:0}%)"));
			}
		}

		if (File.Exists(destinationPath)) File.Delete(destinationPath);
		File.Move(tempPath, destinationPath);

		progress?.Report(new UpdateDownloadProgress(totalBytes, totalBytes, 100.0, $"Downloaded {safeFileName} ready to install."));
		LogService.Info("Updater", $"Downloaded release asset to '{destinationPath}'");
		return destinationPath;
	}

	/// <summary>
	/// Launches either an MSI silent/passive upgrade or a self-cleaning PowerShell portable ZIP updater.
	/// Caller should gracefully save queue and exit immediately after this returns true.
	/// </summary>
	public static bool LaunchDownloadedUpdate(string downloadedFilePath, int currentProcessId, string appInstallDir)
	{
		if (string.IsNullOrWhiteSpace(downloadedFilePath) || !File.Exists(downloadedFilePath))
		{
			return false;
		}

		static string EscapePs(string s) => s.Replace("'", "''");

		string ext = Path.GetExtension(downloadedFilePath).ToLowerInvariant();
		if (ext == ".msi")
		{
			// Launch msiexec with passive progress bar and auto-relaunch script after install completes
			string scriptPath = Path.Combine(Path.GetTempPath(), $"radioapp-msi-update-{Guid.NewGuid():N}.ps1");
			string exePath = EscapePs(Path.Combine(appInstallDir, "RadioApp.exe"));
			string safeMsi = EscapePs(downloadedFilePath);
			string ps = $@"
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {currentProcessId} -Timeout 20
Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i','""{safeMsi}""','/passive' -Wait
if (Test-Path '{exePath}') {{ Start-Process -FilePath '{exePath}' }}
Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force
";
			File.WriteAllText(scriptPath, ps, Encoding.UTF8);
			Process.Start(new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
				UseShellExecute = false,
				CreateNoWindow = true
			});
			return true;
		}

		if (ext == ".zip")
		{
			string scriptPath = Path.Combine(Path.GetTempPath(), $"radioapp-zip-update-{Guid.NewGuid():N}.ps1");
			string exePath = EscapePs(Path.Combine(appInstallDir, "RadioApp.exe"));
			string safeZip = EscapePs(downloadedFilePath);
			string safeDir = EscapePs(appInstallDir);
			string ps = $@"
$ErrorActionPreference = 'Stop'
try {{ Wait-Process -Id {currentProcessId} -Timeout 20 }} catch {{}}
Start-Sleep -Milliseconds 700
Expand-Archive -LiteralPath '{safeZip}' -DestinationPath '{safeDir}' -Force
if (Test-Path '{exePath}') {{ Start-Process -FilePath '{exePath}' -WorkingDirectory '{safeDir}' }}
try {{ Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force }} catch {{}}
";
			File.WriteAllText(scriptPath, ps, Encoding.UTF8);
			Process.Start(new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
				UseShellExecute = false,
				CreateNoWindow = true
			});
			return true;
		}

		return false;
	}

	/// <summary>
	/// Detects whether the app (or configured folder) is inside a Git checkout and runs `git fetch --all --tags`.
	/// </summary>
	public static async Task<LocalGitSyncResult> FetchFromLocalGitAsync(string? configuredRepoPath, CancellationToken ct = default)
	{
		string? root = FindGitRoot(configuredRepoPath) ?? FindGitRoot(AppContext.BaseDirectory);
		if (string.IsNullOrWhiteSpace(root))
		{
			return new LocalGitSyncResult(false, false, string.Empty, string.Empty, 0, string.Empty,
				"No local .git checkout found. Use GitHub Releases Update above, or set a Local Git Repo Path.");
		}

		var fetch = await RunGitAsync(root, "fetch --all --tags --prune", ct).ConfigureAwait(false);
		if (fetch.ExitCode != 0)
		{
			return new LocalGitSyncResult(false, true, root, string.Empty, 0, string.Empty,
				$"git fetch failed: {fetch.StdErr.Trim()}");
		}

		var branch = await RunGitAsync(root, "rev-parse --abbrev-ref HEAD", ct).ConfigureAwait(false);
		var behind = await RunGitAsync(root, "rev-list --count HEAD..@{u}", ct).ConfigureAwait(false);
		var latestTag = await RunGitAsync(root, "describe --tags --abbrev=0 origin/main", ct).ConfigureAwait(false);

		int behindCount = int.TryParse(behind.StdOut.Trim(), out int c) ? c : 0;
		string branchName = branch.StdOut.Trim();
		string tag = latestTag.ExitCode == 0 ? latestTag.StdOut.Trim() : string.Empty;

		string summary = behindCount > 0
			? $"Git fetched '{branchName}': {behindCount} new commit(s) available" + (string.IsNullOrWhiteSpace(tag) ? "." : $" (latest tag: {tag}).")
			: $"Git '{branchName}' is up to date" + (string.IsNullOrWhiteSpace(tag) ? "." : $" (latest tag: {tag}).");

		return new LocalGitSyncResult(true, true, root, branchName, behindCount, tag, summary);
	}

	public static async Task<LocalGitSyncResult> PullFromLocalGitAsync(string? configuredRepoPath, CancellationToken ct = default)
	{
		string? root = FindGitRoot(configuredRepoPath) ?? FindGitRoot(AppContext.BaseDirectory);
		if (string.IsNullOrWhiteSpace(root))
		{
			return new LocalGitSyncResult(false, false, string.Empty, string.Empty, 0, string.Empty,
				"No local .git checkout found.");
		}

		var pull = await RunGitAsync(root, "pull --ff-only", ct).ConfigureAwait(false);
		if (pull.ExitCode != 0)
		{
			return new LocalGitSyncResult(false, true, root, string.Empty, 0, string.Empty,
				$"git pull failed: {pull.StdErr.Trim()}");
		}

		LocalGitSyncResult status = await FetchFromLocalGitAsync(root, ct).ConfigureAwait(false);
		return status with { Summary = $"git pull succeeded: {pull.StdOut.Trim().Split('\n').LastOrDefault()?.Trim()}" };
	}

	public static string? FindGitRoot(string? startPath)
	{
		if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath)) return null;
		var dir = new DirectoryInfo(startPath);
		while (dir != null)
		{
			if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
			{
				return dir.FullName;
			}
			dir = dir.Parent;
		}
		return null;
	}

	private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(string cwd, string args, CancellationToken ct)
	{
		using var proc = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "git",
				Arguments = args,
				WorkingDirectory = cwd,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		proc.Start();
		Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
		Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
		await proc.WaitForExitAsync(ct).ConfigureAwait(false);
		return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
	}

	private static string NormalizeRepoSlug(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
		string s = raw.Trim();
		if (s.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
		{
			s = s.Substring("https://github.com/".Length);
		}
		if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			s = s.Substring(0, s.Length - 4);
		}
		s = s.Trim('/');
		string[] parts = s.Split('/');
		return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1])
			? $"{parts[0]}/{parts[1]}"
			: string.Empty;
	}

	private static UpdateCheckResult FailedCheck(string currentVersion, string message) =>
		new UpdateCheckResult(false, false, currentVersion, currentVersion, string.Empty, string.Empty, string.Empty, string.Empty, null, null, null, message);
}
