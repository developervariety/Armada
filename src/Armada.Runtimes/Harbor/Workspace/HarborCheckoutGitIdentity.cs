namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using Armada.Core.Services;

    /// <summary>Checks the linked checkout's Git metadata before a host operation uses it.</summary>
    internal static class HarborCheckoutGitIdentity
    {
        /// <summary>Refuse a checkout whose linked Git metadata leaves its configured repository.</summary>
        /// <param name="checkout">Canonical checkout root.</param>
        /// <param name="repository">Canonical bare repository from the selected grant.</param>
        public static void Validate(string checkout, string repository)
        {
            string pointer = Path.Combine(checkout, ".git");
            string text = ReadMetadata(pointer);
            const string prefix = "gitdir: ";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) throw Invalid();
            string gitDirectory = Path.GetFullPath(text.Substring(prefix.Length), checkout);
            string worktrees = Path.Combine(Path.GetFullPath(repository), "worktrees");
            if (!PathContainment.IsWithin(worktrees, gitDirectory) || Same(gitDirectory, worktrees)) throw Invalid();
            EnsureNoLinks(gitDirectory);
            string common = Path.GetFullPath(ReadMetadata(Path.Combine(gitDirectory, "commondir")), gitDirectory);
            string backLink = Path.GetFullPath(ReadMetadata(Path.Combine(gitDirectory, "gitdir")), gitDirectory);
            if (!Same(common, Path.GetFullPath(repository)) || !Same(backLink, pointer)) throw Invalid();
        }

        private static string ReadMetadata(string path)
        {
            EnsureNoLinks(path);
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length < 1 || file.Length > 8192) throw Invalid();
            string value = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (value.Length == 0 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw Invalid();
            return value;
        }

        private static void EnsureNoLinks(string path)
        {
            string? current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool Same(string left, string right) => String.Equals(left, right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static HarborJobJournalException Invalid() => new HarborJobJournalException(
            "workspace_checkout_git_identity_mismatch", "The linked checkout Git metadata does not match its repository grant.");
    }
}
