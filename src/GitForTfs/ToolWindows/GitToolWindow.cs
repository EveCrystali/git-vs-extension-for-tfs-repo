using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace GitForTfs.ToolWindows
{
    /// <summary>
    /// The dockable tool window host. Its content is the WPF <see cref="GitToolWindowControl"/>.
    /// </summary>
    [Guid(PackageGuids.ToolWindowString)]
    public sealed class GitToolWindow : ToolWindowPane
    {
        public GitToolWindow() : base(null)
        {
            Caption = "Git for TFS";
            Content = new GitToolWindowControl();
        }
    }
}
