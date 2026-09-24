namespace Armada.Core.Authorization
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for vessel fields that name a place on the server's own disk.
    ///
    /// <see cref="Vessel.LocalPath"/> and <see cref="Vessel.WorkingDirectory"/> are server paths: branch listing,
    /// git status, workspace file access, check runs and vessel removal all act on them, and removal deletes
    /// <see cref="Vessel.LocalPath"/> recursively. A repository URL that is a local path or a file URL makes the
    /// server clone from its own disk. Only a global administrator may name such a path; for every other caller
    /// the paths are server-owned.
    ///
    /// <see cref="Vessel.Name"/> becomes a directory name (the managed bare repository and the dock root are built
    /// from it), so it must be one safe path segment for every caller.
    /// </summary>
    public static class VesselPathPolicy
    {
        #region Public-Members

        /// <summary>
        /// Refusal message for a caller that names a server path it may not set.
        /// </summary>
        public const string ServerPathRefusal =
            "Only a global administrator can set localPath or workingDirectory; for other callers these server paths are server-owned.";

        /// <summary>
        /// Refusal message for a caller that names a local repository URL it may not set.
        /// </summary>
        public const string LocalRepoUrlRefusal =
            "Only a global administrator can use a local filesystem path or file URL as repoUrl.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Validate a vessel name as one safe path segment.
        /// </summary>
        /// <param name="name">Proposed vessel name.</param>
        /// <returns>Null when the name is safe, otherwise the reason it is refused.</returns>
        public static string? ValidateName(string? name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "Vessel name is required.";
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                return "Vessel name must not contain a path separator.";
            if (name.Contains("..", StringComparison.Ordinal))
                return "Vessel name must not contain '..'.";
            if (name.StartsWith(".", StringComparison.Ordinal))
                return "Vessel name must not start with '.'.";
            if (name.IndexOf(':') >= 0)
                return "Vessel name must not contain ':'.";
            foreach (char c in name)
            {
                if (Char.IsControl(c)) return "Vessel name must not contain control characters.";
            }

            return null;
        }

        /// <summary>
        /// True when git would read the repository URL from the server's own disk, or through a transport helper,
        /// rather than from a network remote.
        /// </summary>
        /// <param name="repoUrl">Repository URL.</param>
        /// <returns>True for a local path, a file URL, or a transport-helper address.</returns>
        public static bool IsServerLocalRepoUrl(string? repoUrl)
        {
            if (String.IsNullOrWhiteSpace(repoUrl)) return false;
            string url = repoUrl.Trim();

            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;

            // <transport>::<address> runs a remote helper program.
            if (url.Contains("::", StringComparison.Ordinal)) return true;

            int schemeIndex = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex > 0) return false;

            // scp-like syntax (user@host:path) is a network remote when the colon comes before any slash and the
            // part before it is not a single drive letter.
            int colon = url.IndexOf(':');
            int slash = url.IndexOfAny(new[] { '/', '\\' });
            if (colon > 1 && (slash < 0 || colon < slash)) return false;

            return true;
        }

        /// <summary>
        /// Decide whether a caller may create the requested vessel.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="requested">Requested vessel.</param>
        /// <param name="isForbidden">True when the refusal is a permission refusal rather than invalid input.</param>
        /// <returns>Null when the vessel may be created, otherwise the reason it is refused.</returns>
        public static string? ValidateCreate(AuthContext caller, Vessel requested, out bool isForbidden)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            isForbidden = false;

            string? nameError = ValidateName(requested.Name);
            if (nameError != null) return nameError;

            if (caller.IsAdmin) return null;

            isForbidden = true;
            if (!String.IsNullOrWhiteSpace(requested.LocalPath) || !String.IsNullOrWhiteSpace(requested.WorkingDirectory))
                return ServerPathRefusal;
            if (IsServerLocalRepoUrl(requested.RepoUrl))
                return LocalRepoUrlRefusal;

            isForbidden = false;
            return null;
        }

        /// <summary>
        /// Apply the rule to a full vessel update. For a caller that is not a global administrator the stored server
        /// paths are kept, whatever the request carries.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="existing">Stored vessel.</param>
        /// <param name="updated">Requested update; changed in place.</param>
        /// <param name="isForbidden">True when the refusal is a permission refusal rather than invalid input.</param>
        /// <returns>Null when the update may proceed, otherwise the reason it is refused.</returns>
        public static string? ApplyUpdate(AuthContext caller, Vessel existing, Vessel updated, out bool isForbidden)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (updated == null) throw new ArgumentNullException(nameof(updated));
            isForbidden = false;

            if (!String.Equals(existing.Name, updated.Name, StringComparison.Ordinal))
            {
                string? nameError = ValidateName(updated.Name);
                if (nameError != null) return nameError;
            }

            if (caller.IsAdmin) return null;

            updated.LocalPath = existing.LocalPath;
            updated.WorkingDirectory = existing.WorkingDirectory;

            if (!String.Equals(existing.RepoUrl, updated.RepoUrl, StringComparison.Ordinal)
                && IsServerLocalRepoUrl(updated.RepoUrl))
            {
                isForbidden = true;
                return LocalRepoUrlRefusal;
            }

            return null;
        }

        #endregion
    }
}
