using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using EnvDTE;
using GitForTfs.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace GitForTfs.Commands
{
    /// <summary>
    /// Solution Explorer context-menu command ("Git for TFS: File History"): shows the git
    /// history of the selected file in the tool window.
    /// </summary>
    internal sealed class ShowFileHistoryCommand
    {
        private readonly AsyncPackage _package;

        private ShowFileHistoryCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));

            var commandId = new CommandID(PackageGuids.CommandSet, PackageIds.FileHistoryCommand);
            commandService.AddCommand(new MenuCommand(Execute, commandId));
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
                _ = new ShowFileHistoryCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                var path = GetSelectedFilePath();
                if (string.IsNullOrEmpty(path))
                    return;

                var pane = await _package.ShowToolWindowAsync(typeof(GitToolWindow), 0, create: true, _package.DisposalToken);
                (pane?.Content as GitToolWindowControl)?.ShowFileHistoryForPath(path);
            }).FileAndForget("gitfortfs/solutionexplorer-filehistory");
        }

        private static string GetSelectedFilePath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(Package.GetGlobalService(typeof(DTE)) is DTE dte))
                return null;

            var selectedItems = dte.SelectedItems;
            if (selectedItems == null || selectedItems.Count < 1)
                return null;

            var projectItem = selectedItems.Item(1)?.ProjectItem;
            if (projectItem == null || projectItem.FileCount < 1)
                return null;

            try
            {
                return projectItem.get_FileNames(1);
            }
            catch
            {
                return null;
            }
        }
    }
}
