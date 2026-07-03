using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using GitForTfs.Commands;
using GitForTfs.Services;
using GitForTfs.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
        /// <summary>Set once the package has loaded; used by in-proc editor components (the
        /// blame adornment) that need to reach the tool window without a package reference.</summary>
        internal static GitForTfsPackage Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            Instance = this;
            await OpenGitToolWindowCommand.InitializeAsync(this);
            await ShowFileHistoryCommand.InitializeAsync(this);
        }

        /// <summary>
        /// Opens the "Git for TFS" tool window on the File History tab for the given file.
        /// Called from the current-line blame adornment when the annotation is clicked;
        /// force-loads the package if it is not yet initialized.
        /// </summary>
        internal static void ShowFileHistory(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var package = Instance;
                if (package == null && Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell)
                {
                    var guid = new Guid(PackageGuids.PackageString);
                    shell.LoadPackage(ref guid, out _);
                    package = Instance;
                }

                if (package == null)
                    return;

                var pane = await package.ShowToolWindowAsync(
                    typeof(GitToolWindow), 0, create: true, package.DisposalToken);
                (pane?.Content as GitToolWindowControl)?.ShowFileHistoryForPath(filePath);
            }).FileAndForget("gitfortfs/adornment-filehistory");
        }

        /// <summary>
        /// Opens Visual Studio's side-by-side diff between the file's HEAD version and its
        /// current contents on disk. Called when the git change margin is clicked.
        /// </summary>
        internal static void ShowWorkingDiff(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var head = await GitLineDiff.GetHeadBlobTempAsync(filePath, CancellationToken.None).ConfigureAwait(false);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (head == null)
                    return;

                if (!(Package.GetGlobalService(typeof(SVsDifferenceService)) is IVsDifferenceService diffService))
                    return;

                var name = Path.GetFileName(filePath);
                diffService.OpenComparisonWindow2(
                    head.TempPath,               // left: HEAD version (temporary)
                    filePath,                    // right: the working-tree file (real, not temporary)
                    "Diff: " + name,
                    "Diff: " + name,
                    name + " (HEAD)",
                    name + " (working tree)",
                    null,
                    null,
                    (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
            }).FileAndForget("gitfortfs/margin-working-diff");
        }
    }
}
