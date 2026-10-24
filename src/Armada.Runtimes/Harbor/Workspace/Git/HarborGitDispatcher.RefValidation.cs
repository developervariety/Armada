namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using System;
    using System.Text.RegularExpressions;
    using Armada.Core.Harbor;

    public static partial class HarborGitDispatcher
    {
        private static string Remote(HarborGitOperationRequest request) =>
            String.IsNullOrWhiteSpace(request.RemoteName) ? "origin" : RequiredBranch(request.RemoteName);

        private static string? OptionalBranch(string? value) => String.IsNullOrWhiteSpace(value) ? null : RequiredBranch(value);

        private static string? Optional(string? value, string field, int maxLength) =>
            String.IsNullOrWhiteSpace(value) ? null : Required(value, field, maxLength);

        private static string? OptionalBranchPrefix(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string prefix = Required(value, "branch_prefix", 1024);
            bool trailingSlash = prefix.EndsWith("/", StringComparison.Ordinal);
            string branch = trailingSlash ? prefix.Substring(0, prefix.Length - 1) : prefix;
            return RequiredBranch(branch) + (trailingSlash ? "/" : String.Empty);
        }

        private static string RequiredHttpUrl(string? value)
        {
            string text = Required(value, "pull_request_url", 2048);
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw Invalid("input_pull_request_url_invalid");
            return text;
        }

        private static string RequiredRef(string? value)
        {
            string reference = Required(value, "ref", 1024);
            if (reference[0] == '-' || reference[0] == '/' || reference.EndsWith("/", StringComparison.Ordinal)
                || reference.EndsWith(".", StringComparison.Ordinal) || reference.StartsWith(".", StringComparison.Ordinal)
                || reference.Contains("..", StringComparison.Ordinal) || reference.Contains("@{", StringComparison.Ordinal)
                || reference.Contains("//", StringComparison.Ordinal) || reference.Contains('~') || reference.Contains('^')
                || reference.Contains(':') || reference.Contains('?') || reference.Contains('*') || reference.Contains('[')
                || reference.Contains('\\') || reference.Contains(' '))
                throw Invalid("input_ref_invalid");
            foreach (string segment in reference.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".."
                    || segment.StartsWith(".", StringComparison.Ordinal)
                    || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                    throw Invalid("input_ref_invalid");
            }
            return reference;
        }

        private static string RequiredRefPrefix(string? value)
        {
            string prefix = Required(value, "ref_prefix", 1024);
            string trimmed = prefix.TrimEnd('/');
            if (trimmed.Length == 0 || !prefix.StartsWith("refs/", StringComparison.Ordinal))
                throw Invalid("input_ref_prefix_invalid");
            RequiredRef(trimmed);
            if (!prefix.EndsWith("/", StringComparison.Ordinal))
                throw Invalid("input_ref_prefix_invalid");
            return prefix;
        }

        private static string RequiredPushSource(string? value)
        {
            string source = Required(value, "source_ref", 1024);
            if (Regex.IsMatch(source, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant))
                return source;
            return RequiredRef(source);
        }
    }
}
