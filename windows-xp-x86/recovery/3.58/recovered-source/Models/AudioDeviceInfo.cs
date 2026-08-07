namespace RadioApp.Models;

public class AudioDeviceInfo
{
	public int DeviceNumber { get; set; }

	public string Name { get; set; } = string.Empty;

	public string DisplayName
	{
		get
		{
			if (DeviceNumber >= 0)
			{
				return $"{Name} ({DeviceNumber})";
			}
			return Name;
		}
	}
}
