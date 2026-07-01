using System;

namespace GitForTfs.Services
{
    /// <summary>
    /// Result of a single invocation of the <c>git</c> command line executable.
    /// </summary>
    public sealed class GitResult
    {
        public GitResult(string arguments, int exitCode, string standardOutput, string standardError)
        {
            Arguments = arguments ?? string.Empty;
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? string.Empty;
            StandardError = standardError ?? string.Empty;
        }

        /// <summary>The arguments that were passed to git (without the leading executable name).</summary>
        public string Arguments { get; }

        /// <summary>Process exit code. <c>0</c> means success for virtually every git command.</summary>
        public int ExitCode { get; }

        public string StandardOutput { get; }

        public string StandardError { get; }

        public bool Success => ExitCode == 0;

        /// <summary>
        /// Combined, trimmed output that is safe to display to the user. Prefers stdout,
        /// falls back to stderr (git writes progress and a lot of useful text to stderr).
        /// </summary>
        public string DisplayOutput
        {
            get
            {
                var stdout = StandardOutput.Trim();
                var stderr = StandardError.Trim();

                if (stdout.Length > 0 && stderr.Length > 0)
                    return stdout + Environment.NewLine + stderr;

                return stdout.Length > 0 ? stdout : stderr;
            }
        }
    }
}
