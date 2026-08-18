using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using RadioApp.ViewModels;

namespace RadioApp.Services;

/// <summary>
/// Manages the Windows Taskbar Notification Area (System Tray) icon.
/// Provides live status tooltips, color status badges, quick actions,
/// and one-click launch of the Headless Web Dashboard.
/// </summary>
public class TrayIconService : IDisposable
{
	private readonly NotifyIcon _notifyIcon;
	private readonly MainViewModel _mainViewModel;
	private readonly int _webPort;
	private readonly string _dashboardUrl;

	public TrayIconService(MainViewModel mainViewModel, int webPort = 5000, string? publicHost = null)
	{
		_mainViewModel = mainViewModel;
		_webPort = webPort;
		_dashboardUrl = !string.IsNullOrEmpty(publicHost) ? $"http://{publicHost}:{webPort}/" : $"http://localhost:{webPort}/";

		_notifyIcon = new NotifyIcon
		{
			Visible = true,
			Text = "RadioApp 5.0 Headless Studio"
		};

		// Load or generate tray icon
		SetTrayIcon(isLive: false);

		// Build Context Menu
		BuildContextMenu();

		// Click events
		_notifyIcon.DoubleClick += (s, e) => OpenWebDashboard();
		_notifyIcon.MouseClick += (s, e) =>
		{
			if (e.Button == MouseButtons.Left)
			{
				OpenWebDashboard();
			}
		};

		// Subscribe to ViewModel status updates
		_mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
		UpdateTrayStatus();
	}

	private void BuildContextMenu()
	{
		var contextMenu = new ContextMenuStrip();

		var openDashItem = new ToolStripMenuItem("🌐 Open Web Dashboard", null, (s, e) => OpenWebDashboard())
		{
			Font = new Font(contextMenu.Font, FontStyle.Bold)
		};
		contextMenu.Items.Add(openDashItem);

		contextMenu.Items.Add(new ToolStripSeparator());

		var streamToggleItem = new ToolStripMenuItem("🟢 Start / Stop Stream", null, (s, e) =>
		{
			_mainViewModel.ConnectStreamCommand?.Execute(null);
		});
		contextMenu.Items.Add(streamToggleItem);

		var nextTrackItem = new ToolStripMenuItem("⏭️ Next Track (Gapless Fade)", null, (s, e) =>
		{
			_mainViewModel.PlayNextCommand?.Execute(null);
		});
		contextMenu.Items.Add(nextTrackItem);

		contextMenu.Items.Add(new ToolStripSeparator());

		var copyUrlItem = new ToolStripMenuItem("📋 Copy Dashboard URL", null, (s, e) =>
		{
			Clipboard.SetText(_dashboardUrl);
			_notifyIcon.ShowBalloonTip(2000, "Dashboard URL Copied", _dashboardUrl, ToolTipIcon.Info);
		});
		contextMenu.Items.Add(copyUrlItem);

		var openAppDataItem = new ToolStripMenuItem("📁 Open AppData Folder", null, (s, e) =>
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioApp");
			if (Directory.Exists(folder))
			{
				Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
			}
		});
		contextMenu.Items.Add(openAppDataItem);

		contextMenu.Items.Add(new ToolStripSeparator());

		var exitItem = new ToolStripMenuItem("❌ Exit RadioApp", null, (s, e) =>
		{
			var confirm = MessageBox.Show(
				"Are you sure you want to shut down RadioApp Broadcast Engine?",
				"Exit RadioApp",
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Question);

			if (confirm == DialogResult.Yes)
			{
				System.Windows.Application.Current.Shutdown();
			}
		});
		contextMenu.Items.Add(exitItem);

		_notifyIcon.ContextMenuStrip = contextMenu;
	}

	private void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is "StreamStatus" or "ActivityStatus" or "DeckA")
		{
			UpdateTrayStatus();
		}
	}

	public void UpdateTrayStatus()
	{
		bool isLive = _mainViewModel.StreamStatus.Contains("Live", StringComparison.OrdinalIgnoreCase) ||
		              _mainViewModel.StreamStatus.Contains("Connected", StringComparison.OrdinalIgnoreCase);

		SetTrayIcon(isLive);

		string trackInfo = !string.IsNullOrWhiteSpace(_mainViewModel.DeckA?.TrackTitle)
			? $"{_mainViewModel.DeckA.TrackArtist} - {_mainViewModel.DeckA.TrackTitle}"
			: "Standby / AutoDJ";

		string tooltip = $"RadioApp 5.0 {(isLive ? "[ON AIR]" : "[OFFLINE]")}\n{trackInfo}\nClick to open Web Studio";
		
		// Windows NotifyIcon Text is capped at 63 characters
		if (tooltip.Length > 63)
		{
			tooltip = tooltip.Substring(0, 60) + "...";
		}
		_notifyIcon.Text = tooltip;
	}

	public void OpenWebDashboard()
	{
		try
		{
			Process.Start(new ProcessStartInfo(_dashboardUrl) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			_notifyIcon.ShowBalloonTip(3000, "Error Opening Browser", ex.Message, ToolTipIcon.Error);
		}
	}

	private void SetTrayIcon(bool isLive)
	{
		try
		{
			// Dynamically generate a clean status icon badge
			using var bitmap = new Bitmap(32, 32);
			using var g = Graphics.FromImage(bitmap);
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

			// Base circle
			using var brushBg = new SolidBrush(Color.FromArgb(17, 22, 29));
			g.FillEllipse(brushBg, 1, 1, 30, 30);

			// Live status ring
			Color statusColor = isLive ? Color.FromArgb(16, 185, 129) : Color.FromArgb(56, 189, 248);
			using var penRing = new Pen(statusColor, 3);
			g.DrawEllipse(penRing, 3, 3, 26, 26);

			// Center dot
			using var brushDot = new SolidBrush(statusColor);
			g.FillEllipse(brushDot, 11, 11, 10, 10);

			IntPtr hIcon = bitmap.GetHicon();
			_notifyIcon.Icon = Icon.FromHandle(hIcon);
		}
		catch
		{
			_notifyIcon.Icon = SystemIcons.Application;
		}
	}

	public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
	{
		_notifyIcon.ShowBalloonTip(3000, title, message, icon);
	}

	public void Dispose()
	{
		_mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
		_notifyIcon.Visible = false;
		_notifyIcon.Dispose();
	}
}
