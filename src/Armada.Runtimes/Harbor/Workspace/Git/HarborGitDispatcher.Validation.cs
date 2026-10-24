namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using System;
    using System.IO;
    using System.Text.RegularExpressions;
    using Armada.Core.Harbor;

    public static partial class HarborGitDispatcher
    {
        private static string Required(string? value, string field, int maxLength = 4096)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > maxLength || HasControl(value))
                throw Invalid("input_" + field + "_invalid");
            return value;
        }

        private static string RequiredRevision(string? value) => ValidateRevision(Required(value, "revision", 1024));

        private static string ValidateRevision(string value)
        {
            if (value[0] == '-' || value.Contains("..", StringComparison.Ordinal) || value.Contains("@{", StringComparison.Ordinal))
                throw Invalid("input_revision_invalid");
            return value;
        }

        private static string RequiredBranch(string? value)
        {
            string branch = Required(value, "branch", 1024);
            if (branch[0] == '-' || branch[0] == '/' || branch.EndsWith("/", StringComparison.Ordinal)
                || branch.EndsWith(".", StringComparison.Ordinal) || branch.Contains("..", StringComparison.Ordinal)
                || branch.Contains("//", StringComparison.Ordinal) || branch.Contains("@{", StringComparison.Ordinal)
                || branch.Contains('~') || branch.Contains('^') || branch.Contains(':') || branch.Contains('?')
                || branch.Contains('*') || branch.Contains('[') || branch.Contains('\\') || branch.Contains(' ')
                || branch == "@" || branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                throw Invalid("input_branch_invalid");
            foreach (string segment in branch.Split('/'))
            {
                if (segment.StartsWith(".", StringComparison.Ordinal)
                    || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                    throw Invalid("input_branch_invalid");
            }
            return branch;
        }

        private static string RequiredCommit(string? value, string field = "commit")
        {
            string commit = Required(value, field, 64);
            if (!Regex.IsMatch(commit, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant))
                throw Invalid("input_" + field + "_must_be_full_commit");
            return commit;
        }

        private static string RequiredRelativePath(string? value)
        {
            string path = Required(value, "relative_path", 4096).Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(path, @"\A[A-Za-z]:", RegexOptions.CultureInvariant))
                throw Invalid("input_relative_path_invalid");
            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || String.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
                    throw Invalid("input_relative_path_invalid");
            }
            return path;
        }

        private static void RequireCanonicalPath(string? path, string name)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || HasControl(path))
                throw Invalid("input_" + name.Replace(' ', '_') + "_path_invalid");
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                throw Invalid("input_" + name.Replace(' ', '_') + "_path_invalid");
            }
            if (!String.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw Invalid("input_" + name.Replace(' ', '_') + "_path_not_canonical");
        }

        private static void ValidateBounds(HarborGitOperationRequest request)
        {
            if (request.MaxCount < 1 || request.MaxCount > 1000) throw Invalid("input_max_count_out_of_range");
            if (request.MaxSamples < 1 || request.MaxSamples > 100) throw Invalid("input_max_samples_out_of_range");
            if (request.Line < 1 || request.Line > 100000000) throw Invalid("input_line_out_of_range");
            if (request.MaxLines < 1 || request.MaxLines > 2000) throw Invalid("input_max_lines_out_of_range");
            if (request.TimeoutMilliseconds < 1 || request.TimeoutMilliseconds > 120000) throw Invalid("input_timeout_out_of_range");
            if (request.OutputLimitBytes < 1 || request.OutputLimitBytes > 4 * 1024 * 1024) throw Invalid("input_output_limit_out_of_range");
        }

        private static bool HasControl(string value)
        {
            foreach (char character in value) if (Char.IsControl(character)) return true;
            return false;
        }

        private static InvalidOperationException Invalid(string reason) => new InvalidOperationException("harbor_git_request_invalid: " + reason);
        private static NotSupportedException Unsupported(string reason) => new NotSupportedException("harbor_git_operation_unsupported: " + reason);
    }
}
