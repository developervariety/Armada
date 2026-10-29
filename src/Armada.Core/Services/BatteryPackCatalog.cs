namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// Finds the battery directory and loads its packs. Packs are read from disk on every call so an edited manifest is
    /// picked up without a restart.
    /// </summary>
    public class BatteryPackCatalog
    {
        #region Public-Members

        /// <summary>
        /// Name of the battery directory searched for when none is configured.
        /// </summary>
        public const string DirectoryName = "battery";

        #endregion

        #region Private-Members

        private readonly Func<string?> _ConfiguredDirectory;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="configuredDirectory">Reads the configured battery directory (null or empty to search for one).</param>
        /// <exception cref="ArgumentNullException">Thrown when configuredDirectory is null.</exception>
        public BatteryPackCatalog(Func<string?> configuredDirectory)
        {
            _ConfiguredDirectory = configuredDirectory ?? throw new ArgumentNullException(nameof(configuredDirectory));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The battery directory in use: the configured one, else a "battery" folder next to the application or in one of
        /// its parent folders (a source checkout), else null.
        /// </summary>
        /// <returns>The directory, or null when none is found.</returns>
        public string? ResolveDirectory()
        {
            string? configured = _ConfiguredDirectory();
            if (!String.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

            foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                DirectoryInfo? current = String.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
                for (int depth = 0; current != null && depth < 10; depth++, current = current.Parent)
                {
                    string candidate = Path.Combine(current.FullName, DirectoryName);
                    if (HasPacks(candidate)) return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Every pack, valid or not, ordered by identifier.
        /// </summary>
        /// <returns>Packs.</returns>
        public List<BatteryPack> LoadAll()
        {
            return BatteryPackLoader.LoadAll(ResolveDirectory());
        }

        /// <summary>
        /// One pack by identifier.
        /// </summary>
        /// <param name="id">Pack identifier.</param>
        /// <returns>The pack, or null.</returns>
        public BatteryPack? Find(string? id)
        {
            if (String.IsNullOrWhiteSpace(id)) return null;
            return LoadAll().FirstOrDefault(p => String.Equals(p.Id, id.Trim(), StringComparison.Ordinal));
        }

        #endregion

        #region Private-Methods

        private static bool HasPacks(string directory)
        {
            if (!Directory.Exists(directory)) return false;
            return Directory.GetDirectories(directory).Any(d => File.Exists(Path.Combine(d, BatteryPackLoader.ManifestFileName)));
        }

        #endregion
    }
}
