using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EnvDTE;
using GitForTfs.Services;
using GitForTfs.ViewModels;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace GitForTfs.ToolWindows
{
    /// <summary>Interaction logic for GitToolWindowControl.xaml.</summary>
    public partial class GitToolWindowControl : UserControl
    {
        private readonly GitToolWindowViewModel _viewModel;
        private bool _initialized;
        private DispatcherTimer _autoRefreshTimer;

        public GitToolWindowControl()
        {
            InitializeComponent();

            var logger = new OutputLogger(ServiceProvider.GlobalProvider);
            _viewModel = new GitToolWindowViewModel(GetSolutionDirectoryAsync, OpenDiffAsync, OpenDocumentAsync, logger.Log);
            DataContext = _viewModel;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Poll git periodically so the Changes lists reflect edits made outside the window.
            if (_autoRefreshTimer == null)
            {
                _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _autoRefreshTimer.Tick += OnAutoRefreshTick;
            }
            _autoRefreshTimer.Start();

            if (_initialized)
                return;

            _initialized = true;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await _viewModel.InitializeAsync();
            }).FileAndForget("gitfortfs/initialize");
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => _autoRefreshTimer?.Stop();

        private void OnAutoRefreshTick(object sender, EventArgs e)
        {
            // Skip while the tool window tab is hidden — no point spawning git then.
            if (!IsVisible)
                return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await _viewModel.AutoRefreshChangesAsync();
            }).FileAndForget("gitfortfs/auto-refresh");
        }

        private void OnChangeItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem container && container.DataContext is ChangeItemViewModel change
                && _viewModel.OpenDiffCommand.CanExecute(change))
            {
                _viewModel.OpenDiffCommand.Execute(change);
            }
        }

        private void OnHistoryItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem container && container.DataContext is CommitItemViewModel commit
                && _viewModel.OpenCommitDiffCommand.CanExecute(commit))
            {
                _viewModel.OpenCommitDiffCommand.Execute(commit);
            }
        }

        /// <summary>Entry point used by the Solution Explorer "File History" command.</summary>
        public void ShowFileHistoryForPath(string absolutePath)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await _viewModel.ShowFileHistoryForPathAsync(absolutePath);
            }).FileAndForget("gitfortfs/filehistory");
        }

        /// <summary>
        /// Opens Visual Studio's built-in side-by-side diff viewer for a prepared comparison.
        /// </summary>
        private async Task OpenDiffAsync(DiffRequest request)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package.GetGlobalService(typeof(SVsDifferenceService)) is IVsDifferenceService diffService))
                return;

            uint options = 0;
            if (request.LeftIsTemporary)
                options |= (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;
            if (request.RightIsTemporary)
                options |= (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;

            diffService.OpenComparisonWindow2(
                request.LeftFile,
                request.RightFile,
                request.Caption,
                request.Caption,
                request.LeftLabel,
                request.RightLabel,
                null,
                null,
                options);
        }

        /// <summary>Opens a file (e.g. the generated staged-diff scratch file) in a VS editor tab.</summary>
        private async Task OpenDocumentAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (Package.GetGlobalService(typeof(DTE)) is DTE dte)
                dte.ItemOperations.OpenFile(filePath, EnvDTE.Constants.vsViewKindTextView);
        }

        /// <summary>
        /// Returns a directory to start git repository discovery from: the folder of the open
        /// solution, or of the active document, whichever is available.
        /// </summary>
        private async Task<string> GetSolutionDirectoryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!(Package.GetGlobalService(typeof(DTE)) is DTE dte))
                return null;

            try
            {
                var solutionPath = dte.Solution?.FullName;
                if (!string.IsNullOrEmpty(solutionPath) && File.Exists(solutionPath))
                    return Path.GetDirectoryName(solutionPath);

                var documentPath = dte.ActiveDocument?.FullName;
                if (!string.IsNullOrEmpty(documentPath) && File.Exists(documentPath))
                    return Path.GetDirectoryName(documentPath);
            }
            catch (Exception)
            {
                // DTE can throw for solutions in odd states; treat as "no hint".
            }

            return null;
        }
    }
}
