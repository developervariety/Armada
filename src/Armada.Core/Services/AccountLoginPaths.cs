namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Where subscription account logins live on the Admiral. Every account folder is derived on the server from the
    /// data directory and a validated account ID; a client never supplies a path.
    /// </summary>
    public static class AccountLoginPaths
    {
        #region Public-Members

        /// <summary>Folder under the data directory that holds one folder per account.</summary>
        public const string AccountsFolderName = "accounts";

        /// <summary>File inside a Cursor account folder that holds its API key.</summary>
        public const string CursorKeyFileName = "cursor-api-key";

        /// <summary>Longest accepted account ID.</summary>
        public const int MaxAccountIdLength = 64;

        #endregion

        #region Private-Members

        private static readonly Regex _SafeId = new Regex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the ID is a safe single folder name: a letter or digit, then letters, digits, hyphens, or
        /// underscores, at most 64 characters. Rejects separators, dots, and traversal.
        /// </summary>
        public static bool IsSafeAccountId(string? accountId)
        {
            return !String.IsNullOrEmpty(accountId) && _SafeId.IsMatch(accountId);
        }

        /// <summary>The root folder that holds every account folder.</summary>
        public static string AccountsRoot(string dataDirectory)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentNullException(nameof(dataDirectory));
            return Path.GetFullPath(Path.Combine(dataDirectory, AccountsFolderName));
        }

        /// <summary>The account's folder. Throws <see cref="ArgumentException"/> for an unsafe ID.</summary>
        public static string HomeFor(string dataDirectory, string accountId)
        {
            if (!IsSafeAccountId(accountId)) throw new ArgumentException("Account ID must be a safe folder name.", nameof(accountId));
            string root = AccountsRoot(dataDirectory);
            string home = Path.GetFullPath(Path.Combine(root, accountId));
            // Defence in depth: the ID pattern already forbids separators and dots.
            if (!String.Equals(Path.GetDirectoryName(home), root, StringComparison.Ordinal))
                throw new ArgumentException("Account folder escaped the accounts root.", nameof(accountId));
            return home;
        }

        /// <summary>The Cursor API key file for an account.</summary>
        public static string CursorKeyFileFor(string dataDirectory, string accountId)
        {
            return Path.Combine(HomeFor(dataDirectory, accountId), CursorKeyFileName);
        }

        /// <summary>
        /// True when the path is the Cursor key file of this account: absolute, already normalized, named
        /// <see cref="CursorKeyFileName"/>, in a folder named for the account and, when a root is given, in that root.
        /// </summary>
        public static bool IsAccountKeyFile(string accountId, string path, string? accountsRoot)
        {
            if (!IsSafeAccountId(accountId) || String.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            if (!String.Equals(full, path, StringComparison.Ordinal)) return false;
            if (!String.Equals(Path.GetFileName(full), CursorKeyFileName, StringComparison.Ordinal)) return false;
            string? folder = Path.GetDirectoryName(full);
            if (folder == null || !String.Equals(Path.GetFileName(folder), accountId, StringComparison.Ordinal)) return false;
            if (accountsRoot == null) return true;
            return String.Equals(Path.GetDirectoryName(folder), Path.GetFullPath(accountsRoot), StringComparison.Ordinal);
        }

        #endregion
    }
}
