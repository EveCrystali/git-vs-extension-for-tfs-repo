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
    [ContentType("CSharp")]
    [ContentType("Basic")]
    public sealed class GitBlameCodeLensProvider : IAsyncCodeLensDataPointProvider
    {
        internal const string Id = "GitForTfs.GitBlame";

        public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
        {
            var supported = descriptor != null
                && !string.IsNullOrEmpty(descriptor.FilePath)
                && (descriptor.Kind == CodeElementKinds.Method
                    || descriptor.Kind == CodeElementKinds.Property
                    || descriptor.Kind == CodeElementKinds.Type)
                && GitBlameReader.IsInRepository(descriptor.FilePath);

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
            var (startLine, endLine) = ResolveLineRange(descriptorContext);

            var blame = await GitBlameReader
                .GetLastChangeAsync(Descriptor.FilePath, startLine, endLine, token)
                .ConfigureAwait(false);

            if (blame == null)
                return null;

            return new CodeLensDataPointDescriptor
            {
                Description = $"{blame.Author}, {blame.RelativeDate}",
                TooltipText = $"Last git change: {blame.Author} — {blame.RelativeDate}\n{blame.ShortHash}  {blame.Summary}",
                IntValue = null,
            };
        }

        public Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
        {
            // No expandable details view; the inline text and tooltip carry the information.
            return Task.FromResult<CodeLensDetailsDescriptor>(null);
        }

        private (int start, int end) ResolveLineRange(CodeLensDescriptorContext descriptorContext)
        {
            Span span = descriptorContext?.ApplicableSpan ?? Descriptor.ApplicableToSpan;
            return GitBlameReader.MapSpanToLines(Descriptor.FilePath, span.Start, span.End);
        }
    }
}
