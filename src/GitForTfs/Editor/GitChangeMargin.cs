using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GitForTfs.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace GitForTfs.Editor
{
    /// <summary>
    /// Creates the git change margin for every code document view backed by a file in a git
    /// working tree. Runs in-process (devenv), so it works on VS 2026 regardless of the
    /// out-of-process CodeLens host, and it sits alongside (does not replace) the TFVC change
    /// markers that Visual Studio already draws.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(GitChangeMargin.MarginName)]
    [Order(After = PredefinedMarginNames.LineNumber)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class GitChangeMarginProvider : IWpfTextViewMarginProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            var view = wpfTextViewHost?.TextView;
            if (view == null ||
                DocumentFactory == null ||
                !DocumentFactory.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document) ||
                string.IsNullOrEmpty(document.FilePath) ||
                !GitLineBlame.IsInRepository(document.FilePath))
                return null;

            return new GitChangeMargin(view, document);
        }
    }

    /// <summary>The thin coloured bar strip: green = added, amber = modified, red = deleted.</summary>
    internal sealed class GitChangeMargin : Canvas, IWpfTextViewMargin
    {
        internal const string MarginName = "GitForTfs.ChangeMargin";
        private const double Width_ = 5.0;

        private static readonly Brush AddedBrush = Frozen(0x4C, 0xAF, 0x50);
        private static readonly Brush ModifiedBrush = Frozen(0xE2, 0xA0, 0x3F);
        private static readonly Brush DeletedBrush = Frozen(0xE0, 0x52, 0x52);

        private readonly IWpfTextView _view;
        private readonly ITextDocument _document;
        private readonly string _filePath;

        private GitDiffResult _diff = GitDiffResult.Empty;
        private CancellationTokenSource _cts;
        private bool _disposed;

        public GitChangeMargin(IWpfTextView view, ITextDocument document)
        {
            _view = view;
            _document = document;
            _filePath = document.FilePath;

            Width = Width_;
            ClipToBounds = true;
            Background = Brushes.Transparent; // needed so the strip is hit-testable for click-to-diff
            Cursor = Cursors.Hand;
            ToolTip = "Git changes since the last commit — click to open the diff";

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
            _document.FileActionOccurred += OnFileActionOccurred;
            MouseLeftButtonDown += OnMouseLeftButtonDown;

            Recompute();
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_diff.IsEmpty)
                return;
            e.Handled = true;
            GitForTfsPackage.ShowWorkingDiff(_filePath);
        }

        private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            // git diff reflects the file on disk, so refresh once the buffer is saved.
            if (e.FileActionType == FileActionTypes.ContentSavedToDisk)
                Recompute();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Redraw();

        private void OnClosed(object sender, EventArgs e) => Dispose();

        private void Recompute()
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            var token = cts.Token;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var diff = await GitLineDiff.GetDiffAsync(_filePath, token).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (token.IsCancellationRequested || _disposed)
                    return;
                _diff = diff ?? GitDiffResult.Empty;
                Redraw();
            }).FileAndForget("gitfortfs/change-margin");
        }

        private void Redraw()
        {
            Children.Clear();

            if (_disposed || _diff.IsEmpty || _view.InLayout)
                return;

            IWpfTextViewLineCollection lines;
            try
            {
                lines = _view.TextViewLines;
            }
            catch
            {
                return;
            }
            if (lines == null)
                return;

            foreach (var line in lines)
            {
                if (line.VisibilityState == VisibilityState.Unattached)
                    continue;

                var lineNumber = line.Start.GetContainingLine().LineNumber + 1;
                var top = line.TextTop - _view.ViewportTop;

                Brush bar = null;
                if (_diff.Added.Contains(lineNumber))
                    bar = AddedBrush;
                else if (_diff.Modified.Contains(lineNumber))
                    bar = ModifiedBrush;

                if (bar != null)
                {
                    var rect = new Rectangle
                    {
                        Width = Width_ - 1,
                        Height = Math.Max(1, line.Height),
                        Fill = bar,
                        SnapsToDevicePixels = true,
                    };
                    SetLeft(rect, 0);
                    SetTop(rect, top);
                    Children.Add(rect);
                }

                if (_diff.Deletions.Contains(lineNumber))
                {
                    // Small triangle at the bottom edge, pointing at where lines were removed.
                    var y = top + Math.Max(1, line.Height) - 4;
                    var tri = new Polygon
                    {
                        Fill = DeletedBrush,
                        Points = new PointCollection { new Point(0, y), new Point(Width_ + 1, y), new Point(0, y + 4) },
                    };
                    Children.Add(tri);
                }
            }
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // -------- IWpfTextViewMargin / ITextViewMargin --------

        public FrameworkElement VisualElement
        {
            get { ThrowIfDisposed(); return this; }
        }

        public double MarginSize
        {
            get { ThrowIfDisposed(); return ActualWidth; }
        }

        public bool Enabled
        {
            get { ThrowIfDisposed(); return true; }
        }

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts?.Cancel();
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
            _document.FileActionOccurred -= OnFileActionOccurred;
            MouseLeftButtonDown -= OnMouseLeftButtonDown;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(MarginName);
        }
    }
}
