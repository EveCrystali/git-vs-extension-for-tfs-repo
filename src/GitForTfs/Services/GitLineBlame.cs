using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitForTfs.Services
{
    /// <summary>The blame of a single line: who last touched it and when.</summary>
    internal sealed class LineBlame
    {
        public string Author { get; set; }
        public string ShortHash { get; set; }
        public string Summary { get; set; }
        public string RelativeDate { get; set; }
        public bool IsUncommitted { get; set; }

        /// <summary>The author's initials, e.g. "Antoine Javelle" → "AJ", "Test" → "T".</summary>
        public string AuthorInitials
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Author))
                    return "?";

                var parts = Author.Split(new[] { ' ', '.', '-', '_', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    return "?";
                if (parts.Length == 1)
                    return char.ToUpperInvariant(parts[0][0]).ToString();

                return string.Concat(
                    char.ToUpperInvariant(parts[0][0]),
                    char.ToUpperInvariant(parts[parts.Length - 1][0]));
            }
        }

        /// <summary>The one-line label shown at the end of the current editor line.
        /// No author here — the full author/hash live in the adornment's tooltip.</summary>
        public string InlineText =>
            string.IsNullOrEmpty(Summary)
                ? RelativeDate
                : $"{RelativeDate} • {Summary}";
    }

    /// <summary>
    /// Runs <c>git blame</c> for a single line, entirely off the UI thread. Used by the
    /// current-line blame adornment (an in-process editor component), so it must not depend
    /// on any Visual Studio service — it only shells out to <c>git.exe</c>.
    /// </summary>
    internal static class GitLineBlame
    {
        private static readonly ConcurrentDictionary<string, bool> RepoMembershipCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cheap, process-free check for whether a file lives in a git working tree.</summary>
        public static bool IsInRepository(string filePath)
        {
            var dir = SafeGetDirectory(filePath);
            if (dir == null)
                return false;

            return RepoMembershipCache.GetOrAdd(dir, d =>
            {
                var current = new DirectoryInfo(d);
                while (current != null)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                        File.Exists(Path.Combine(current.FullName, ".git")))
                        return true;
                    current = current.Parent;
                }
                return false;
            });
        }

        public static async Task<LineBlame> GetLineBlameAsync(string filePath, int line, CancellationToken cancellationToken)
        {
            var dir = SafeGetDirectory(filePath);
            if (dir == null || line < 1)
                return null;

            var args = string.Format(
                CultureInfo.InvariantCulture,
                "--no-pager blame --porcelain -L {0},{0} -- \"{1}\"",
                line, filePath);

            var output = await RunGitAsync(args, dir, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(output))
                return null;

            return Parse(output);
        }

        /// <summary>
        /// Blames the whole file in a single git call and returns every line's blame keyed by
        /// 1-based line number. Used to pre-fill the adornment's cache so the first visit to any
        /// line is instant. One heavier call up front instead of one small call per line.
        /// </summary>
        public static async Task<Dictionary<int, LineBlame>> GetFileBlameAsync(string filePath, CancellationToken cancellationToken)
        {
            var dir = SafeGetDirectory(filePath);
            if (dir == null)
                return null;

            var args = string.Format(CultureInfo.InvariantCulture, "--no-pager blame --porcelain -- \"{0}\"", filePath);

            var output = await RunGitAsync(args, dir, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(output))
                return null;

            return ParseFile(output);
        }

        // ------------------------------------------------------------------

        private static LineBlame Parse(string porcelain)
        {
            string hash = null, author = null, summary = null;
            long time = 0;

            using (var reader = new StringReader(porcelain))
            {
                string line;
                var first = true;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '\t')
                        continue;

                    var space = line.IndexOf(' ');
                    var key = space < 0 ? line : line.Substring(0, space);
                    var value = space < 0 ? string.Empty : line.Substring(space + 1);

                    if (first)
                    {
                        hash = key; // the first porcelain line starts with the commit hash
                        first = false;
                        continue;
                    }

                    switch (key)
                    {
                        case "author": author = value; break;
                        case "committer-time":
                            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out time);
                            break;
                        case "summary": summary = value; break;
                    }
                }
            }

            return string.IsNullOrEmpty(hash) ? null : BuildBlame(hash, author, time, summary);
        }

        /// <summary>
        /// Parses whole-file <c>git blame --porcelain</c> output into a per-line map. The
        /// extended commit headers (author/time/summary) appear only the first time a commit is
        /// seen, so they are remembered per SHA and reused for that commit's other lines.
        /// </summary>
        private static Dictionary<int, LineBlame> ParseFile(string porcelain)
        {
            var meta = new Dictionary<string, CommitMeta>(StringComparer.OrdinalIgnoreCase);
            var byLine = new Dictionary<int, LineBlame>();
            string currentSha = null;
            var currentLine = 0;

            using (var reader = new StringReader(porcelain))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0)
                        continue;

                    if (line[0] == '\t')
                    {
                        // The actual source line; attribute it to the current commit.
                        if (currentSha != null && currentLine > 0)
                        {
                            meta.TryGetValue(currentSha, out var m);
                            byLine[currentLine] = BuildBlame(currentSha, m.Author, m.Time, m.Summary);
                        }
                        continue;
                    }

                    var space = line.IndexOf(' ');
                    var key = space < 0 ? line : line.Substring(0, space);

                    if (IsHash(key))
                    {
                        // Header: "<sha> <origLine> <finalLine> [<count>]".
                        currentSha = key;
                        var parts = line.Split(' ');
                        if (parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var finalLine))
                            currentLine = finalLine;
                        if (!meta.ContainsKey(currentSha))
                            meta[currentSha] = new CommitMeta();
                        continue;
                    }

                    if (currentSha == null)
                        continue;

                    var value = space < 0 ? string.Empty : line.Substring(space + 1);
                    var entry = meta[currentSha];
                    switch (key)
                    {
                        case "author": entry.Author = value; break;
                        case "committer-time":
                            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t);
                            entry.Time = t;
                            break;
                        case "summary": entry.Summary = value; break;
                    }
                    meta[currentSha] = entry;
                }
            }

            return byLine.Count == 0 ? null : byLine;
        }

        private struct CommitMeta
        {
            public string Author;
            public long Time;
            public string Summary;
        }

        private static bool IsHash(string token)
        {
            if (token.Length != 40)
                return false;
            foreach (var c in token)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                    return false;
            }
            return true;
        }

        private static LineBlame BuildBlame(string hash, string author, long time, string summary)
        {
            var uncommitted = hash.Replace("0", string.Empty).Length == 0;
            return new LineBlame
            {
                Author = uncommitted ? "You" : (author ?? "Unknown"),
                ShortHash = uncommitted ? "0000000" : hash.Substring(0, Math.Min(7, hash.Length)),
                Summary = uncommitted ? string.Empty : (summary ?? string.Empty),
                RelativeDate = uncommitted ? "uncommitted" : ToRelativeDate(time),
                IsUncommitted = uncommitted,
            };
        }

        private static string ToRelativeDate(long unixSeconds)
        {
            if (unixSeconds <= 0)
                return string.Empty;

            var delta = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            if (delta.TotalSeconds < 60) return "just now";
            if (delta.TotalMinutes < 60) return Plural((int)delta.TotalMinutes, "minute");
            if (delta.TotalHours < 24) return Plural((int)delta.TotalHours, "hour");
            if (delta.TotalDays < 30) return Plural((int)delta.TotalDays, "day");
            if (delta.TotalDays < 365) return Plural((int)(delta.TotalDays / 30), "month");
            return Plural((int)(delta.TotalDays / 365), "year");
        }

        private static string Plural(int value, string unit)
        {
            if (value <= 0) value = 1;
            return value + " " + unit + (value == 1 ? "" : "s") + " ago";
        }

        private static string SafeGetDirectory(string filePath)
        {
            try
            {
                return string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(Path.GetFullPath(filePath));
            }
            catch { return null; }
        }

        private static async Task<string> RunGitAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
                {
                    process.Start();
                    // Drain both pipes: if stderr fills while unread, git blocks and we deadlock.
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    var exitTcs = new TaskCompletionSource<object>();
                    process.Exited += (s, e) => exitTcs.TrySetResult(null);
                    if (process.HasExited)
                        exitTcs.TrySetResult(null);

                    using (cancellationToken.Register(() => exitTcs.TrySetCanceled()))
                    {
                        await exitTcs.Task.ConfigureAwait(false);
                    }

                    var stdout = await stdoutTask.ConfigureAwait(false);
                    await stderrTask.ConfigureAwait(false);
                    return process.ExitCode == 0 ? stdout : null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
