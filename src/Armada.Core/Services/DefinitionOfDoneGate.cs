namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Evaluates whether a Worker mission's in-dock build and unit-test commands pass before
    /// the mission is accepted as complete. Missions that carry the configured doc-only opt-out
    /// marker are skipped without running any commands. Non-Worker personas are also skipped
    /// unless the settings explicitly list them under AppliedPersonas.
    /// </summary>
    public class DefinitionOfDoneGate
    {
        #region Private-Members

        private readonly string _Header = "[DefinitionOfDoneGate] ";
        private readonly DefinitionOfDoneSettings _Settings;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly IContainerRuntimeProbe? _ContainerRuntimeProbe;
        private readonly IGitService? _Git;
        private readonly DefinitionOfDoneFailureClassifier _FailureClassifier = new DefinitionOfDoneFailureClassifier();

        private const int _MAX_DIAGNOSTIC_TEXT_CHARS = 16000;
        private const int _MAX_SECTION_CHARS = 7800;
        private const int _MAX_LINE_CHARS = 2000;

        private static readonly Regex _SecretLikePattern = new Regex(
            @"(?:password|passwd|secret|token|key|credential|auth|api_key|apikey|access_key|private_key)\s*[=:]\s*\S+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with required dependencies.
        /// </summary>
        /// <param name="settings">Gate configuration.</param>
        /// <param name="database">Database driver for resolving vessel and workflow profile data.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="containerRuntimeProbe">
        /// Optional container-runtime probe. When supplied and a vessel declares a containerless
        /// unit-test command, the gate falls back to that command if no runtime is available.
        /// Null disables the pre-flight and preserves the original behavior.
        /// </param>
        /// <param name="gitService">
        /// Optional git seam used to provision declared consumers for verification. Null disables
        /// consumer verification entirely, preserving the original behavior for every caller that
        /// does not supply it.
        /// </param>
        public DefinitionOfDoneGate(
            DefinitionOfDoneSettings settings,
            DatabaseDriver database,
            LoggingModule logging,
            IContainerRuntimeProbe? containerRuntimeProbe = null,
            IGitService? gitService = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ContainerRuntimeProbe = containerRuntimeProbe;
            _Git = gitService;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Evaluate the definition-of-done gate for the specified mission and dock.
        /// Returns a skipped result when the gate does not apply; returns a passing result
        /// when all required commands succeed; returns a failing result with the command
        /// label, exit code, and output tail when any command fails.
        /// </summary>
        /// <param name="mission">The mission being completed.</param>
        /// <param name="dock">The captain's dock, used to locate the worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A <see cref="DefinitionOfDoneResult"/> describing the gate outcome.</returns>
        public async Task<DefinitionOfDoneResult> EvaluateAsync(
            Mission mission,
            Dock dock,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (dock == null) throw new ArgumentNullException(nameof(dock));

            if (!_Settings.Enabled)
                return DefinitionOfDoneResult.Skipped("DoD gate is disabled");

            if (!IsPersonaApplicable(mission.Persona))
                return DefinitionOfDoneResult.Skipped("persona '" + (mission.Persona ?? "(none)") + "' is not in AppliedPersonas");

            if (HasDocOnlyMarker(mission.Description))
                return DefinitionOfDoneResult.Skipped("mission description contains doc-only opt-out marker");

            string? worktreePath = dock.WorktreePath;
            if (String.IsNullOrWhiteSpace(worktreePath))
            {
                string diagnostic = BuildDiagnosticText("Dock has no WorktreePath; cannot run in-dock checks.");
                return DefinitionOfDoneResult.Fail(
                    "dock-setup",
                    -1,
                    diagnostic,
                    DefinitionOfDoneFailureClassEnum.Infra);
            }

            WorkflowProfile? profile = await ResolveProfileAsync(mission, token).ConfigureAwait(false);
            string? buildCommand = profile?.BuildCommand;
            string? testCommand = profile?.UnitTestCommand;

            if (String.IsNullOrWhiteSpace(buildCommand) && String.IsNullOrWhiteSpace(testCommand))
            {
                return DefinitionOfDoneResult.Fail(
                    "missing-commands",
                    -1,
                    BuildDiagnosticText("No BuildCommand or UnitTestCommand is configured on the vessel's workflow profile. " +
                    "Add a workflow profile for this vessel, or add '" + _Settings.DocOnlyMarker +
                    "' to the mission description to opt out of in-dock verification."),
                    DefinitionOfDoneFailureClassEnum.Infra);
            }

            // Serialize the expensive half host-wide. A gate runs the vessel's full build and unit-test
            // command, and two of those at once on one host produce a failure that looks like broken
            // code but is not: a burst of many simultaneous sub-millisecond failures across unrelated
            // test classes, classified Timeout, where the same command passes alone. The mission that
            // loses the race is marked Failed and its whole downstream pipeline is cancelled.
            //
            // Mission status cannot be the interlock. WorkProduced is persisted when the agent
            // finishes, before this gate runs, and the vessel-mutex query counts only
            // Assigned/InProgress -- so a vessel looks idle for the entire duration of its own gate
            // and the next mission is admitted underneath it. Widening that query to include
            // WorkProduced would deadlock every vessel holding a stranded WorkProduced mission.
            // The contended resource is the host, not the vessel, so the lock belongs here.
            //
            // A queued gate must keep its dock alive for the whole wait: the host-wide lock can sit
            // this gate behind another gate's full build+test run, and dock reclamation would delete
            // the worktree in between.
            // The lease is held across the queue wait and the command run, and released afterwards;
            // the disk-lifecycle sweep and DockService.ReclaimAsync both honor it.
            //
            // Check runs and merge-queue test runs share the same slot, so a gate never overlaps a
            // check or a merge-queue test either. See HostWideCommandLock.
            DockLeaseRegistry.Acquire(dock.Id);
            try
            {
                using (await HostWideCommandLock.AcquireAsync(token).ConfigureAwait(false))
                {
                    return await RunGateCommandsAsync(mission, profile, buildCommand, testCommand, worktreePath, token).ConfigureAwait(false);
                }
            }
            finally
            {
                DockLeaseRegistry.Release(dock.Id);
            }
        }

        #endregion

        #region Private-Methods

        private async Task<DefinitionOfDoneResult> RunGateCommandsAsync(
            Mission mission,
            WorkflowProfile? profile,
            string? buildCommand,
            string? testCommand,
            string worktreePath,
            CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(buildCommand))
            {
                string effectiveBuild = _Settings.RunRestoreBeforeBuild ? EnsureRestore(buildCommand) : buildCommand;
                DefinitionOfDoneResult buildResult = await RunCommandAsync("build", effectiveBuild, worktreePath, token).ConfigureAwait(false);
                if (!buildResult.Passed)
                    return buildResult;
            }

            if (!String.IsNullOrWhiteSpace(testCommand))
            {
                // Container pre-flight. Without a runtime, every container-backed fixture fails and the
                // gate spends its whole timeout proving the environment is missing. When the vessel has
                // declared a containerless variant, run that instead so the gate still verifies
                // everything that does not need containers rather than reporting a blanket failure.
                string selectedTest = testCommand!;
                string testLabel = "unit-test";
                if (!String.IsNullOrWhiteSpace(profile?.ContainerlessUnitTestCommand)
                    && _ContainerRuntimeProbe != null
                    && !await _ContainerRuntimeProbe.IsAvailableAsync(worktreePath, token).ConfigureAwait(false))
                {
                    selectedTest = profile!.ContainerlessUnitTestCommand!;
                    testLabel = "unit-test (containerless)";
                    _Logging.Warn(_Header + "no container runtime detected; running the vessel's containerless unit-test command");
                }

                string effectiveTest = _Settings.RunRestoreBeforeBuild ? EnsureRestore(selectedTest) : selectedTest;
                DefinitionOfDoneResult testResult = await RunCommandAsync(testLabel, effectiveTest, worktreePath, token).ConfigureAwait(false);
                if (!testResult.Passed)
                    return testResult;
            }

            // The vessel's own build and tests pass. That says nothing about the repositories that
            // compile against it, so verify those before the branch is allowed to land.
            return await VerifyDeclaredConsumersAsync(mission, worktreePath, token).ConfigureAwait(false);
        }

        /// <summary>
        /// How long a single gate command may run before it is cancelled.
        /// </summary>
        /// <remarks>
        /// Read from settings, where <c>CommandTimeoutSeconds</c> has a deliberate 30-second
        /// floor: a shorter production timeout would kill legitimate builds. That floor also made
        /// the "a hanging command is interrupted" test wait a real 30 seconds, which was the single
        /// slowest test in the suite. Overriding this seam lets that test prove the same behavior
        /// in about a second without lowering the floor for real captains.
        /// </remarks>
        /// <returns>Timeout for one command.</returns>
        protected virtual TimeSpan ResolveCommandTimeout()
        {
            return TimeSpan.FromSeconds(_Settings.CommandTimeoutSeconds);
        }

        /// <summary>
        /// Build every vessel that declares the mission's vessel as a sibling repository.
        /// </summary>
        /// <remarks>
        /// The producer's change is still unlanded here, which is the whole point: this is the
        /// last moment at which a public-API break can be attributed to the change that caused
        /// it rather than to whatever happens to build next.
        /// <para>
        /// Each consumer is materialized under a scratch root unique to this verification. That
        /// is deliberate and not merely tidy: shared sibling checkouts are reused rather than
        /// re-pointed when another dock already owns them, so a shared path could leave the
        /// consumer compiling against some other commit while reporting on this one. A private
        /// root cannot be reused by anyone, so what is built is always what was asked for.
        /// </para>
        /// <para>
        /// The producer is checked out detached at the mission branch; the consumer's other
        /// declared siblings take their default branches, because only the producer is under
        /// test. Consumers are built, not tested: a build catches the break that leaves a target
        /// branch red, while running every consumer's suite inside every producer gate would cost
        /// more wall time than the gate itself.
        /// </para>
        /// </remarks>
        private async Task<DefinitionOfDoneResult> VerifyDeclaredConsumersAsync(
            Mission mission,
            string producerWorktreePath,
            CancellationToken token)
        {
            if (!_Settings.VerifyDeclaredConsumers) return DefinitionOfDoneResult.Pass();

            // No git seam means no way to provision a consumer. Skipping is correct; failing would
            // block every gate in a host that simply did not wire the dependency.
            if (_Git == null) return DefinitionOfDoneResult.Pass();

            if (String.IsNullOrWhiteSpace(mission.VesselId)) return DefinitionOfDoneResult.Pass();

            Vessel? producer = await ReadVesselAsync(mission.TenantId, mission.VesselId!, token).ConfigureAwait(false);
            if (producer == null) return DefinitionOfDoneResult.Pass();

            List<Vessel> allVessels = await EnumerateVesselsAsync(mission.TenantId, token).ConfigureAwait(false);
            IReadOnlyList<ConsumerDeclaration> consumers =
                ConsumerVesselResolver.Resolve(producer.Id, producer.Name, allVessels);
            if (consumers.Count == 0) return DefinitionOfDoneResult.Pass();

            string? producerRef = !String.IsNullOrWhiteSpace(mission.BranchName)
                ? mission.BranchName
                : await _Git.GetHeadCommitHashAsync(producerWorktreePath, token).ConfigureAwait(false);

            if (String.IsNullOrWhiteSpace(producerRef))
            {
                return ConsumerVerificationError(
                    "consumer-verify",
                    "Could not determine the producer ref to verify consumers against.");
            }

            _Logging.Info(_Header + "verifying " + consumers.Count + " declared consumer(s) of vessel " + producer.Id);

            foreach (ConsumerDeclaration edge in consumers)
            {
                DefinitionOfDoneResult result = await VerifyOneConsumerAsync(
                    producer,
                    producerRef!,
                    edge,
                    token).ConfigureAwait(false);
                if (!result.Passed) return result;
            }

            return DefinitionOfDoneResult.Pass();
        }

        private async Task<DefinitionOfDoneResult> VerifyOneConsumerAsync(
            Vessel producer,
            string producerRef,
            ConsumerDeclaration edge,
            CancellationToken token)
        {
            Vessel consumer = edge.Consumer;
            string label = "consumer-build (" + consumer.Name + ")";

            WorkflowProfile? consumerProfile = await ResolveProfileForVesselAsync(consumer, token).ConfigureAwait(false);
            string? consumerBuild = consumerProfile?.BuildCommand;
            if (String.IsNullOrWhiteSpace(consumerBuild))
            {
                return ConsumerVerificationError(
                    label,
                    "Consumer vessel '" + consumer.Name + "' has no BuildCommand on its workflow profile, so its "
                    + "compilation against this change cannot be verified.");
            }

            string scratchRoot = Path.Combine(
                Path.GetTempPath(),
                "armada-consumer-verify",
                Guid.NewGuid().ToString("N"));

            List<string> createdWorktrees = new List<string>();

            try
            {
                Directory.CreateDirectory(scratchRoot);

                string consumerWorktree = Path.Combine(scratchRoot, SafeDirName(consumer.Name));
                string? consumerRepo = ResolveVesselRepoPath(consumer);
                if (consumerRepo == null)
                {
                    return ConsumerVerificationError(
                        label,
                        "Consumer vessel '" + consumer.Name + "' has no LocalPath, so its repository cannot be located.");
                }

                string consumerBranch = ResolveDefaultBranch(consumer);

                await _Git.CreateWorktreeAsync(
                    consumerRepo, consumerWorktree, consumerBranch, consumerBranch, true, token).ConfigureAwait(false);
                createdWorktrees.Add(consumerWorktree);

                // Materialize every sibling the consumer declares. The producer takes the mission
                // ref; the rest take their declared defaults, because only the producer is under
                // test and pinning the others would verify a combination nobody is proposing.
                foreach (SiblingRepo sibling in consumer.GetSiblingRepos())
                {
                    if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath)) continue;

                    Vessel? siblingVessel = await ResolveSiblingVesselAsync(sibling, allowNull: true, token).ConfigureAwait(false);
                    if (siblingVessel == null) continue;

                    bool isProducer = String.Equals(siblingVessel.Id, producer.Id, StringComparison.OrdinalIgnoreCase);
                    string siblingRef = isProducer
                        ? producerRef
                        : (!String.IsNullOrWhiteSpace(sibling.DefaultBranch) ? sibling.DefaultBranch! : ResolveDefaultBranch(siblingVessel));

                    string siblingPath = Path.GetFullPath(Path.Combine(consumerWorktree, sibling.RelativePath));
                    string? siblingRepo = ResolveVesselRepoPath(siblingVessel);
                    if (siblingRepo == null)
                    {
                        // The producer is the subject of this verification; without it the build
                        // would prove nothing, so report rather than run a misleading compile.
                        if (isProducer)
                        {
                            return ConsumerVerificationError(
                                label,
                                "Producer vessel '" + siblingVessel.Name + "' has no LocalPath, so it cannot be "
                                + "provisioned into consumer '" + consumer.Name + "' for verification.");
                        }

                        _Logging.Warn(_Header + "sibling " + siblingVessel.Name + " has no LocalPath; skipping it for consumer verification");
                        continue;
                    }

                    await _Git.CreateWorktreeAsync(
                        siblingRepo, siblingPath, siblingRef, siblingRef, true, token).ConfigureAwait(false);
                    createdWorktrees.Add(siblingPath);
                }

                string effective = _Settings.RunRestoreBeforeBuild ? EnsureRestore(consumerBuild!) : consumerBuild!;
                DefinitionOfDoneResult result = await RunCommandAsync(label, effective, consumerWorktree, token).ConfigureAwait(false);

                if (!result.Passed)
                {
                    _Logging.Warn(_Header + "consumer " + consumer.Name + " failed to build against this change");
                }

                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ConsumerVerificationError(
                    label,
                    "Could not prepare consumer '" + consumer.Name + "' for verification: " + ex.Message);
            }
            finally
            {
                foreach (string worktree in createdWorktrees)
                {
                    try { await _Git!.RemoveWorktreeAsync(worktree, token).ConfigureAwait(false); }
                    catch (Exception ex) { _Logging.Debug(_Header + "consumer worktree cleanup failed for " + worktree + ": " + ex.Message); }
                }

                try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, true); }
                catch (Exception ex) { _Logging.Debug(_Header + "consumer scratch cleanup failed for " + scratchRoot + ": " + ex.Message); }
            }
        }

        /// <summary>
        /// Report a verification that could not be carried out, as distinct from a consumer that
        /// genuinely failed to compile.
        /// </summary>
        private DefinitionOfDoneResult ConsumerVerificationError(string label, string message)
        {
            if (_Settings.FailOnConsumerVerificationError)
            {
                _Logging.Warn(_Header + message);
                return DefinitionOfDoneResult.Fail(label, -1, BuildDiagnosticText(message), DefinitionOfDoneFailureClassEnum.Infra);
            }

            // Say it out loud. A verification that silently did not happen is indistinguishable
            // from one that passed, and that is the failure this whole step exists to prevent.
            _Logging.Warn(_Header + "consumer verification incomplete: " + message);
            return DefinitionOfDoneResult.Pass();
        }

        private async Task<Vessel?> ReadVesselAsync(string? tenantId, string vesselId, CancellationToken token)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(tenantId)
                    ? await _Database.Vessels.ReadAsync(tenantId, vesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            }
            catch { return null; }
        }

        private async Task<List<Vessel>> EnumerateVesselsAsync(string? tenantId, CancellationToken token)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(tenantId)
                    ? await _Database.Vessels.EnumerateAsync(tenantId, token).ConfigureAwait(false)
                    : await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            }
            catch { return new List<Vessel>(); }
        }

        private async Task<Vessel?> ResolveSiblingVesselAsync(SiblingRepo sibling, bool allowNull, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(sibling.VesselRef)) return null;

            Vessel? vessel = null;
            try { vessel = await _Database.Vessels.ReadAsync(sibling.VesselRef!, token).ConfigureAwait(false); }
            catch { }
            if (vessel == null)
            {
                try { vessel = await _Database.Vessels.ReadByNameAsync(sibling.VesselRef!, token).ConfigureAwait(false); }
                catch { }
            }
            return vessel;
        }

        /// <summary>
        /// Locate a vessel's bare repository, or null when the record does not say where it is.
        /// </summary>
        /// <remarks>
        /// Only the vessel's own <see cref="Vessel.LocalPath"/> is consulted. Guessing a path from
        /// the vessel name would produce a plausible directory that may not be the repository, and
        /// a verification that silently built the wrong tree is worse than one that reports it
        /// could not run.
        /// </remarks>
        private static string? ResolveVesselRepoPath(Vessel vessel)
        {
            if (String.IsNullOrWhiteSpace(vessel.LocalPath)) return null;
            return Path.GetFullPath(vessel.LocalPath!);
        }

        private static string ResolveDefaultBranch(Vessel vessel)
        {
            return !String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? vessel.DefaultBranch! : "main";
        }

        private static string SafeDirName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "consumer";
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return name;
        }

        private async Task<WorkflowProfile?> ResolveProfileForVesselAsync(Vessel vessel, CancellationToken token)
        {
            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = vessel.TenantId,
                Active = true,
                PageNumber = 1,
                PageSize = 1000
            };

            List<WorkflowProfile> candidates = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0) return null;

            WorkflowProfile? match = ChooseBestFromScope(
                candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(p.VesselId, vessel.Id, StringComparison.Ordinal)).ToList());
            if (match != null) return match;

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestFromScope(
                    candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Fleet
                        && String.Equals(p.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null) return match;
            }

            return ChooseBestFromScope(candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Global).ToList());
        }

        private bool IsPersonaApplicable(string? persona)
        {
            if (_Settings.AppliedPersonas == null || _Settings.AppliedPersonas.Count == 0)
                return false;
            return _Settings.AppliedPersonas.Exists(p =>
                String.Equals(p, persona, StringComparison.OrdinalIgnoreCase));
        }

        private bool HasDocOnlyMarker(string? description)
        {
            if (String.IsNullOrWhiteSpace(description)) return false;
            if (String.IsNullOrWhiteSpace(_Settings.DocOnlyMarker)) return false;
            return description.Contains(_Settings.DocOnlyMarker, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<WorkflowProfile?> ResolveProfileAsync(Mission mission, CancellationToken token)
        {
            string? vesselId = mission.VesselId;
            if (String.IsNullOrWhiteSpace(vesselId)) return null;

            Vessel? vessel = !String.IsNullOrWhiteSpace(mission.TenantId)
                ? await _Database.Vessels.ReadAsync(mission.TenantId, vesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);

            if (vessel == null) return null;

            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = mission.TenantId,
                Active = true,
                PageNumber = 1,
                PageSize = 1000
            };

            List<WorkflowProfile> candidates = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0) return null;

            WorkflowProfile? match = ChooseBestFromScope(
                candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(p.VesselId, vesselId, StringComparison.Ordinal)).ToList());
            if (match != null) return match;

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestFromScope(
                    candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Fleet
                        && String.Equals(p.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null) return match;
            }

            return ChooseBestFromScope(candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Global).ToList());
        }

        private WorkflowProfile? ChooseBestFromScope(List<WorkflowProfile> candidates)
        {
            if (candidates.Count == 0) return null;
            WorkflowProfile? defaultProfile = candidates.FirstOrDefault(p => p.IsDefault);
            return defaultProfile ?? candidates[0];
        }

        private async Task<DefinitionOfDoneResult> RunCommandAsync(
            string label,
            string command,
            string workingDir,
            CancellationToken token)
        {
            _Logging.Info(_Header + "running " + label + " command");

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = GetShell(),
                Arguments = GetShellArgs(command),
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            TimeSpan commandTimeout = ResolveCommandTimeout();
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(commandTimeout);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;

            // Declare outside the try block so catch blocks can kill the process.
            using Process process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("The command process did not start.");

                // Read both streams concurrently with the linked token so a hanging process
                // cannot fill either redirected pipe while the process is running.
                stdoutTask = process.StandardOutput.ReadToEndAsync();
                stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                string combined = CombineOutput(stdoutTask.Result, stderrTask.Result);

                int exitCode = process.ExitCode;
                _Logging.Info(_Header + label + " command exited " + exitCode);

                if (exitCode == 0)
                    return DefinitionOfDoneResult.Pass();

                DefinitionOfDoneFailureClassEnum failureClass = _FailureClassifier.Classify(
                    label,
                    exitCode,
                    combined);
                return DefinitionOfDoneResult.Fail(
                    label,
                    exitCode,
                    BuildDiagnosticText(combined),
                    failureClass);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                TryKillProcess(process);
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                TryKillProcess(process);
                // Report the timeout that actually fired, not the configured one, so the message
                // stays true when the two differ.
                string message = label + " command timed out after "
                    + commandTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds.";
                string partialOutput = await CaptureOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                string combined = String.IsNullOrWhiteSpace(partialOutput) ? message : message + "\n" + partialOutput;
                _Logging.Warn(_Header + message);
                return DefinitionOfDoneResult.Fail(
                    label,
                    -1,
                    BuildDiagnosticText(combined),
                    _FailureClassifier.Classify(label, -1, combined, true));
            }
            catch (Exception ex)
            {
                TryKillProcess(process);
                string partialOutput = await CaptureOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                string message = label + " command could not be started or completed: " + ex.Message;
                string combined = String.IsNullOrWhiteSpace(partialOutput) ? message : message + "\n" + partialOutput;
                _Logging.Warn(_Header + label + " command infrastructure failure");
                return DefinitionOfDoneResult.Fail(
                    label,
                    -1,
                    BuildDiagnosticText(combined),
                    DefinitionOfDoneFailureClassEnum.Infra);
            }
        }

        private void TryKillProcess(Process process)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception killEx)
            {
                _Logging.Warn(_Header + "could not kill command process exceptionType=" + killEx.GetType().Name);
            }
        }

        private static string CombineOutput(string? stdout, string? stderr)
        {
            string combined = stdout ?? String.Empty;
            if (!String.IsNullOrEmpty(stderr))
                combined += "\n--- STDERR ---\n" + stderr;
            return combined;
        }

        private static async Task<string> CaptureOutputAsync(
            Task<string>? stdoutTask,
            Task<string>? stderrTask)
        {
            try
            {
                string stdout = stdoutTask == null ? String.Empty : await stdoutTask.ConfigureAwait(false);
                string stderr = stderrTask == null ? String.Empty : await stderrTask.ConfigureAwait(false);
                return CombineOutput(stdout, stderr);
            }
            catch
            {
                return String.Empty;
            }
        }

        private string BuildDiagnosticText(string output)
        {
            string[] lines = (output ?? String.Empty).Split('\n');
            HashSet<string> retained = new HashSet<string>(StringComparer.Ordinal);
            List<string> diagnosticLines = new List<string>();

            foreach (string line in lines)
            {
                if (diagnosticLines.Count >= _Settings.DiagnosticLines)
                    break;
                if (!DefinitionOfDoneFailureClassifier.IsActionableDiagnosticLine(line))
                    continue;

                string redacted = RedactAndBoundLine(line);
                if (!String.IsNullOrWhiteSpace(redacted) && retained.Add(redacted))
                    diagnosticLines.Add(redacted);
            }

            int tailCount = Math.Min(lines.Length, _Settings.OutputTailLines);
            int startIndex = lines.Length - tailCount;
            List<string> reversedTailLines = new List<string>();
            for (int i = lines.Length - 1; i >= startIndex; i--)
            {
                string redacted = RedactAndBoundLine(lines[i]);
                if (!String.IsNullOrWhiteSpace(redacted) && retained.Add(redacted))
                    reversedTailLines.Add(redacted);
            }
            reversedTailLines.Reverse();

            string diagnostics = diagnosticLines.Count == 0
                ? "(none recognized)"
                : BuildBoundedSection(diagnosticLines, false);
            string tail = reversedTailLines.Count == 0
                ? "(no additional output)"
                : BuildBoundedSection(reversedTailLines, true);

            string result = "--- ACTIONABLE DIAGNOSTICS ---\n" + diagnostics
                + "\n--- OUTPUT TAIL ---\n" + tail;
            if (result.Length > _MAX_DIAGNOSTIC_TEXT_CHARS)
                return result.Substring(0, _MAX_DIAGNOSTIC_TEXT_CHARS);
            return result;
        }

        private static string RedactAndBoundLine(string line)
        {
            string normalized = line.TrimEnd('\r');
            string redacted = _SecretLikePattern.Replace(normalized, match =>
            {
                int separatorIndex = match.Value.IndexOfAny(new char[] { '=', ':' });
                if (separatorIndex < 0) return match.Value;
                return match.Value.Substring(0, separatorIndex + 1) + " [REDACTED]";
            });

            if (redacted.Length <= _MAX_LINE_CHARS)
                return redacted;
            return redacted.Substring(0, _MAX_LINE_CHARS) + "...(line truncated)";
        }

        private static string BuildBoundedSection(IReadOnlyList<string> lines, bool keepEnd)
        {
            const string truncatedMarker = "...(section truncated)";
            int contentBudget = _MAX_SECTION_CHARS - truncatedMarker.Length - 1;
            List<string> selected = new List<string>();
            int usedCharacters = 0;

            if (keepEnd)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    int required = lines[i].Length + 1;
                    if (usedCharacters + required > contentBudget)
                        break;
                    selected.Add(lines[i]);
                    usedCharacters += required;
                }
                selected.Reverse();
                if (selected.Count < lines.Count)
                    selected.Insert(0, truncatedMarker);
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    int required = lines[i].Length + 1;
                    if (usedCharacters + required > contentBudget)
                        break;
                    selected.Add(lines[i]);
                    usedCharacters += required;
                }
                if (selected.Count < lines.Count)
                    selected.Add(truncatedMarker);
            }

            return String.Join("\n", selected);
        }

        /// <summary>
        /// Strips the <c>--no-restore</c> token from a shell command string so the build or
        /// test tool performs its own NuGet restore. Called only when
        /// <see cref="DefinitionOfDoneSettings.RunRestoreBeforeBuild"/> is true.
        /// A command that does not contain <c>--no-restore</c> is returned unchanged.
        /// </summary>
        private static string EnsureRestore(string command)
        {
            return Regex.Replace(command, @"(^|\s)--no-restore\b", " ", RegexOptions.IgnoreCase).Trim();
        }

        private string GetShell()
        {
            if (OperatingSystem.IsWindows()) return "cmd.exe";
            return "/bin/sh";
        }

        private string GetShellArgs(string command)
        {
            if (OperatingSystem.IsWindows()) return "/c " + command;
            return "-c \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        #endregion
    }
}
