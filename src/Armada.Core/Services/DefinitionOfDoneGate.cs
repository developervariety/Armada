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
        #region Public-Members

        /// <summary>
        /// Named skip reason recorded when a read-only (Audit or Research) mission reaches the gate without
        /// producing a commit: its dock head still equals the dock start commit, so the build and unit-test
        /// commands would measure only the base branch, and a base branch that is already red would fail a
        /// mission that changed nothing.
        /// </summary>
        public const string ReadOnlyNoCommitSkipReason = "read_only_no_commit";

        /// <summary>
        /// Whether the gate is on. Read from the live settings section on every call, so a settings
        /// reload turns the gate on or off without a restart.
        /// </summary>
        public bool IsEnabled
        {
            get => _Settings.Enabled;
        }

        #endregion

        #region Private-Members

        private readonly string _Header = "[DefinitionOfDoneGate] ";
        private readonly DefinitionOfDoneSettings _Settings;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly IContainerRuntimeProbe? _ContainerRuntimeProbe;
        private readonly IGitService? _Git;
        private readonly TypedFlakeScoreAdapter? _FlakeScoreAdapter;
        private readonly DefinitionOfDoneFailureClassifier _FailureClassifier = new DefinitionOfDoneFailureClassifier();

        private const int _CommandOutputLimitBytes = 16 * 1024 * 1024;
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
        /// <param name="flakeScoreAdapter">
        /// Optional D15 <c>flake_score</c> adapter. When supplied and a unit-test command fails as a
        /// test failure, the gate scores the failure; if the model recommends it (Gate mode at or above
        /// threshold), the gate runs an isolated class-filtered re-run of only the failing classes and
        /// the re-run's real result becomes the truth. Null — the default, and the effective state
        /// whenever the decision is Off — preserves the original behavior exactly.
        /// </param>
        public DefinitionOfDoneGate(
            DefinitionOfDoneSettings settings,
            DatabaseDriver database,
            LoggingModule logging,
            IContainerRuntimeProbe? containerRuntimeProbe = null,
            IGitService? gitService = null,
            TypedFlakeScoreAdapter? flakeScoreAdapter = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ContainerRuntimeProbe = containerRuntimeProbe;
            _Git = gitService;
            _FlakeScoreAdapter = flakeScoreAdapter;
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

            string? skipReason = ResolveSkipReason(mission);
            if (skipReason != null)
                return DefinitionOfDoneResult.Skipped(skipReason);

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

        /// <summary>
        /// Describe the configuration this gate would apply to the mission. Uses the same skip rules and
        /// workflow-profile resolution as <see cref="EvaluateAsync"/>, but runs no command and reads no diff.
        /// </summary>
        /// <param name="mission">The mission to describe.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Current effective configuration.</returns>
        public async Task<DefinitionOfDoneConfiguration> DescribeAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            DefinitionOfDoneConfiguration configuration = new DefinitionOfDoneConfiguration
            {
                GateActive = true,
                Enabled = _Settings.Enabled,
                AppliedPersonas = new List<string>(_Settings.AppliedPersonas ?? new List<string>()),
                PersonaApplies = IsPersonaApplicable(mission.Persona),
                DocOnlyMarkerPresent = HasDocOnlyMarker(mission.Description),
                ExpectedSkipReason = ResolveSkipReason(mission),
                RunRestoreBeforeBuild = _Settings.RunRestoreBeforeBuild,
                CommandTimeoutSeconds = _Settings.CommandTimeoutSeconds,
                VerifyDeclaredConsumers = _Settings.VerifyDeclaredConsumers,
                FailOnConsumerVerificationError = _Settings.FailOnConsumerVerificationError,
                RunConsumerTests = _Settings.RunConsumerTests,
                DefaultConsumerTestTriggerPaths = new List<string>(_Settings.ConsumerTestTriggerPaths ?? new List<string>())
            };

            WorkflowProfile? profile = await ResolveProfileAsync(mission, token).ConfigureAwait(false);
            if (profile != null)
            {
                configuration.WorkflowProfileId = profile.Id;
                configuration.WorkflowProfileName = profile.Name;
                configuration.WorkflowProfileScope = profile.Scope;
                configuration.HasBuildCommand = !String.IsNullOrWhiteSpace(profile.BuildCommand);
                configuration.HasUnitTestCommand = !String.IsNullOrWhiteSpace(profile.UnitTestCommand);
                configuration.HasContainerlessUnitTestCommand = !String.IsNullOrWhiteSpace(profile.ContainerlessUnitTestCommand);
            }

            configuration.MissingCommands = !configuration.HasBuildCommand && !configuration.HasUnitTestCommand;
            return configuration;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// The one definition of when the gate does not apply. Evaluation and description both call it.
        /// </summary>
        private string? ResolveSkipReason(Mission mission)
        {
            if (!_Settings.Enabled)
                return "DoD gate is disabled";

            if (!IsPersonaApplicable(mission.Persona))
                return "persona '" + (mission.Persona ?? "(none)") + "' is not in AppliedPersonas";

            if (HasDocOnlyMarker(mission.Description))
                return "mission description contains doc-only opt-out marker";

            return null;
        }

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
                {
                    // D15 flake_score: when the failure reads as a load flake or a known flaky family,
                    // re-run only the failing classes in isolation; the re-run's real result is the
                    // truth. A red that stays red here is returned unchanged. The model never marks it
                    // green — only a genuine passing isolated re-run can.
                    DefinitionOfDoneResult afterFlake = await MaybeRerunFlakyTestAsync(mission, effectiveTest, worktreePath, testResult, token).ConfigureAwait(false);
                    if (!afterFlake.Passed) return afterFlake;

                    // The isolated re-run passed: the failure was a flake. Fall through to the consumer
                    // verification the gate would have run had the suite passed the first time.
                }
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

            // The producer's changed paths decide whether each consumer's suite runs. Read them
            // once here rather than per consumer: the diff is a property of the producer's branch,
            // not of any one consumer.
            ChangedPathsRead producerChangedPaths =
                await ReadProducerChangedPathsAsync(producer, producerWorktreePath, token).ConfigureAwait(false);

            foreach (ConsumerDeclaration edge in consumers)
            {
                DefinitionOfDoneResult result = await VerifyOneConsumerAsync(
                    producer,
                    producerRef!,
                    edge,
                    producerChangedPaths,
                    token).ConfigureAwait(false);
                if (!result.Passed) return result;
            }

            return DefinitionOfDoneResult.Pass();
        }

        private async Task<DefinitionOfDoneResult> VerifyOneConsumerAsync(
            Vessel producer,
            string producerRef,
            ConsumerDeclaration edge,
            ChangedPathsRead producerChangedPaths,
            CancellationToken token)
        {
            Vessel consumer = edge.Consumer;
            string label = "consumer-build (" + consumer.Name + ")";
            SiblingRepo? producerSibling = null;

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
            List<string> artifactLinks = new List<string>();

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
                    if (isProducer) producerSibling = sibling;
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
                    LinkSiblingArtifacts(sibling, siblingVessel, siblingPath, artifactLinks);
                }

                string effective = _Settings.RunRestoreBeforeBuild ? EnsureRestore(consumerBuild!) : consumerBuild!;
                DefinitionOfDoneResult buildResult = await RunCommandAsync(label, effective, consumerWorktree, token).ConfigureAwait(false);

                if (!buildResult.Passed)
                {
                    _Logging.Warn(_Header + "consumer " + consumer.Name + " failed to build against this change");
                    return buildResult;
                }

                // A build proves the consumer still compiles; it cannot prove the consumer still
                // behaves. When the producer change reaches a triggering path, run the consumer's
                // own suite against the same provisioned worktree before the branch may land.
                if (ShouldRunConsumerTests(producerSibling, producerChangedPaths, consumer.Name))
                {
                    DefinitionOfDoneResult testResult =
                        await RunConsumerTestsAsync(consumer, consumerProfile, consumerWorktree, token).ConfigureAwait(false);
                    if (!testResult.Passed) return testResult;
                }

                return buildResult;
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
                // Links first: a worktree removal or a recursive delete must never reach the host
                // artifact trees the links point at.
                RemoveArtifactLinks(artifactLinks);

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
        /// Link a sibling's git-ignored extraction artifact directories (for example a decompiled source
        /// tree) from the sibling vessel's host working directory into its verification worktree. A
        /// mission dock copies the same directories from the same place, so the consumer suite reads
        /// the trees it reads in a dock; without them every tree-dependent test fails and the gate
        /// blames the producer. The verification only reads the trees, so a link replaces the copy. A
        /// partial tree the worktree already tracks at that path is moved aside first. A missing source
        /// is logged and skipped, as a dock does.
        /// </summary>
        /// <param name="sibling">The consumer's sibling declaration.</param>
        /// <param name="siblingVessel">The sibling vessel.</param>
        /// <param name="siblingWorktree">The sibling's verification worktree.</param>
        /// <param name="links">Receives each link created, for removal.</param>
        internal void LinkSiblingArtifacts(SiblingRepo sibling, Vessel siblingVessel, string siblingWorktree, List<string> links)
        {
            if (sibling?.ExtractionArtifactPaths == null || sibling.ExtractionArtifactPaths.Count == 0) return;
            if (siblingVessel == null || String.IsNullOrWhiteSpace(siblingVessel.WorkingDirectory)) return;

            foreach (string artifactPath in sibling.ExtractionArtifactPaths)
            {
                if (String.IsNullOrWhiteSpace(artifactPath)) continue;
                string source = Path.GetFullPath(Path.Combine(siblingVessel.WorkingDirectory, artifactPath));
                string destination = Path.GetFullPath(Path.Combine(siblingWorktree, artifactPath));
                try
                {
                    if (!Directory.Exists(source))
                    {
                        _Logging.Warn(_Header + "extraction artifact source absent for consumer verification (vessel "
                            + siblingVessel.Name + ", path " + artifactPath + "); its tree-dependent tests will not find it");
                        continue;
                    }
                    // A deobfuscator commits a few files under its artifact directory, so the worktree
                    // already holds a partial tree there. The host working directory holds the whole
                    // tree, tracked files included; move the partial one aside and link the whole one,
                    // as a dock replaces it with a full copy. The worktree is discarded after the run.
                    if (File.Exists(destination)) continue;
                    if (Directory.Exists(destination))
                    {
                        if (new DirectoryInfo(destination).LinkTarget != null) continue;
                        Directory.Move(destination, destination + ".tracked-" + Guid.NewGuid().ToString("N"));
                    }

                    string? parent = Path.GetDirectoryName(destination);
                    if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    Directory.CreateSymbolicLink(destination, source);
                    links.Add(destination);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not link extraction artifacts for consumer verification (vessel "
                        + siblingVessel.Name + ", path " + artifactPath + "): " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Remove the artifact links created for a verification. Only the link is removed; the tree it
        /// points at is never touched.
        /// </summary>
        /// <param name="links">Links created by <see cref="LinkSiblingArtifacts"/>.</param>
        internal void RemoveArtifactLinks(List<string> links)
        {
            foreach (string link in links)
            {
                try
                {
                    FileSystemInfo info = new DirectoryInfo(link);
                    if (info.LinkTarget == null) continue;
                    File.Delete(link);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not remove artifact link " + link + ": " + ex.Message);
                }
            }
            links.Clear();
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

        /// <summary>
        /// Read the producer's changed paths against its default branch, for deciding whether a
        /// consumer suite must run. A read that fails, including a git seam that throws, is
        /// returned as unavailable, never as an empty change.
        /// </summary>
        private async Task<ChangedPathsRead> ReadProducerChangedPathsAsync(
            Vessel producer,
            string producerWorktreePath,
            CancellationToken token)
        {
            if (_Git == null) return ChangedPathsRead.Unavailable("no git seam to read the producer change");
            string baseBranch = ResolveDefaultBranch(producer);
            try
            {
                ChangedPathsRead read = await _Git.GetChangedFilePathsAgainstBaseAsync(producerWorktreePath, baseBranch, token).ConfigureAwait(false);
                return read ?? ChangedPathsRead.Unavailable("the git seam returned no changed-path result");
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                return ChangedPathsRead.Unavailable(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Decide whether a consumer's unit-test suite runs for this producer change. It runs when
        /// consumer-test verification is enabled, trigger prefixes are configured, and either at
        /// least one changed producer path is a non-test file under a trigger prefix or the changed
        /// paths could not be read. An unreadable change may reach any trigger, so the suite runs
        /// and the named reason is logged. The prefixes come from the producer's own sibling
        /// declaration when it lists any, otherwise from the gate settings default.
        /// </summary>
        private bool ShouldRunConsumerTests(SiblingRepo? producerSibling, ChangedPathsRead producerChangedPaths, string consumerName)
        {
            if (!_Settings.RunConsumerTests) return false;

            IReadOnlyList<string>? triggers =
                producerSibling?.ConsumerTestTriggerPaths != null && producerSibling.ConsumerTestTriggerPaths.Count > 0
                    ? producerSibling.ConsumerTestTriggerPaths
                    : _Settings.ConsumerTestTriggerPaths;
            if (triggers == null || triggers.Count == 0) return false;

            if (!producerChangedPaths.Available)
            {
                _Logging.Warn(_Header + "running consumer tests for " + consumerName
                    + " because the producer change could not be read: " + producerChangedPaths.FormatReason());
                return true;
            }

            foreach (string rawPath in producerChangedPaths.Paths)
            {
                if (String.IsNullOrWhiteSpace(rawPath)) continue;
                string path = rawPath.Replace('\\', '/');
                if (IsLikelyTestPath(path)) continue;

                foreach (string trigger in triggers)
                {
                    if (String.IsNullOrWhiteSpace(trigger)) continue;
                    string prefix = NormalizeTriggerPrefix(trigger);
                    if (prefix.Length == 0) continue;
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        private static string NormalizeTriggerPrefix(string trigger)
        {
            string prefix = trigger.Replace('\\', '/').Trim();
            if (prefix.StartsWith("./", StringComparison.Ordinal)) prefix = prefix.Substring(2);
            return prefix;
        }

        /// <summary>
        /// Whether a repository-relative path names a test file rather than production source, so a
        /// change to it alone does not trigger a consumer-test run. A path is a test path when any
        /// segment is a test project (ends with ".Tests" or ".Test") or a "test"/"tests" directory.
        /// </summary>
        private static bool IsLikelyTestPath(string path)
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                if (segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                    || segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (String.Equals(segment, "test", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Run a consumer's unit-test suite in its provisioned worktree. A failure is reported with
        /// a named reason distinct from a build break, so a broken consumer test can never be read
        /// as a compilation failure.
        /// </summary>
        private async Task<DefinitionOfDoneResult> RunConsumerTestsAsync(
            Vessel consumer,
            WorkflowProfile? consumerProfile,
            string consumerWorktree,
            CancellationToken token)
        {
            string? testCommand = consumerProfile?.UnitTestCommand;
            if (String.IsNullOrWhiteSpace(testCommand))
            {
                return ConsumerVerificationError(
                    "consumer-tests (" + consumer.Name + ")",
                    "Consumer vessel '" + consumer.Name + "' has no UnitTestCommand on its workflow profile, so its "
                    + "test suite cannot be run for a change that can break it.");
            }

            string selectedTest = testCommand!;
            string logLabel = "consumer-tests (" + consumer.Name + ")";

            // Mirror the producer's container pre-flight: without a runtime, container-backed
            // fixtures fail for the environment, so a declared containerless variant runs instead.
            if (!String.IsNullOrWhiteSpace(consumerProfile?.ContainerlessUnitTestCommand)
                && _ContainerRuntimeProbe != null
                && !await _ContainerRuntimeProbe.IsAvailableAsync(consumerWorktree, token).ConfigureAwait(false))
            {
                selectedTest = consumerProfile!.ContainerlessUnitTestCommand!;
                _Logging.Warn(_Header + "no container runtime detected; running consumer '" + consumer.Name + "' containerless unit-test command");
            }

            string effective = _Settings.RunRestoreBeforeBuild ? EnsureRestore(selectedTest) : selectedTest;
            DefinitionOfDoneResult result = await RunCommandAsync(logLabel, effective, consumerWorktree, token).ConfigureAwait(false);

            if (!result.Passed)
            {
                _Logging.Warn(_Header + "consumer " + consumer.Name + " test suite failed against this change");
                result.CommandLabel = "consumer_tests_failed: " + consumer.Name;
            }

            return result;
        }

        /// <summary>
        /// D15 <c>flake_score</c>: score a red unit-test result and, when the model recommends it, run an
        /// isolated class-filtered re-run whose real result becomes the truth. Returns the original
        /// failing result unchanged when the adapter is absent, the failure is not a test failure, the
        /// model does not recommend a re-run, or an isolated command could not be formed. Never marks a
        /// red result green on its own: only a genuine passing isolated re-run does.
        /// </summary>
        private async Task<DefinitionOfDoneResult> MaybeRerunFlakyTestAsync(
            Mission mission,
            string testCommand,
            string worktreePath,
            DefinitionOfDoneResult testResult,
            CancellationToken token)
        {
            if (_FlakeScoreAdapter == null) return testResult;
            if (testResult.FailureClass != DefinitionOfDoneFailureClassEnum.TestFail) return testResult;

            // Only a complete, non-overflowed set of failing names can be isolated into a filter.
            if (testResult.FailedTestNames == null || testResult.FailedTestNames.Count == 0 || testResult.FailedTestNamesOverflow)
                return testResult;

            IReadOnlyList<string> classNames = FlakeRerunCommand.DeriveClassNames(testResult.FailedTestNames);
            if (classNames.Count == 0) return testResult;

            bool crossBranch;
            try
            {
                crossBranch = await HasRecentCrossBranchFailureAsync(mission, testResult.FailedTestNames, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "flake_score cross-branch history lookup failed, treating as none: " + ex.Message);
                crossBranch = false;
            }

            FlakeScoreDecisionInput input = new FlakeScoreDecisionInput
            {
                Mission = mission,
                FailingTestNames = testResult.FailedTestNames,
                AssertionLines = testResult.OutputTail ?? String.Empty,
                TouchedFiles = new List<string>(),
                SameTestFailedElsewhere24h = crossBranch,
                RuleClass = DefinitionOfDoneFailureClassEnum.TestFail
            };

            FlakeScoreVerdict verdict = await _FlakeScoreAdapter.DecideAsync(input, FlakeScoreVerdict.NoRerun(), token).ConfigureAwait(false);
            if (!verdict.RerunRecommended) return testResult;

            if (!FlakeRerunCommand.TryBuild(testCommand, classNames, out string filteredCommand))
            {
                _Logging.Info(_Header + "flake_score recommended a re-run but no isolated command could be formed for this test command; the red stands");
                return testResult;
            }

            _Logging.Info(_Header + "flake_score " + verdict.Outcome + ": re-running " + classNames.Count + " failing class(es) in isolation");
            DefinitionOfDoneResult rerunResult = await RunIsolatedRerunAsync("unit-test (flake re-run)", filteredCommand, worktreePath, token).ConfigureAwait(false);

            // Record BOTH results: the original red and the isolated re-run. The re-run is the truth.
            _Logging.Info(_Header + "flake_score re-run outcome passed=" + rerunResult.Passed
                + " (original failure class=" + testResult.FailureClass + ", label=" + testResult.CommandLabel + ")");
            return rerunResult;
        }

        /// <summary>
        /// Test-only hook onto the D15 flake re-run decision path, so the re-run-is-truth behavior can be
        /// proved without executing a real test command (the isolated re-run itself is stubbed by
        /// overriding <see cref="RunIsolatedRerunAsync"/>).
        /// </summary>
        internal Task<DefinitionOfDoneResult> EvaluateFlakeRerunForTestAsync(
            Mission mission,
            string testCommand,
            string worktreePath,
            DefinitionOfDoneResult testResult,
            CancellationToken token)
        {
            return MaybeRerunFlakyTestAsync(mission, testCommand, worktreePath, testResult, token);
        }

        /// <summary>
        /// Run one isolated flake re-run command. A seam so a test can prove the re-run-is-truth behavior
        /// without executing a real test command; production runs the command in the dock worktree.
        /// </summary>
        /// <param name="label">The command label.</param>
        /// <param name="command">The isolated class-filtered command.</param>
        /// <param name="worktreePath">The dock worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The re-run result.</returns>
        protected virtual Task<DefinitionOfDoneResult> RunIsolatedRerunAsync(string label, string command, string worktreePath, CancellationToken token)
        {
            return RunCommandAsync(label, command, worktreePath, token);
        }

        /// <summary>
        /// Whether any of the failing test names failed on another branch (a different mission) in the
        /// last 24 hours. Best-effort state for the D15 model; a lookup failure reads as "no". A seam so
        /// a test can supply history without a database.
        /// </summary>
        /// <param name="mission">The mission whose failure is being scored.</param>
        /// <param name="failingTestNames">The failing test names to look for.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a matching cross-branch failure was found.</returns>
        protected virtual async Task<bool> HasRecentCrossBranchFailureAsync(Mission mission, IReadOnlyList<string> failingTestNames, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VesselId) || failingTestNames.Count == 0) return false;

            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = mission.TenantId,
                VesselId = mission.VesselId,
                Status = CheckRunStatusEnum.Failed,
                FromUtc = DateTime.UtcNow.AddHours(-24),
                PageNumber = 1,
                PageSize = 50
            };

            EnumerationResult<CheckRun> recent = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (CheckRun run in recent.Objects)
            {
                if (String.Equals(run.MissionId, mission.Id, StringComparison.Ordinal)) continue;
                string haystack = (run.Output ?? String.Empty) + "\n" + (run.Summary ?? String.Empty);
                if (haystack.Length == 0) continue;
                foreach (string name in failingTestNames)
                {
                    if (!String.IsNullOrWhiteSpace(name) && haystack.Contains(name, StringComparison.Ordinal)) return true;
                }
            }

            return false;
        }

        private async Task<Vessel?> ReadVesselAsync(string? tenantId, string vesselId, CancellationToken token)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(tenantId)
                    ? await _Database.Vessels.ReadAsync(tenantId, vesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "vessel " + vesselId + " could not be read (database error, not a missing vessel): " + ex.Message);
                return null;
            }
        }

        private async Task<List<Vessel>> EnumerateVesselsAsync(string? tenantId, CancellationToken token)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(tenantId)
                    ? await _Database.Vessels.EnumerateAsync(tenantId, token).ConfigureAwait(false)
                    : await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "vessels could not be enumerated (database error); consumer builds for this change are not checked: " + ex.Message);
                return new List<Vessel>();
            }
        }

        private async Task<Vessel?> ResolveSiblingVesselAsync(SiblingRepo sibling, bool allowNull, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(sibling.VesselRef)) return null;

            Vessel? vessel = null;
            try { vessel = await _Database.Vessels.ReadAsync(sibling.VesselRef!, token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "sibling vessel " + sibling.VesselRef + " could not be read by id (database error): " + ex.Message);
            }
            if (vessel == null)
            {
                try { vessel = await _Database.Vessels.ReadByNameAsync(sibling.VesselRef!, token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "sibling vessel " + sibling.VesselRef + " could not be read by name (database error): " + ex.Message);
                }
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

            ProcessStartInfo startInfo = new ProcessStartInfo(GetShell()) { WorkingDirectory = workingDir };
            if (OperatingSystem.IsWindows())
            {
                startInfo.Arguments = "/c " + command;
            }
            else
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(command);
            }

            // The command owns its process group, so a timeout or cancellation also stops a background child it
            // started. Each stream keeps 16 MiB: the failing-test extraction below reads the whole runner output,
            // and a truncated output marks the extracted set as incomplete.
            TimeSpan commandTimeout = ResolveCommandTimeout();
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, commandTimeout)
            {
                OutputLimitBytes = _CommandOutputLimitBytes,
                OwnProcessGroup = true
            };

            BoundedProcessResult result;
            try
            {
                result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string message = label + " command could not be started or completed: " + ex.Message;
                _Logging.Warn(_Header + label + " command infrastructure failure");
                return DefinitionOfDoneResult.Fail(
                    label,
                    -1,
                    BuildDiagnosticText(message),
                    DefinitionOfDoneFailureClassEnum.Infra);
            }

            if (result.KillError != null)
                _Logging.Warn(_Header + "could not kill command process: " + result.KillError);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }

            string combined = CombineOutput(result.StandardOutput, result.StandardError);
            if (result.TimedOut)
            {
                // Report the timeout that actually fired, not the configured one, so the message
                // stays true when the two differ.
                string message = label + " command timed out after "
                    + commandTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds.";
                string timedOutOutput = String.IsNullOrWhiteSpace(combined) ? message : message + "\n" + combined;
                _Logging.Warn(_Header + message);
                return DefinitionOfDoneResult.Fail(
                    label,
                    -1,
                    BuildDiagnosticText(timedOutOutput),
                    _FailureClassifier.Classify(label, -1, timedOutOutput, true));
            }

            string anomalies = BoundedProcessRunner.DescribeAnomalies(result);
            if (anomalies.Length > 0)
                _Logging.Warn(_Header + label + " command output: " + anomalies);

            int exitCode = result.ExitCode ?? -1;
            _Logging.Info(_Header + label + " command exited " + exitCode);

            if (exitCode == 0)
                return DefinitionOfDoneResult.Pass();

            DefinitionOfDoneFailureClassEnum failureClass = _FailureClassifier.Classify(
                label,
                exitCode,
                combined);
            DefinitionOfDoneResult failResult = DefinitionOfDoneResult.Fail(
                label,
                exitCode,
                BuildDiagnosticText(combined),
                failureClass);

            // Extract the failing test identifiers here, where the runner output is still whole:
            // the diagnostic text keeps only a bounded, redacted tail, so a later reader could not
            // recover the complete set. Only a test failure carries a set; every other class leaves
            // it null. A rescue-vs-parent comparison reads this set to detect an unchanged failure,
            // and a set read from truncated output is marked incomplete so it is never compared.
            if (failureClass == DefinitionOfDoneFailureClassEnum.TestFail)
            {
                FailedTestNameExtractor.FailedTestNameSet failedTests = FailedTestNameExtractor.Extract(combined);
                failResult.FailedTestNames = failedTests.Names;
                failResult.FailedTestNamesOverflow = failedTests.Overflow || result.Truncated;
            }

            return failResult;
        }

        private static string CombineOutput(string? stdout, string? stderr)
        {
            string combined = stdout ?? String.Empty;
            if (!String.IsNullOrEmpty(stderr))
                combined += "\n--- STDERR ---\n" + stderr;
            return combined;
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

        #endregion
    }
}
