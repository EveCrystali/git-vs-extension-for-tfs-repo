using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace GitForTfs.CodeLens
{
    /// <summary>
    /// Exports a CodeLens data point provider that shows, above each method/property/type,
    /// the author and date of the last git commit that touched it — the equivalent of the
    /// built-in Git CodeLens, which is unavailable when TFVC is the active source control
    /// provider. Runs out-of-process in the CodeLens host and talks to git directly.
    /// </summary>
    [Export(typeof(IAsyncCodeLensDataPointProvider))]
    [Name(Id)]
    // "code" is the umbrella content type the CodeLens engine matches against (the
    // official CodeLens sample registers on it); CSharp/Basic derive from it. Registering
    // on all three keeps the lens firing regardless of how the descriptor is content-typed.
    [ContentType("code")]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [Priority(200)]
    public sealed class GitBlameCodeLensProvider : IAsyncCodeLensDataPointProvider
    {
        internal const string Id = "GitForTfs.GitBlame";

        public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
        {
            var kindOk = descriptor != null
                && (descriptor.Kind == CodeElementKinds.Method
                    || descriptor.Kind == CodeElementKinds.Property
                    || descriptor.Kind == CodeElementKinds.Type);
            var inRepo = descriptor != null && GitBlameReader.IsInRepository(descriptor.FilePath);
            var supported = descriptor != null
                && !string.IsNullOrEmpty(descriptor.FilePath)
                && kindOk
                && inRepo;

            Diag.Log($"CanCreate kind={descriptor?.Kind} file='{descriptor?.FilePath}' kindOk={kindOk} inRepo={inRepo} => {supported}");

            return Task.FromResult(supported);
        }

        public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
        {
            return Task.FromResult<IAsyncCodeLensDataPoint>(new GitBlameDataPoint(descriptor));
        }
    }

    /// <summary>The per-element data point that produces the inline blame text.</summary>
    public sealed class GitBlameDataPoint : IAsyncCodeLensDataPoint
    {
        // Remember the last blame we computed so the expandable details view can reuse it
        // instead of running git a second time when the user clicks the lens.
        private BlameInfo _lastBlame;

        public GitBlameDataPoint(CodeLensDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public CodeLensDescriptor Descriptor { get; }

#pragma warning disable 0067 // The lens content is static per element; we never raise invalidation.
        public event AsyncEventHandler InvalidatedAsync;
#pragma warning restore 0067

        public async Task<CodeLensDataPointDescriptor> GetDataAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
        {
            var blame = await ComputeBlameAsync(descriptorContext, token).ConfigureAwait(false);
            if (blame == null)
                return null;

            var summary = blame.Summary ?? string.Empty;
            var shortSummary = Truncate(summary, 60);

            // Inline text mirrors GitLens: "author, N ago • commit subject".
            var description = string.IsNullOrEmpty(shortSummary)
                ? $"{blame.Author}, {blame.RelativeDate}"
                : $"{blame.Author}, {blame.RelativeDate}  •  {shortSummary}";

            return new CodeLensDataPointDescriptor
            {
                Description = description,
                TooltipText = $"{blame.Author} committed {blame.RelativeDate}\n{blame.ShortHash}  {summary}",
                IntValue = null,
            };
        }

        public async Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
        {
            // Clicking the lens expands this grid: hash / author / when / message.
            var blame = _lastBlame ?? await ComputeBlameAsync(descriptorContext, token).ConfigureAwait(false);
            if (blame == null)
                return null;

            return new CodeLensDetailsDescriptor
            {
                Headers = new List<CodeLensDetailHeaderDescriptor>
                {
                    new CodeLensDetailHeaderDescriptor { UniqueName = "commit",  DisplayName = "Commit",  Width = 0.15 },
                    new CodeLensDetailHeaderDescriptor { UniqueName = "author",  DisplayName = "Author",  Width = 0.20 },
                    new CodeLensDetailHeaderDescriptor { UniqueName = "when",    DisplayName = "When",    Width = 0.20 },
                    new CodeLensDetailHeaderDescriptor { UniqueName = "message", DisplayName = "Message", Width = 0.45 },
                },
                Entries = new List<CodeLensDetailEntryDescriptor>
                {
                    new CodeLensDetailEntryDescriptor
                    {
                        Fields = new List<CodeLensDetailEntryField>
                        {
                            new CodeLensDetailEntryField { Text = blame.ShortHash },
                            new CodeLensDetailEntryField { Text = blame.Author },
                            new CodeLensDetailEntryField { Text = blame.RelativeDate },
                            new CodeLensDetailEntryField { Text = blame.Summary ?? string.Empty },
                        },
                        Tooltip = blame.Summary,
                    },
                },
            };
        }

        private async Task<BlameInfo> ComputeBlameAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
        {
            // The applicable span (character offsets in the current buffer) is the reliable
            // source of the element's location across SDK versions.
            Span? span = descriptorContext?.ApplicableSpan;
            Diag.Log($"ComputeBlame span={(span == null ? "null" : span.Value.Start + ".." + span.Value.End)} file='{Descriptor?.FilePath}'");
            if (span == null)
                return null;

            var (startLine, endLine) = GitBlameReader.MapSpanToLines(Descriptor.FilePath, span.Value.Start, span.Value.End);

            var blame = await GitBlameReader
                .GetLastChangeAsync(Descriptor.FilePath, startLine, endLine, token)
                .ConfigureAwait(false);

            _lastBlame = blame;
            return blame;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;
            return value.Substring(0, max - 1).TrimEnd() + "…";
        }
    }
}
