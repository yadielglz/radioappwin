using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RadioApp.Models;

public class ScheduleItem
{
	public int Id { get; set; }

	public string Title { get; set; } = string.Empty;

	public ScheduleEventType EventType { get; set; }

	public string FilePath { get; set; } = string.Empty;

	public DateTime ScheduledAt { get; set; }

	public TimeSpan? RepeatInterval { get; set; }

	public ScheduleRepeatMode RepeatMode { get; set; }

	public int ToleranceBeforeMinutes { get; set; }

	public int ToleranceAfterMinutes { get; set; } = 2;

	public bool IsMain { get; set; }

	public bool Enabled { get; set; } = true;

	public DeckId TargetDeck { get; set; }

	public bool IsFolderSource { get; set; }

	public int BlockDurationMinutes { get; set; } = 60;

	public int? PlaysetId { get; set; }

	public string SourceSummary =>
		PlaysetId.HasValue && PlaysetId.Value > 0
			? $"Playset #{PlaysetId.Value}"
			: FilePath;
}

public class AudioDeviceInfo
{
	public int DeviceNumber { get; set; }

	public string Name { get; set; } = string.Empty;

	public string DisplayName => DeviceNumber >= 0 ? $"{Name} ({DeviceNumber})" : Name;
}

public class ImportOptions
{
	public string Category { get; set; } = "Music";

	public string Tags { get; set; } = string.Empty;

	public bool UseInferredCategory { get; set; }

	public bool AnalyzeAfterImport { get; set; } = true;
}

public class StreamConfig : INotifyPropertyChanged
{
	private string _host = "localhost";
	private int _port = 8000;
	private string _mount = "radio";
	private string _username = "source";
	private string _password = string.Empty;
	private int _bitrate = 128;
	private StreamServerType _serverType;
	private int _shoutcastStreamId = 1;
	private string _stationName = "RadioApp Studio";
	private string _stationGenre = "Radio";
	private string _stationUrl = "https://radioapp.local";
	private string _stationDescription = "Live Broadcast via RadioApp Studio";
	private bool _pushTrackMetadata = true;
	private string _metadataFormat = "{artist} - {title}";
	private bool _autoReconnectEnabled = true;

	public StreamServerType ServerType
	{
		get => _serverType;
		set
		{
			if (SetProperty(ref _serverType, value))
			{
				OnPropertyChanged(nameof(IsIcecast));
				OnPropertyChanged(nameof(IsShoutcast));
			}
		}
	}

	public string Host
	{
		get => _host;
		set => SetProperty(ref _host, value ?? string.Empty);
	}

	public int Port
	{
		get => _port;
		set => SetProperty(ref _port, value);
	}

	public string Mount
	{
		get => _mount;
		set
		{
			if (SetProperty(ref _mount, value?.Trim() ?? string.Empty))
			{
				OnPropertyChanged(nameof(FullPath));
			}
		}
	}

	public string Username
	{
		get => _username;
		set => SetProperty(ref _username, value ?? string.Empty);
	}

	public string Password
	{
		get => _password;
		set => SetProperty(ref _password, value ?? string.Empty);
	}

	public int Bitrate
	{
		get => _bitrate;
		set => SetProperty(ref _bitrate, value);
	}

	public int ShoutcastStreamId
	{
		get => _shoutcastStreamId;
		set => SetProperty(ref _shoutcastStreamId, Math.Max(0, value));
	}

	public string StationName
	{
		get => _stationName;
		set => SetProperty(ref _stationName, value ?? string.Empty);
	}

	public string StationGenre
	{
		get => _stationGenre;
		set => SetProperty(ref _stationGenre, value ?? string.Empty);
	}

	public string StationUrl
	{
		get => _stationUrl;
		set => SetProperty(ref _stationUrl, value ?? string.Empty);
	}

	public string StationDescription
	{
		get => _stationDescription;
		set => SetProperty(ref _stationDescription, value ?? string.Empty);
	}

	public bool PushTrackMetadata
	{
		get => _pushTrackMetadata;
		set => SetProperty(ref _pushTrackMetadata, value);
	}

	public string MetadataFormat
	{
		get => _metadataFormat;
		set => SetProperty(ref _metadataFormat, value ?? "{artist} - {title}");
	}

	public bool AutoReconnectEnabled
	{
		get => _autoReconnectEnabled;
		set => SetProperty(ref _autoReconnectEnabled, value);
	}

	public string FullPath
	{
		get
		{
			string mount = Mount.Trim().TrimStart('/');
			return string.IsNullOrWhiteSpace(mount) ? "/" : "/" + mount;
		}
	}

	public bool IsIcecast => ServerType == StreamServerType.Icecast;

	public bool IsShoutcast => ServerType == StreamServerType.Shoutcast;

	public event PropertyChangedEventHandler? PropertyChanged;

	private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
