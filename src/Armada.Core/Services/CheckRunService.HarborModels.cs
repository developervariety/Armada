namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class CheckRunService
    {
        /// <summary>Execution facts and retained output from one Harbor-owned CheckRun.</summary>
        private sealed class HarborCheckExecution
        {
            /// <summary>Durable auxiliary dock used for the isolated check.</summary>
            public Dock Dock { get; set; } = null!;
            /// <summary>Immutable runner checkout identity used by the check.</summary>
            public HarborCheckoutBinding Binding { get; set; } = null!;
            /// <summary>Process, output, and verification metadata returned by the runner.</summary>
            public HarborWorkspaceOperationResult Result { get; set; } = null!;
            /// <summary>Decoded stdout and stderr retained for the CheckRun record.</summary>
            public string Output { get; set; } = String.Empty;
            /// <summary>Opaque runner checkout address stored with the CheckRun.</summary>
            public string WorkingDirectory { get; set; } = String.Empty;
        }

        /// <summary>Runner and repository grant selected for a Harbor-owned check.</summary>
        private sealed class HarborCheckRoute
        {
            /// <summary>Creates a route from one explicit vessel or mission binding.</summary>
            /// <param name="runnerId">Enrolled runner identifier.</param>
            /// <param name="repositoryGrantId">Runner-local grant for the vessel repository.</param>
            public HarborCheckRoute(string runnerId, string repositoryGrantId)
            {
                RunnerId = runnerId;
                RepositoryGrantId = repositoryGrantId;
            }

            /// <summary>Enrolled runner that owns the repository grant.</summary>
            public string RunnerId { get; }
            /// <summary>Configured repository grant selected for the check.</summary>
            public string RepositoryGrantId { get; }
        }

        /// <summary>Temporary local staging area for artifacts read from a Harbor runner.</summary>
        private sealed class HarborArtifactStage : IDisposable
        {
            /// <summary>Creates an artifact stage and its metadata list.</summary>
            /// <param name="directory">Temporary directory that contains staged files.</param>
            /// <param name="artifacts">Validated artifact metadata staged from the runner.</param>
            public HarborArtifactStage(string directory, List<CheckRunArtifact> artifacts)
            {
                Directory = directory;
                Artifacts = artifacts;
            }

            /// <summary>Temporary local directory containing the staged artifact bytes.</summary>
            public string Directory { get; }
            /// <summary>Validated metadata for staged artifacts.</summary>
            public List<CheckRunArtifact> Artifacts { get; }

            /// <summary>Removes the temporary staged artifact directory.</summary>
            public void Dispose()
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
