using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace GitForTfs.Services
{
    /// <summary>
    /// Writes every git command and its result to a dedicated pane in the Visual Studio
    /// Output window, so the user always has a full, auditable transcript of what ran.
    /// </summary>
    public sealed class OutputLogger
    {
        // Fixed pane GUID so the pane is reused across sessions.
        private static readonly Guid PaneGuid = new Guid("2C9B7A31-9E0F-4A1E-8C4B-9A5C0D3E7F10");
        private const string PaneTitle = "Git for TFS";

        private readonly IServiceProvider _serviceProvider;
        private IVsOutputWindowPane _pane;

        public OutputLogger(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Queues a line for the output pane. Safe to call from any thread; the actual write
        /// is marshalled to the UI thread.
        /// </summary>
        public void Log(string message)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                EnsurePane();
                _pane?.OutputStringThreadSafe(message + Environment.NewLine);
            }).FileAndForget("gitfortfs/outputlogger");
        }

        private void EnsurePane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pane != null)
                return;

            if (_serviceProvider.GetService(typeof(SVsOutputWindow)) is IVsOutputWindow outputWindow)
            {
                var paneGuid = PaneGuid;
                outputWindow.CreatePane(ref paneGuid, PaneTitle, fInitVisible: 1, fClearWithSolution: 0);
                outputWindow.GetPane(ref paneGuid, out _pane);
            }
        }
    }
}
