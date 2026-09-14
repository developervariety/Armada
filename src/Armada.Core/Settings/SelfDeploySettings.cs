namespace Armada.Core.Settings
{
    using Armada.Core.Models;

    /// <summary>
    /// Opt-in settings for rebuilding and supervised restart when the self-hosted
    /// armada vessel lands to its own default branch.
    /// </summary>
    public sealed class SelfDeploySettings
    {
        /// <summary>
        /// Master enable. When false, self-deploy is a no-op regardless of other fields.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Optional vessel id pin. When set, only lands for this vessel trigger self-deploy.
        /// When null, the running admiral resolves the self vessel from WorkingDirectory.
        /// </summary>
        public string? SelfVesselId { get; set; }

        /// <summary>
        /// Debounce interval in seconds for coalescing burst lands into one deploy.
        /// </summary>
        public int DebounceSeconds
        {
            get => _DebounceSeconds;
            set => _DebounceSeconds = Math.Clamp(value, 0, 600);
        }

        /// <summary>
        /// Maximum seconds to wait for in-flight merge-queue landing work to finish
        /// before attempting rebuild and restart.
        /// </summary>
        public int MergeQueueDrainTimeoutSeconds
        {
            get => _MergeQueueDrainTimeoutSeconds;
            set => _MergeQueueDrainTimeoutSeconds = Math.Clamp(value, 5, 3600);
        }

        /// <summary>
        /// Release build timeout in seconds.
        /// </summary>
        public int BuildTimeoutSeconds
        {
            get => _BuildTimeoutSeconds;
            set => _BuildTimeoutSeconds = Math.Clamp(value, 60, 7200);
        }

        /// <summary>
        /// Maximum seconds either side of the admiral-supervisor handshake waits for the other.
        /// A timeout aborts the cutover while the running admiral stays the owner.
        /// </summary>
        public int HandshakeTimeoutSeconds
        {
            get => _HandshakeTimeoutSeconds;
            set => _HandshakeTimeoutSeconds = Math.Clamp(value, 5, 300);
        }

        /// <summary>
        /// Maximum seconds the supervisor waits for the previous admiral to exit before terminating
        /// that exact process. The candidate never starts until the exit is confirmed.
        /// </summary>
        public int OldProcessExitTimeoutSeconds
        {
            get => _OldProcessExitTimeoutSeconds;
            set => _OldProcessExitTimeoutSeconds = Math.Clamp(value, 5, 900);
        }

        /// <summary>
        /// Maximum seconds a launched candidate or rollback server has to prove health.
        /// </summary>
        public int HealthTimeoutSeconds
        {
            get => _HealthTimeoutSeconds;
            set => _HealthTimeoutSeconds = Math.Clamp(value, 10, 1800);
        }

        /// <summary>
        /// Number of previous releases kept in the release store beyond the running release, the rollback
        /// release, and every release named by an unresolved restart record.
        /// </summary>
        public int RetainedPreviousReleases
        {
            get => _RetainedPreviousReleases;
            set => _RetainedPreviousReleases = Math.Clamp(value, 0, 20);
        }

        /// <summary>
        /// Backup directory visible to the SQL Server host, required when the admiral uses SQL Server.
        /// Without it the default preflight fails and no cutover runs.
        /// </summary>
        public string? SqlServerBackupDirectory { get; set; }

        /// <summary>
        /// Solution path relative to the vessel WorkingDirectory.
        /// </summary>
        public string SolutionRelativePath { get; set; } = "src/Armada.sln";

        /// <summary>
        /// MSBuild configuration passed to dotnet build.
        /// </summary>
        public string BuildConfiguration { get; set; } = "Release";

        /// <summary>
        /// Target framework passed to dotnet build.
        /// </summary>
        public string TargetFramework { get; set; } = "net10.0";

        /// <summary>
        /// Server DLL path relative to WorkingDirectory. Its directory is captured as the immutable candidate artifact.
        /// </summary>
        public string ServerDllRelativePath { get; set; } = "src/Armada.Server/bin/Release/net10.0/Armada.Server.dll";

        /// <summary>
        /// Cutover bounds derived from these settings.
        /// </summary>
        /// <returns>Cutover options.</returns>
        public SelfDeployCutoverOptions ToCutoverOptions()
        {
            return new SelfDeployCutoverOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(HandshakeTimeoutSeconds),
                OldProcessExitTimeout = TimeSpan.FromSeconds(OldProcessExitTimeoutSeconds),
                HealthTimeout = TimeSpan.FromSeconds(HealthTimeoutSeconds)
            };
        }

        private int _DebounceSeconds = 30;
        private int _MergeQueueDrainTimeoutSeconds = 300;
        private int _BuildTimeoutSeconds = 600;
        private int _HandshakeTimeoutSeconds = 30;
        private int _OldProcessExitTimeoutSeconds = 120;
        private int _HealthTimeoutSeconds = 120;
        private int _RetainedPreviousReleases = 2;
    }
}
