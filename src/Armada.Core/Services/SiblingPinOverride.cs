namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// A required sibling input that sets <see cref="ObjectivePreparationSiblingInput.ProvisionPinnedCommit"/> makes
    /// every dock of the objective's dispatch check out the pinned sibling commit itself, so a consumer can be
    /// measured against a sibling commit that has not landed. The consumer's work is then valid only for that
    /// commit, so it lands only once the pin is an ancestor of the sibling vessel's default branch.
    /// </summary>
    public static class SiblingPinOverride
    {
        #region Public-Members

        /// <summary>
        /// Prefix of the reason a landing is refused while a provisioned sibling pin has not landed.
        /// </summary>
        public const string LandingRefusalPrefix = "sibling_pin_unlanded";

        /// <summary>
        /// Prefix of the environment variable that carries a provisioned sibling pin to every gate and check command
        /// of the objective's missions. The rest of the name is the sibling directory name in upper case, with each
        /// character that is not a letter or digit replaced by an underscore; the value is the full pinned commit.
        /// </summary>
        public const string EnvironmentVariablePrefix = "ARMADA_SIBLING_PIN_";

        /// <summary>
        /// Prefix of the reason a gate or check refuses to run when a provisioned pin does not resolve to a commit.
        /// </summary>
        public const string UnresolvedPrefix = "sibling_pin_unresolved";

        #endregion

        #region Public-Methods

        /// <summary>
        /// The pinned commit a dock must provision for a declared sibling, or null when the objective does not
        /// opt that sibling into the override.
        /// </summary>
        /// <param name="objective">Objective linked to the dock's voyage; may be null.</param>
        /// <param name="sibling">Declared sibling.</param>
        /// <returns>The trimmed pinned commit, or null.</returns>
        public static string? PinFor(Objective? objective, SiblingRepo sibling)
        {
            if (sibling == null) return null;
            ObjectivePreparationSiblingInput? input = SiblingCheckoutBrief.FindRequiredInput(objective, sibling);
            if (input == null || !input.ProvisionPinnedCommit || String.IsNullOrWhiteSpace(input.PinnedCommit)) return null;
            return input.PinnedCommit!.Trim();
        }

        /// <summary>
        /// Event recorded once per mission when it is allowed to land ahead of an unlanded provisioned pin.
        /// </summary>
        public const string LandedAheadEvent = "mission.landed_ahead_of_sibling";

        /// <summary>
        /// Why a mission must not land yet, or null when it may. Each provisioned sibling pin of the mission's
        /// objective must be an ancestor of its sibling vessel's default branch; a pin whose ancestry cannot be read
        /// refuses too. An input with <see cref="ObjectivePreparationSiblingInput.LandBeforeSibling"/> lets the
        /// mission land ahead of its unlanded pin, and that landing records <see cref="LandedAheadEvent"/> once.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="git">Git service used to read the sibling repository.</param>
        /// <param name="mission">Mission about to land, merge or enqueue; may be null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The refusal reason, or null.</returns>
        public static async Task<string?> EvaluateLandingAsync(DatabaseDriver database, IGitService git, Mission? mission, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (git == null) throw new ArgumentNullException(nameof(git));
            if (mission == null || String.IsNullOrEmpty(mission.VoyageId)) return null;

            Objective? objective = await VoyageObjectiveResolver.ResolveAsync(database, mission.VoyageId, token).ConfigureAwait(false);
            List<string> landedAhead = new List<string>();
            foreach (ObjectivePreparationSiblingInput input in objective?.Preparation?.RequiredSiblingInputs ?? new List<ObjectivePreparationSiblingInput>())
            {
                if (input == null || !input.ProvisionPinnedCommit || String.IsNullOrWhiteSpace(input.PinnedCommit)) continue;
                string pin = input.PinnedCommit!.Trim();

                Vessel? sibling = await database.Vessels.ReadAsync(input.VesselRef, token).ConfigureAwait(false)
                    ?? await database.Vessels.ReadByNameAsync(input.VesselRef, token).ConfigureAwait(false);
                string branch = !String.IsNullOrWhiteSpace(sibling?.DefaultBranch) ? sibling!.DefaultBranch : "main";
                string? repoPath = sibling?.LocalPath;
                string? tip = String.IsNullOrWhiteSpace(repoPath)
                    ? null
                    : await git.GetRevisionCommitShaAsync(repoPath!, "refs/heads/" + branch, token).ConfigureAwait(false);
                bool? landed = String.IsNullOrWhiteSpace(tip)
                    ? null
                    : await git.TryIsAncestorAsync(repoPath!, pin, tip!, token).ConfigureAwait(false);
                if (landed == true) continue;

                string state = landed == false
                    ? "is not an ancestor of " + input.VesselRef + " branch " + branch + " at " + Short(tip)
                    : "could not be checked against " + input.VesselRef + " branch " + branch;
                string description = "sibling " + input.RelativePath + " at pinned commit " + Short(pin) + ", which " + state;
                if (input.LandBeforeSibling)
                {
                    landedAhead.Add(description);
                    continue;
                }

                return LandingRefusalPrefix + ": objective " + objective!.Id + " provisions " + description
                    + ". Land the sibling commit first, or re-measure at the landed tip and clear provisionPinnedCommit.";
            }

            if (landedAhead.Count > 0)
                await RecordLandedAheadAsync(database, mission, objective!, landedAhead, token).ConfigureAwait(false);
            return null;
        }

        /// <summary>
        /// Name of the environment variable that carries the pin of <paramref name="sibling"/>.
        /// </summary>
        /// <param name="sibling">Declared sibling.</param>
        /// <returns>The variable name.</returns>
        public static string EnvironmentVariableName(SiblingRepo sibling)
        {
            if (sibling == null) throw new ArgumentNullException(nameof(sibling));
            string relative = (sibling.RelativePath ?? String.Empty).Replace('\\', '/').TrimEnd('/');
            string leaf = relative.Substring(relative.LastIndexOf('/') + 1);
            StringBuilder name = new StringBuilder(EnvironmentVariablePrefix);
            foreach (char c in leaf)
                name.Append(Char.IsAsciiLetterOrDigit(c) ? Char.ToUpperInvariant(c) : '_');
            return name.ToString();
        }

        /// <summary>
        /// The provisioned sibling pins that apply to a vessel's work under a voyage: one entry for each declared
        /// sibling whose required input sets <see cref="ObjectivePreparationSiblingInput.ProvisionPinnedCommit"/>,
        /// with the pin resolved to a full commit in the sibling vessel's repository. An entry whose pin does not
        /// resolve has a null <see cref="ProvisionedSiblingPin.Commit"/>.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="vessel">Vessel the work belongs to; may be null.</param>
        /// <param name="voyageId">Voyage of the work; may be null.</param>
        /// <param name="resolveCommit">Resolves a revision to a full commit in a repository path, or returns null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The pins; empty when none applies.</returns>
        public static async Task<List<ProvisionedSiblingPin>> ResolvePinsAsync(
            DatabaseDriver database,
            Vessel? vessel,
            string? voyageId,
            Func<string, string, CancellationToken, Task<string?>> resolveCommit,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (resolveCommit == null) throw new ArgumentNullException(nameof(resolveCommit));
            List<ProvisionedSiblingPin> pins = new List<ProvisionedSiblingPin>();
            if (vessel == null || String.IsNullOrWhiteSpace(voyageId)) return pins;
            List<SiblingRepo> siblings = vessel.GetSiblingRepos();
            if (siblings.Count == 0) return pins;

            Objective? objective = await VoyageObjectiveResolver.ResolveAsync(database, voyageId!, token).ConfigureAwait(false);
            if (objective == null) return pins;

            foreach (SiblingRepo sibling in siblings)
            {
                string? pin = PinFor(objective, sibling);
                if (pin == null) continue;
                ObjectivePreparationSiblingInput input = SiblingCheckoutBrief.FindRequiredInput(objective, sibling)!;
                Vessel? siblingVessel = await database.Vessels.ReadAsync(input.VesselRef, token).ConfigureAwait(false)
                    ?? await database.Vessels.ReadByNameAsync(input.VesselRef, token).ConfigureAwait(false);
                string? repoPath = String.IsNullOrWhiteSpace(siblingVessel?.LocalPath) ? null : siblingVessel!.LocalPath;
                string? commit = repoPath == null ? null : await resolveCommit(repoPath, pin, token).ConfigureAwait(false);
                pins.Add(new ProvisionedSiblingPin(sibling, objective.Id, pin, repoPath, String.IsNullOrWhiteSpace(commit) ? null : commit!.Trim()));
            }
            return pins;
        }

        /// <summary>
        /// Why work under these pins must not run, or null when every pin resolved.
        /// </summary>
        /// <param name="pins">Resolved pins.</param>
        /// <returns>The refusal reason, or null.</returns>
        public static string? DescribeUnresolved(IReadOnlyList<ProvisionedSiblingPin> pins)
        {
            if (pins == null) return null;
            foreach (ProvisionedSiblingPin pin in pins)
            {
                if (pin.Commit != null) continue;
                return UnresolvedPrefix + ": objective " + pin.ObjectiveId + " provisions sibling " + pin.Sibling.RelativePath
                    + " at pinned commit " + pin.PinnedCommit + ", which "
                    + (pin.RepoPath == null ? "has no readable sibling repository" : "does not exist in " + pin.RepoPath)
                    + ". Nothing runs against an unresolved pin.";
            }
            return null;
        }

        /// <summary>
        /// The environment variables that carry resolved pins to a command.
        /// </summary>
        /// <param name="pins">Resolved pins.</param>
        /// <returns>Variable name to full pinned commit.</returns>
        public static Dictionary<string, string> ToEnvironment(IReadOnlyList<ProvisionedSiblingPin>? pins)
        {
            Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (pins == null) return environment;
            foreach (ProvisionedSiblingPin pin in pins)
                if (pin.Commit != null) environment[EnvironmentVariableName(pin.Sibling)] = pin.Commit;
            return environment;
        }

        #endregion

        #region Private-Methods

        private static async Task RecordLandedAheadAsync(DatabaseDriver database, Mission mission, Objective objective, List<string> siblings, CancellationToken token)
        {
            List<ArmadaEvent> existing = await database.Events.EnumerateByMissionAsync(mission.Id, 200, token).ConfigureAwait(false);
            if (existing.Exists(item => String.Equals(item.EventType, LandedAheadEvent, StringComparison.Ordinal))) return;

            ArmadaEvent landedAhead = new ArmadaEvent(LandedAheadEvent,
                "Mission " + mission.Id + " lands ahead of its sibling under objective " + objective.Id + "'s landBeforeSibling: "
                + String.Join("; ", siblings) + ".")
            {
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                EntityType = "mission",
                EntityId = mission.Id,
                MissionId = mission.Id,
                VesselId = mission.VesselId,
                VoyageId = mission.VoyageId
            };
            await database.Events.CreateAsync(landedAhead, token).ConfigureAwait(false);
        }

        private static string Short(string? commit)
        {
            if (String.IsNullOrWhiteSpace(commit)) return "unknown";
            string trimmed = commit!.Trim();
            return trimmed.Length > 12 ? trimmed.Substring(0, 12) : trimmed;
        }

        #endregion
    }
}
