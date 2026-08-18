using System.Windows.Forms;

namespace Microsoft.Win32;

public sealed class OpenFolderDialog
{
    public string Title { get; set; } = string.Empty;
    public bool Multiselect { get; set; }
    public string FolderName { get; private set; } = string.Empty;

    public bool? ShowDialog()
    {
        using FolderBrowserDialog dialog = new FolderBrowserDialog
        {
            Description = Title,
            ShowNewFolderButton = true
        };

        DialogResult result = dialog.ShowDialog();
        if (result == DialogResult.OK)
        {
            FolderName = dialog.SelectedPath;
            return true;
        }

        return false;
    }
}
