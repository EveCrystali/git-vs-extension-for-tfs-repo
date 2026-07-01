using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitForTfs.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace GitForTfs.Editor
{
    /// <summary>
    /// In-process editor components that draw a GitLens-style blame annotation
    /// ("author, N ago • commit subject") at the end of the caret's line.
    ///
    /// <para>
    /// This deliberately replaces the CodeLens git-blame provider for Visual Studio 2026,
    /// whose out-of-process CodeLens host does not surface third-party providers. An editor
    /// adornment runs in-process (devenv) and is discovered through the normal editor MEF
    /// catalog, so it works regardless of the CodeLens host.
    /// </para>
    /// </summary>
    internal static class GitBlameAdornmentLayer
    {
        internal const string LayerName = "GitForTfs.BlameCurrentLine";

#pragma warning disable 0649, 0169 // set by MEF composition
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(LayerName)]
        [Order(After = PredefinedAdornmentLayers.Caret)]
        internal static AdornmentLayerDefinition Definition;
#pragma warning restore 0649, 0169
    }

    /// <summary>Attaches a <see cref="GitBlameCurrentLineManager"/> to every code document view.</summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class GitBlameCurrentLineListener : IWpfTextViewCreationListener
    {
        [Import]
        internal Microsoft.VisualStudio.Text.ITextDocumentFactoryService DocumentFactory { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            if (textView == null)
                return;

            // Only annotate real, file-backed documents that live inside a git repo.
            if (DocumentFactory == null ||
                !DocumentFactory.TryGetTextDocument(textView.TextDataModel.DocumentBuffer, out var document) ||
                string.IsNullOrEmpty(document.FilePath) ||
                !GitLineBlame.IsInRepository(document.FilePath))
                return;

            // The manager wires itself to the view's events and lives as long as the view.
            _ = new GitBlameCurrentLineManager(textView, document.FilePath);
        }
    }

    /// <summary>Per-view controller: recomputes and redraws the current-line blame.</summary>
    internal sealed class GitBlameCurrentLineManager
    {
        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly string _filePath;

        private CancellationTokenSource _cts;
        private int _blamedLine = -1;
        private LineBlame _blame;

        public GitBlameCurrentLineManager(IWpfTextView view, string filePath)
        {
            _view = view;
            _filePath = filePath;
            _layer = view.GetAdornmentLayer(GitBlameAdornmentLayer.LayerName);

            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;

            ScheduleBlame();
        }

        private int CurrentCaretLine()
        {
            // 1-based line number of the caret.
            return _view.Caret.Position.BufferPosition.GetContainingLine().LineNumber + 1;
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (CurrentCaretLine() != _blamedLine)
                ScheduleBlame();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // The layer is cleared when lines reformat; redraw with what we already know,
            // or fetch if the caret moved to a not-yet-blamed line.
            if (CurrentCaretLine() == _blamedLine && _blame != null)
                Draw();
            else
                ScheduleBlame();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
            _cts?.Cancel();
        }

        private void ScheduleBlame()
        {
            var line = CurrentCaretLine();
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            var token = cts.Token;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // Debounce: don't blame on every caret twitch.
                    await Task.Delay(250, token).ConfigureAwait(false);
                    var blame = await GitLineBlame.GetLineBlameAsync(_filePath, line, token).ConfigureAwait(false);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (token.IsCancellationRequested || CurrentCaretLine() != line)
                        return;

                    _blamedLine = line;
                    _blame = (blame != null && !blame.IsUncommitted) ? blame : null;
                    Draw();
                }
                catch (OperationCanceledException) { }
            }).FileAndForget("gitfortfs/blame-adornment");
        }

        private void Draw()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _layer.RemoveAllAdornments();

            if (_blame == null || CurrentCaretLine() != _blamedLine)
                return;

            ITextViewLine viewLine;
            try
            {
                viewLine = _view.Caret.ContainingTextViewLine;
            }
            catch
            {
                return;
            }

            if (viewLine == null || !viewLine.IsValid || viewLine.VisibilityState != VisibilityState.FullyVisible)
                return;

            var brush = _view.FormattedLineSource?.DefaultTextProperties?.ForegroundBrush ?? Brushes.Gray;

            var label = new TextBlock
            {
                Text = "  " + _blame.InlineText,
                Foreground = brush,
                Opacity = 0.55,
                FontStyle = FontStyles.Italic,
                IsHitTestVisible = true,
                Cursor = Cursors.Hand,
                ToolTip = $"{_blame.Author} — {_blame.RelativeDate}\n{_blame.ShortHash}  {_blame.Summary}\n\nClick: Git for TFS file history",
            };

            if (_view.FormattedLineSource != null)
            {
                label.FontFamily = _view.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily;
                label.FontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
            }

            label.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                GitForTfsPackage.ShowFileHistory(_filePath);
            };

            Canvas.SetLeft(label, viewLine.TextRight + 12);
            Canvas.SetTop(label, viewLine.TextTop);

            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, viewLine.Extent, null, label, null);
        }
    }
}
