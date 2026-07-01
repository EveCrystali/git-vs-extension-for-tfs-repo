using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using GitForTfs.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace GitForTfs.Commands
{
    /// <summary>
    /// Wires up the "Git for TFS" View-menu command that shows the tool window.
    /// </summary>
    internal sealed class OpenGitToolWindowCommand
    {
        private readonly AsyncPackage _package;

        private OpenGitToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));

            var commandId = new CommandID(PackageGuids.CommandSet, PackageIds.OpenToolWindowCommand);
            var menuItem = new MenuCommand(Execute, commandId);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
                _ = new OpenGitToolWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.ShowToolWindowAsync(typeof(GitToolWindow), 0, create: true, _package.DisposalToken);
            }).FileAndForget("gitfortfs/showtoolwindow");
        }
    }
}
