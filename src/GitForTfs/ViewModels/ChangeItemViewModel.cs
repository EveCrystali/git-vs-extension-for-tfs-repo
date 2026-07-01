using System.IO;
using GitForTfs.Mvvm;
using GitForTfs.Services;

namespace GitForTfs.ViewModels
{
    /// <summary>Row in the "Changes" list representing one modified/added/deleted file.</summary>
    public sealed class ChangeItemViewModel : ViewModelBase
    {
        public ChangeItemViewModel(GitChange change)
        {
            Change = change;
        }

        public GitChange Change { get; }

        public string Path => Change.Path;

        public string FileName => System.IO.Path.GetFileName(Change.Path);

        public string Directory
        {
            get
            {
                var dir = System.IO.Path.GetDirectoryName(Change.Path);
                return string.IsNullOrEmpty(dir) ? string.Empty : dir;
            }
        }

        public string StatusText => Change.StatusText;

        public string StatusGlyph => Change.StatusCode.ToString();

        public bool IsStaged => Change.Stage == GitChangeStage.Staged;

        /// <summary>Tooltip that includes the rename source when relevant.</summary>
        public string ToolTip =>
            string.IsNullOrEmpty(Change.OriginalPath)
                ? $"{Change.StatusText}: {Change.Path}"
                : $"{Change.StatusText}: {Change.OriginalPath} -> {Change.Path}";
    }
}
