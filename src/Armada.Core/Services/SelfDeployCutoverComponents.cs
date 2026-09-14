namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Collaborators the running admiral uses to prepare a supervised cutover.
    /// </summary>
    public sealed class SelfDeployCutoverComponents
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="processHost">Verified process host.</param>
        /// <param name="artifacts">Immutable artifact store.</param>
        /// <param name="records">Durable restart record store.</param>
        /// <param name="launchPlanner">Launch planner.</param>
        /// <param name="environment">Host facts.</param>
        /// <param name="options">Cutover bounds.</param>
        public SelfDeployCutoverComponents(
            ISelfDeployProcessHost processHost,
            ISelfDeployArtifactStore artifacts,
            SelfDeployRestartRecordStore records,
            ISelfDeployLaunchPlanner launchPlanner,
            ISelfDeployHostEnvironment environment,
            SelfDeployCutoverOptions options)
        {
            ProcessHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
            Artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            Records = records ?? throw new ArgumentNullException(nameof(records));
            LaunchPlanner = launchPlanner ?? throw new ArgumentNullException(nameof(launchPlanner));
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>Verified process host.</summary>
        public ISelfDeployProcessHost ProcessHost { get; }

        /// <summary>Immutable artifact store.</summary>
        public ISelfDeployArtifactStore Artifacts { get; }

        /// <summary>Durable restart record store.</summary>
        public SelfDeployRestartRecordStore Records { get; }

        /// <summary>Launch planner.</summary>
        public ISelfDeployLaunchPlanner LaunchPlanner { get; }

        /// <summary>Host facts.</summary>
        public ISelfDeployHostEnvironment Environment { get; }

        /// <summary>Cutover bounds.</summary>
        public SelfDeployCutoverOptions Options { get; }

        /// <summary>
        /// Production components rooted in the Armada data directory.
        /// </summary>
        /// <param name="dataDirectory">Armada data directory.</param>
        /// <param name="settings">Self-deploy settings.</param>
        /// <returns>Components.</returns>
        public static SelfDeployCutoverComponents CreateDefault(string dataDirectory, SelfDeploySettings settings)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentNullException(nameof(dataDirectory));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            string stateDirectory = SelfDeployRestartRecordStore.DirectoryFor(dataDirectory);
            return new SelfDeployCutoverComponents(
                new SelfDeployProcessHost(),
                new SelfDeployArtifactStore(Path.Combine(stateDirectory, "releases")),
                new SelfDeployRestartRecordStore(stateDirectory),
                new SelfDeployDotnetLaunchPlanner(),
                new SelfDeployHostEnvironment(),
                settings.ToCutoverOptions());
        }
    }
}
