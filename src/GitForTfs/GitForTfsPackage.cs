using System;
using System.Runtime.InteropServices;
using System.Threading;
using GitForTfs.Commands;
using GitForTfs.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace GitForTfs
{
    /// <summary>
    /// The extension package. Loads in the background and registers the tool window and the
    /// command that opens it.
    ///
    /// <para>
    /// It is intentionally <b>not</b> a source-control provider (no <c>ProvideSourceControlProvider</c>).
    /// The whole point of this extension is to coexist with the TFVC provider that Visual Studio
    /// binds for solutions inside a TFS workspace — so it never competes for the single active
    /// provider slot, and TFVC keeps working untouched.
    /// </para>
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(
        "Git for TFS",
        "Standalone Git tool window for solutions that live inside a TFVC (TFS) workspace, where Visual Studio's built-in Git tooling is unavailable.",
        "1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(GitToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    [Guid(PackageGuids.PackageString)]
    public sealed class GitForTfsPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await OpenGitToolWindowCommand.InitializeAsync(this);
            await ShowFileHistoryCommand.InitializeAsync(this);
        }
    }
}
