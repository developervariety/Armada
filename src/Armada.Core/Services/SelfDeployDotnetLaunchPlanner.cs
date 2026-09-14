namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Launches servers and the supervisor through <c>dotnet</c> from immutable artifacts.
    /// </summary>
    public sealed class SelfDeployDotnetLaunchPlanner : ISelfDeployLaunchPlanner
    {
        /// <summary>
        /// Server argument that runs the cutover supervisor for one operation.
        /// </summary>
        public const string SuperviseArgument = "--self-deploy-supervise";

        /// <summary>
        /// Server argument that drives an interrupted restart to a terminal state.
        /// </summary>
        public const string RecoverArgument = "--self-deploy-recover";

        /// <inheritdoc />
        public SelfDeployLaunchSpec ForServer(SelfDeployReleaseArtifact artifact, string operationId)
        {
            if (artifact == null) throw new ArgumentNullException(nameof(artifact));
            return new SelfDeployLaunchSpec
            {
                FileName = "dotnet",
                Arguments = new[] { Path.Combine(artifact.Directory, artifact.EntryAssembly) },
                WorkingDirectory = artifact.Directory,
                EnvironmentVariables = OperationEnvironment(operationId)
            };
        }

        /// <inheritdoc />
        public SelfDeployLaunchSpec ForSupervisor(SelfDeployReleaseArtifact rollback, string operationId)
        {
            if (rollback == null) throw new ArgumentNullException(nameof(rollback));
            return new SelfDeployLaunchSpec
            {
                FileName = "dotnet",
                Arguments = new[] { Path.Combine(rollback.Directory, rollback.EntryAssembly), SuperviseArgument, operationId },
                WorkingDirectory = rollback.Directory,
                EnvironmentVariables = OperationEnvironment(operationId)
            };
        }

        private static IReadOnlyDictionary<string, string> OperationEnvironment(string operationId)
        {
            if (String.IsNullOrWhiteSpace(operationId)) throw new ArgumentNullException(nameof(operationId));
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SelfDeployRestartRecordStore.OperationIdVariable] = operationId
            };
        }
    }
}
