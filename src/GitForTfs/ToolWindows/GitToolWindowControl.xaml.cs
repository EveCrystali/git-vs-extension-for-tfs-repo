using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using GitForTfs.Services;
using GitForTfs.ViewModels;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace GitForTfs.ToolWindows
{
    /// <summary>Interaction logic for GitToolWindowControl.xaml.</summary>
    public partial class GitToolWindowControl : UserControl
    {
        private readonly GitToolWindowViewModel _viewModel;
        private bool _initialized;

        public GitToolWindowControl()
        {
            InitializeComponent();

            var logger = new OutputLogger(ServiceProvider.GlobalProvider);
            _viewModel = new GitToolWindowViewModel(GetSolutionDirectoryAsync, logger.Log);
            DataContext = _viewModel;

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized)
                return;

            _initialized = true;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await _viewModel.InitializeAsync();
            }).FileAndForget("gitfortfs/initialize");
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
