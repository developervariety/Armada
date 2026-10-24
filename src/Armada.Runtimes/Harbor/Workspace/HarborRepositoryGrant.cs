namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>Explicit local authority for one repository and its Harbor-owned checkout root.</summary>
    public sealed class HarborRepositoryGrant
    {
        /// <summary>Stable grant identifier.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>Vessel identifier allowed to use this grant.</summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>Owning tenant identifier.</summary>
        public string TenantId { get; set; } = String.Empty;

        /// <summary>Owning user identifier.</summary>
        public string UserId { get; set; } = String.Empty;

        /// <summary>Absolute path to the already provisioned bare repository.</summary>
        public string RepositoryPath { get; set; } = String.Empty;

        /// <summary>Absolute parent directory for checkouts created under this grant.</summary>
        public string CheckoutRoot { get; set; } = String.Empty;

        /// <summary>Optional existing checkout path for read-only repository inspection.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Exact repository URL identity used to bind the grant to a vessel.</summary>
        public string RepositoryUrl { get; set; } = String.Empty;

        /// <summary>Administrator-configured verification command text keyed by a stable command name.</summary>
        public Dictionary<string, string> VerificationCommands { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Validate identifiers and existing non-linked repository roots.</summary>
        public void Validate()
        {
            ValidateIdentifier(Id, nameof(Id));
            ValidateIdentifier(VesselId, nameof(VesselId));
            ValidateIdentifier(TenantId, nameof(TenantId));
            ValidateIdentifier(UserId, nameof(UserId));
            if (String.IsNullOrWhiteSpace(RepositoryUrl) || RepositoryUrl.Length > 4096
                || RepositoryUrl.Contains("\r", StringComparison.Ordinal)
                || RepositoryUrl.Contains("\n", StringComparison.Ordinal))
                throw new ArgumentException("RepositoryUrl must be a non-empty repository identity.", nameof(RepositoryUrl));
            if (VerificationCommands == null) throw new ArgumentNullException(nameof(VerificationCommands));
            foreach (KeyValuePair<string, string> command in VerificationCommands)
            {
                if (String.IsNullOrWhiteSpace(command.Key) || command.Key.Length > 100
                    || String.IsNullOrWhiteSpace(command.Value) || command.Value.Length > 8192)
                    throw new ArgumentException("Verification command grants must have bounded names and command text.", nameof(VerificationCommands));
            }

            RequireExistingDirectory(RepositoryPath, nameof(RepositoryPath));
            RequireExistingDirectory(CheckoutRoot, nameof(CheckoutRoot));
            if (!String.IsNullOrWhiteSpace(WorkingDirectory))
            {
                RequireExistingDirectory(WorkingDirectory, nameof(WorkingDirectory));
            }
        }

        private static void ValidateIdentifier(string value, string name)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 160)
                throw new ArgumentException("Repository grant identifiers must contain between 1 and 160 characters.", name);
            foreach (char character in value)
                if (Char.IsControl(character)) throw new ArgumentException("Repository grant identifiers cannot contain control characters.", name);
        }

        private static void RequireExistingDirectory(string path, string name)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Repository grant paths must be absolute.", name);
            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("A repository grant directory does not exist.");
            string current = fullPath;
            while (true)
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Repository grant paths cannot contain symbolic links or reparse points.", name);
                string? parent = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(parent) || String.Equals(parent, current, PathComparison)) break;
                current = parent;
            }
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
