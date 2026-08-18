#if NET6_0_WINDOWS
using System.Windows.Forms;

namespace Microsoft.Win32;

/// <summary>
/// Windows 7/.NET 6 compatibility shim for the OpenFolderDialog API available in newer WPF targets.
/// Uses the classic WinForms folder picker on the legacy target.
/// </summary>
public sealed class OpenFolderDialog
{
    public string Title { get; set; } = string.Empty;

    // Kept for source compatibility. FolderBrowserDialog is single-select only.
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
#endif
