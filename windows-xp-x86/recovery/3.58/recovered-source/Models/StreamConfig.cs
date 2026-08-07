using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RadioApp.Models;

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

	public StreamServerType ServerType
	{
		get
		{
			return _serverType;
		}
		set
		{
			if (SetProperty(ref _serverType, value, "ServerType"))
			{
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsIcecast"));
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsShoutcast"));
			}
		}
	}

	public string Host
	{
		get
		{
			return _host;
		}
		set
		{
			SetProperty(ref _host, value, "Host");
		}
	}

	public int Port
	{
		get
		{
			return _port;
		}
		set
		{
			SetProperty(ref _port, value, "Port");
		}
	}

	public string Mount
	{
		get
		{
			return _mount;
		}
		set
		{
			if (SetProperty(ref _mount, value?.Trim() ?? string.Empty, "Mount"))
			{
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FullPath"));
			}
		}
	}

	public string Username
	{
		get
		{
			return _username;
		}
		set
		{
			SetProperty(ref _username, value, "Username");
		}
	}

	public string Password
	{
		get
		{
			return _password;
		}
		set
		{
			SetProperty(ref _password, value, "Password");
		}
	}

	public int Bitrate
	{
		get
		{
			return _bitrate;
		}
		set
		{
			SetProperty(ref _bitrate, value, "Bitrate");
		}
	}

	public int ShoutcastStreamId
	{
		get
		{
			return _shoutcastStreamId;
		}
		set
		{
			SetProperty(ref _shoutcastStreamId, Math.Max(0, value), "ShoutcastStreamId");
		}
	}

	public string FullPath
	{
		get
		{
			string mount = Mount.Trim().TrimStart('/');
			if (!string.IsNullOrWhiteSpace(mount))
			{
				return "/" + mount;
			}
			return "/";
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
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}
