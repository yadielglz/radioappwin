namespace RadioApp.Models;

public class ImportOptions
{
	public string Category { get; set; } = "Music";

	public string Tags { get; set; } = string.Empty;

	public bool UseInferredCategory { get; set; }
}
