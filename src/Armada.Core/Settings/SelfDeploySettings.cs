namespace Armada.Core.Settings
{
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
        /// Optional supervisor script path relative to WorkingDirectory.
        /// When null, scripts/admiral-watchdog.ps1 on Windows or scripts/admiral-watchdog.sh elsewhere.
        /// </summary>
        public string? SupervisorScriptRelativePath { get; set; }

        /// <summary>
        /// Server DLL path relative to WorkingDirectory used by the supervisor to start the new admiral.
        /// </summary>
        public string ServerDllRelativePath { get; set; } = "src/Armada.Server/bin/Release/net10.0/Armada.Server.dll";

        private int _DebounceSeconds = 30;
        private int _MergeQueueDrainTimeoutSeconds = 300;
        private int _BuildTimeoutSeconds = 600;
    }
}
