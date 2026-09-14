namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Builds the default self-deploy preflight: provider-native backup, restore into an owned isolated
    /// target, candidate <c>--validate-database</c> against that target, and owned cleanup. Missing
    /// utilities, private storage that cannot be verified, or an incomplete provider configuration make
    /// the preflight fail, so a cutover is refused.
    /// </summary>
    public static class SelfDeployNativePreflight
    {
        /// <summary>
        /// Directory for disposable preflight backups under a data directory.
        /// </summary>
        /// <param name="dataDirectory">Armada data directory.</param>
        /// <returns>Backup directory.</returns>
        public static string BackupDirectoryFor(string dataDirectory)
        {
            return Path.Combine(SelfDeployRestartRecordStore.DirectoryFor(dataDirectory), "backups");
        }

        /// <summary>
        /// Create the default preflight for the running admiral's database.
        /// </summary>
        /// <param name="settings">Armada settings.</param>
        /// <param name="commands">Optional native command runner.</param>
        /// <returns>Fail-closed native preflight.</returns>
        public static ISelfDeployPreflight CreateDefault(ArmadaSettings settings, ISelfDeployNativeCommandRunner? commands = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            ISelfDeployNativeCommandRunner runner = commands ?? new SelfDeployNativeCommandRunner();
            SelfDeployDatabaseBackupProvider backups = new SelfDeployDatabaseBackupProvider(
                settings.Database,
                BackupDirectoryFor(settings.DataDirectory),
                runner,
                settings.SelfDeploy.SqlServerBackupDirectory);
            SelfDeployCandidateProcessValidator candidate = new SelfDeployCandidateProcessValidator(backups, runner, settings.Database);
            return new SelfDeployBackupRestorePreflight(backups, candidate);
        }
    }
}
