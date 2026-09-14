namespace Armada.Helm.Infrastructure
{
    using System.IO;
    using Armada.Core.Settings;

    /// <summary>
    /// The one settings loader Helm commands and the embedded Admiral share.
    /// </summary>
    internal static class HelmSettings
    {
        #region Internal-Methods

        /// <summary>
        /// Load settings from a file, writing defaults first when no file exists.
        /// </summary>
        /// <param name="path">Settings file path.</param>
        /// <param name="initialized">True when this call created the settings file.</param>
        /// <returns>The loaded settings.</returns>
        internal static ArmadaSettings LoadOrInitialize(string path, out bool initialized)
        {
            ArmadaSettings settings = ArmadaSettings.LoadAsync(path).GetAwaiter().GetResult();
            initialized = false;
            if (File.Exists(path)) return settings;

            settings.InitializeDirectories();
            settings.SaveAsync(path).GetAwaiter().GetResult();
            initialized = true;
            return settings;
        }

        #endregion
    }
}
