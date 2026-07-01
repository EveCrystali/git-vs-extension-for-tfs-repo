using System;

namespace GitForTfs.Services
{
    /// <summary>
    /// The staging state of a working-tree change, derived from the two-letter
    /// porcelain status code emitted by <c>git status --porcelain</c>.
    /// </summary>
    public enum GitChangeStage
    {
        /// <summary>Change is present in the index (green in most git UIs).</summary>
        Staged,

        /// <summary>Change exists only in the working tree (red in most git UIs).</summary>
        Unstaged,

        /// <summary>File is not tracked by git at all.</summary>
        Untracked
    }

    /// <summary>
    /// A single entry produced by <c>git status --porcelain=v1</c>.
    /// </summary>
    public sealed class GitChange
    {
        public GitChange(string path, string originalPath, char statusCode, GitChangeStage stage)
        {
            Path = path;
            OriginalPath = originalPath;
            StatusCode = statusCode;
            Stage = stage;
        }

        /// <summary>Repository-relative path of the file.</summary>
        public string Path { get; }

        /// <summary>For renames/copies, the previous path; otherwise <c>null</c>.</summary>
        public string OriginalPath { get; }

        /// <summary>Single-letter git status code (M, A, D, R, C, U, ?).</summary>
        public char StatusCode { get; }

        public GitChangeStage Stage { get; }

        /// <summary>Human readable description of <see cref="StatusCode"/>.</summary>
        public string StatusText
        {
            get
            {
                switch (StatusCode)
                {
                    case 'M': return "Modified";
                    case 'A': return "Added";
                    case 'D': return "Deleted";
                    case 'R': return "Renamed";
                    case 'C': return "Copied";
                    case 'U': return "Conflict";
                    case '?': return "Untracked";
                    case '!': return "Ignored";
                    case 'T': return "Type changed";
                    default: return StatusCode.ToString();
                }
            }
        }
    }

    /// <summary>A local git branch.</summary>
    public sealed class GitBranch
    {
        public GitBranch(string name, bool isCurrent, string upstream, int ahead, int behind)
        {
            Name = name;
            IsCurrent = isCurrent;
            Upstream = upstream;
            Ahead = ahead;
            Behind = behind;
        }

        public string Name { get; }

        public bool IsCurrent { get; }

        /// <summary>Configured upstream tracking branch (e.g. <c>origin/main</c>) or <c>null</c>.</summary>
        public string Upstream { get; }

        public int Ahead { get; }

        public int Behind { get; }
    }

    /// <summary>A single commit as returned by <c>git log</c>.</summary>
    public sealed class GitCommit
    {
        public GitCommit(string fullHash, string shortHash, string author, string relativeDate, string subject)
        {
            FullHash = fullHash;
            ShortHash = shortHash;
            Author = author;
            RelativeDate = relativeDate;
            Subject = subject;
        }

        public string FullHash { get; }

        public string ShortHash { get; }

        public string Author { get; }

        public string RelativeDate { get; }

        public string Subject { get; }
    }
}
