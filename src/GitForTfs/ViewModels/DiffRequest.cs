namespace GitForTfs.ViewModels
{
    /// <summary>
    /// Describes a two-file comparison to hand off to Visual Studio's built-in diff viewer.
    /// The view model prepares the (temporary) files; the tool window supplies the actual
    /// <c>IVsDifferenceService</c> integration.
    /// </summary>
    public sealed class DiffRequest
    {
        public DiffRequest(string leftFile, string rightFile, string caption, string leftLabel, string rightLabel,
            bool leftIsTemporary = true, bool rightIsTemporary = true)
        {
            LeftFile = leftFile;
            RightFile = rightFile;
            Caption = caption;
            LeftLabel = leftLabel;
            RightLabel = rightLabel;
            LeftIsTemporary = leftIsTemporary;
            RightIsTemporary = rightIsTemporary;
        }

        /// <summary>Path of the "before" file (left pane). Both files may be temporary.</summary>
        public string LeftFile { get; }

        /// <summary>Path of the "after" file (right pane).</summary>
        public string RightFile { get; }

        public string Caption { get; }

        public string LeftLabel { get; }

        public string RightLabel { get; }

        /// <summary>
        /// When true, Visual Studio may delete <see cref="LeftFile"/> when the diff closes.
        /// Must be false for a real working-tree file so the user's file is never removed.
        /// </summary>
        public bool LeftIsTemporary { get; }

        /// <summary>When true, Visual Studio may delete <see cref="RightFile"/> when the diff closes.</summary>
        public bool RightIsTemporary { get; }
    }
}
