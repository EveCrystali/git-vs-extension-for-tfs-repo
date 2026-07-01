using GitForTfs.Mvvm;
using GitForTfs.Services;

namespace GitForTfs.ViewModels
{
    /// <summary>Row in the "History" list.</summary>
    public sealed class CommitItemViewModel : ViewModelBase
    {
        public CommitItemViewModel(GitCommit commit)
        {
            Commit = commit;
        }

        public GitCommit Commit { get; }

        public string ShortHash => Commit.ShortHash;

        public string Subject => Commit.Subject;

        public string Author => Commit.Author;

        public string RelativeDate => Commit.RelativeDate;

        public string ToolTip => $"{Commit.FullHash}\n{Commit.Author} — {Commit.RelativeDate}\n\n{Commit.Subject}";
    }
}
