namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Cross-vessel mission-pattern aggregator that powers the persona-curate / captain-curate
    /// brief evidence bundle (Reflections v2-F2). Reads terminal-mission rows scoped by
    /// persona role or by captain id and aggregates per-identity behavior signals: judge
    /// verdict trends, success/failure ratio, average tier, recurring failure-mode tags,
    /// signal-mail-received summaries, top-touched files, and (for persona-curate) the
    /// persona-role distribution across the captains in scope.
    /// Reuses <see cref="PackUsageMiner"/> for tool-call signal where the captain log is
    /// available; the file-touch buckets contribute to the top-touched-files aggregation.
    /// Stateless and free of side effects.
    /// </summary>
    public sealed class HabitPatternMiner
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly PackUsageMiner? _PackUsageMiner;

        // Lightweight failure-mode tag taxonomy. Open-ended -- the consolidator may emit
        // ad-hoc tags too. The miner pre-tags common patterns parsed from FailureReason
        // strings and judge notes so the consolidator's brief surfaces structured signal
        // alongside free-form text.
        private static readonly (string Tag, Regex Pattern)[] _FailureTagPatterns = new[]
        {
            ("missing-tests", new Regex(@"\b(no|missing|did\s*not|didn'?t)\b[^.]{0,40}\btests?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("test-framework-mismatch", new Regex(@"\b(xunit|nunit|mstest|TestSuite)\b.{0,40}\b(wrong|incorrect|expected|should be)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("compiler-warnings-suppressed", new Regex(@"\b(suppressed|swallowed|hid(den|e)|ignored)\b.{0,30}\bwarning", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("wrong-file-paths", new Regex(@"\b(wrong|incorrect|invalid)\s+(file\s+)?path", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("missing-claude-md-update", new Regex(@"CLAUDE\.md.{0,30}(should|needs?|expected|missing)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("scope-creep", new Regex(@"\b(scope creep|out of scope|beyond scope|over-?scoped)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("decomposition-too-fine", new Regex(@"\b(over-?decomposed|too fine|fragmented|too granular)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("decomposition-too-coarse", new Regex(@"\b(under-?decomposed|too coarse|monolithic mission|too large)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("merge-conflict", new Regex(@"\bmerge conflict|conflicted files\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("auth-violation", new Regex(@"\b(auth|tenant|cross-tenant)\b.{0,40}(leak|bypass|violation)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="packUsageMiner">Optional <see cref="PackUsageMiner"/> reused for
        ///   captain-log file-touch evidence; null disables the top-touched-files signal.</param>
        public HabitPatternMiner(DatabaseDriver database, PackUsageMiner? packUsageMiner = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _PackUsageMiner = packUsageMiner;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Aggregate behavior signals for a persona across all captains that have served the role.
        /// </summary>
        /// <param name="personaName">Persona name (matched case-insensitively against <c>missions.persona</c>).</param>
        /// <param name="sinceUtc">Lower bound on terminal mission completion time; missions with
        ///   <see cref="Mission.CompletedUtc"/> at or before this are excluded. Null includes the
        ///   full history (capped by <paramref name="initialWindow"/>).</param>
        /// <param name="initialWindow">Maximum terminal missions retained when <paramref name="sinceUtc"/> is null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Aggregated <see cref="HabitPatternResult"/>; empty when no missions match.</returns>
        public async Task<HabitPatternResult> MinePersonaAsync(
            string personaName,
            DateTime? sinceUtc,
            int initialWindow,
            CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(personaName)) throw new ArgumentNullException(nameof(personaName));
            if (initialWindow < 1) initialWindow = 25;

            List<Mission> all = await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);
            List<Mission> scope = all
                .Where(m => IsTerminal(m.Status))
                .Where(m => !String.IsNullOrEmpty(m.Persona)
                    && String.Equals(m.Persona, personaName, StringComparison.OrdinalIgnoreCase))
                .Where(m => !sinceUtc.HasValue || (m.CompletedUtc.HasValue && m.CompletedUtc.Value > sinceUtc.Value))
                .OrderByDescending(m => m.CompletedUtc ?? m.LastUpdateUtc)
                .ToList();

            if (!sinceUtc.HasValue && scope.Count > initialWindow)
            {
                scope = scope.Take(initialWindow).ToList();
            }

            HabitPatternResult result = new HabitPatternResult
            {
                Scope = HabitPatternScope.Persona,
                TargetId = personaName,
                MissionsExamined = scope.Count
            };

            if (scope.Count == 0) return result;

            await PopulateAggregatesAsync(result, scope, token).ConfigureAwait(false);
            await PopulateCaptainBreakdownAsync(result, scope, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Aggregate behavior signals for a single captain across all vessels they have served.
        /// </summary>
        /// <param name="captainId">Captain id (cpt_ prefix).</param>
        /// <param name="sinceUtc">Lower bound on terminal mission completion time.</param>
        /// <param name="initialWindow">Maximum terminal missions retained when <paramref name="sinceUtc"/> is null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Aggregated <see cref="HabitPatternResult"/>; empty when no missions match.</returns>
        public async Task<HabitPatternResult> MineCaptainAsync(
            string captainId,
            DateTime? sinceUtc,
            int initialWindow,
            CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (initialWindow < 1) initialWindow = 25;

            List<Mission> all = await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);
            List<Mission> scope = all
                .Where(m => IsTerminal(m.Status))
                .Where(m => String.Equals(m.CaptainId, captainId, StringComparison.Ordinal))
                .Where(m => !sinceUtc.HasValue || (m.CompletedUtc.HasValue && m.CompletedUtc.Value > sinceUtc.Value))
                .OrderByDescending(m => m.CompletedUtc ?? m.LastUpdateUtc)
                .ToList();

            if (!sinceUtc.HasValue && scope.Count > initialWindow)
            {
                scope = scope.Take(initialWindow).ToList();
            }

            HabitPatternResult result = new HabitPatternResult
            {
                Scope = HabitPatternScope.Captain,
                TargetId = captainId,
                MissionsExamined = scope.Count
            };

            if (scope.Count == 0) return result;

            await PopulateAggregatesAsync(result, scope, token).ConfigureAwait(false);

            // Captain scope: surface persona-role distribution rather than a captain-by-captain
            // breakdown. Useful signal for "what kind of work does this captain see".
            Dictionary<string, int> personaCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Mission m in scope)
            {
                string key = String.IsNullOrEmpty(m.Persona) ? "Worker" : m.Persona!;
                if (!personaCounts.ContainsKey(key)) personaCounts[key] = 0;
                personaCounts[key]++;
            }
            foreach (KeyValuePair<string, int> kv in personaCounts.OrderByDescending(p => p.Value))
            {
                result.PersonaRoleDistribution.Add(new PersonaRoleCount
                {
                    PersonaName = kv.Key,
                    MissionCount = kv.Value
                });
            }

            return result;
        }

        /// <summary>
        /// Aggregate behavior signals across ALL active vessels in a fleet (Reflections v2-F3).
        /// Mirrors <see cref="MinePersonaAsync"/> but partitions by fleetId instead of persona,
        /// filters vessels to <c>Active = true</c> per the F3 active-vessel-filter rule, and
        /// returns a per-vessel contribution list under <see cref="HabitPatternResult.VesselContributions"/>
        /// (consumed by the fleet-curate brief assembler to surface which vessels supplied
        /// evidence). The captain breakdown is omitted for fleet scope.
        /// </summary>
        /// <param name="fleetId">Fleet id (flt_ prefix).</param>
        /// <param name="sinceUtc">Lower bound on terminal mission completion time.</param>
        /// <param name="initialWindow">Maximum terminal missions retained when <paramref name="sinceUtc"/> is null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Aggregated <see cref="HabitPatternResult"/>; empty when no active vessels in
        ///   the fleet have terminal missions.</returns>
        public async Task<HabitPatternResult> MineFleetAsync(
            string fleetId,
            DateTime? sinceUtc,
            int initialWindow,
            CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(fleetId)) throw new ArgumentNullException(nameof(fleetId));
            if (initialWindow < 1) initialWindow = 200;

            List<Vessel> vesselsInFleet = await _Database.Vessels.EnumerateByFleetAsync(fleetId, token).ConfigureAwait(false);
            HashSet<string> activeVesselIds = new HashSet<string>(
                vesselsInFleet.Where(v => v.Active).Select(v => v.Id),
                StringComparer.Ordinal);
            Dictionary<string, string> vesselNamesById = vesselsInFleet
                .Where(v => v.Active)
                .ToDictionary(v => v.Id, v => v.Name, StringComparer.Ordinal);

            HabitPatternResult result = new HabitPatternResult
            {
                Scope = HabitPatternScope.Fleet,
                TargetId = fleetId
            };

            if (activeVesselIds.Count == 0) return result;

            List<Mission> all = await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);
            List<Mission> scope = all
                .Where(m => IsTerminal(m.Status))
                .Where(m => !String.IsNullOrEmpty(m.VesselId) && activeVesselIds.Contains(m.VesselId!))
                .Where(m => !sinceUtc.HasValue || (m.CompletedUtc.HasValue && m.CompletedUtc.Value > sinceUtc.Value))
                .OrderByDescending(m => m.CompletedUtc ?? m.LastUpdateUtc)
                .ToList();

            if (!sinceUtc.HasValue && scope.Count > initialWindow)
            {
                scope = scope.Take(initialWindow).ToList();
            }

            result.MissionsExamined = scope.Count;

            if (scope.Count == 0) return result;

            await PopulateAggregatesAsync(result, scope, token).ConfigureAwait(false);

            // Per-vessel contribution breakdown: how many terminal missions each active vessel
            // contributed to the fleet evidence pool.
            Dictionary<string, List<Mission>> byVessel = new Dictionary<string, List<Mission>>(StringComparer.Ordinal);
            foreach (Mission m in scope)
            {
                string key = m.VesselId ?? "_unassigned";
                if (!byVessel.TryGetValue(key, out List<Mission>? lst))
                {
                    lst = new List<Mission>();
                    byVessel[key] = lst;
                }
                lst.Add(m);
            }

            foreach (KeyValuePair<string, List<Mission>> kv in byVessel.OrderByDescending(p => p.Value.Count))
            {
                vesselNamesById.TryGetValue(kv.Key, out string? name);
                result.VesselContributions.Add(new VesselContribution
                {
                    VesselId = kv.Key,
                    VesselName = name ?? kv.Key,
                    MissionCount = kv.Value.Count,
                    CompleteCount = kv.Value.Count(x => x.Status == MissionStatusEnum.Complete),
                    FailedCount = kv.Value.Count(x => x.Status == MissionStatusEnum.Failed),
                    CancelledCount = kv.Value.Count(x => x.Status == MissionStatusEnum.Cancelled)
                });
            }

            return result;
        }

        /// <summary>
        /// Compute the Jaccard similarity between two strings using character-level 3-gram
        /// shingles (Reflections v2-F3). Used by the fleet-curate vessel-fleet conflict gate
        /// at accept time and the cross-vessel-suggestion hint pass at vessel-curate brief
        /// assembly. Both inputs are lower-cased; whitespace runs collapse to a single space.
        /// Returns 0.0 when either input has fewer than 3 effective characters.
        /// </summary>
        /// <param name="a">First string.</param>
        /// <param name="b">Second string.</param>
        /// <returns>Jaccard similarity in [0.0, 1.0].</returns>
        public static double Jaccard3GramSimilarity(string? a, string? b)
        {
            if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b)) return 0.0;
            HashSet<string> setA = ToTrigramSet(a!);
            HashSet<string> setB = ToTrigramSet(b!);
            if (setA.Count == 0 || setB.Count == 0) return 0.0;
            int intersection = 0;
            foreach (string g in setA)
            {
                if (setB.Contains(g)) intersection++;
            }
            int union = setA.Count + setB.Count - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        /// <summary>
        /// Cheap sentiment-disagreement heuristic for the fleet-curate vessel-fleet conflict
        /// gate (Reflections v2-F3). Returns true when one side contains a negation token
        /// (`not`, `no`, `never`, `do not`, `don't`, `cannot`, `can't`) AND the other does not.
        /// Does not analyze structure beyond token presence; combined with Jaccard similarity
        /// over a high threshold, it surfaces likely contradictions.
        /// </summary>
        /// <param name="a">First string.</param>
        /// <param name="b">Second string.</param>
        /// <returns>True when negation polarity differs across the two inputs.</returns>
        public static bool SentimentDisagrees(string? a, string? b)
        {
            return HasNegation(a) != HasNegation(b);
        }

        private static readonly string[] _NegationTokens = new[]
        {
            " not ", " no ", " never ", " do not ", " don't ", " cannot ", " can't ", " won't ", " isn't ", " aren't "
        };

        private static bool HasNegation(string? s)
        {
            if (String.IsNullOrEmpty(s)) return false;
            string padded = " " + s!.ToLowerInvariant() + " ";
            foreach (string token in _NegationTokens)
            {
                if (padded.Contains(token, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static HashSet<string> ToTrigramSet(string raw)
        {
            string normalized = Regex.Replace(raw.ToLowerInvariant(), @"\s+", " ").Trim();
            HashSet<string> shingles = new HashSet<string>(StringComparer.Ordinal);
            if (normalized.Length < 3) return shingles;
            for (int i = 0; i + 3 <= normalized.Length; i++)
            {
                shingles.Add(normalized.Substring(i, 3));
            }
            return shingles;
        }

        #endregion

        #region Private-Methods

        private async Task PopulateAggregatesAsync(HabitPatternResult result, List<Mission> scope, CancellationToken token)
        {
            int complete = 0;
            int failed = 0;
            int cancelled = 0;
            int recoveryAttemptsTotal = 0;
            Dictionary<string, int> failureTags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> fileTouchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int signalMailReceivedTotal = 0;

            foreach (Mission m in scope)
            {
                switch (m.Status)
                {
                    case MissionStatusEnum.Complete: complete++; break;
                    case MissionStatusEnum.Failed: failed++; break;
                    case MissionStatusEnum.Cancelled: cancelled++; break;
                }

                recoveryAttemptsTotal += m.RecoveryAttempts;

                if (!String.IsNullOrEmpty(m.FailureReason))
                {
                    foreach ((string tag, Regex pattern) in _FailureTagPatterns)
                    {
                        if (pattern.IsMatch(m.FailureReason!))
                        {
                            if (!failureTags.ContainsKey(tag)) failureTags[tag] = 0;
                            failureTags[tag]++;
                        }
                    }
                }

                // Signal mails are course-correction inputs received mid-mission. The proxy
                // counts events of type "signal.received" (or fall back to "signal.created"
                // when the mission was the recipient).
                int sigCount = await CountSignalsForMissionAsync(m.Id, token).ConfigureAwait(false);
                signalMailReceivedTotal += sigCount;

                if (_PackUsageMiner != null)
                {
                    PackUsageTriple triple = await _PackUsageMiner.MineAsync(m, token).ConfigureAwait(false);
                    if (triple.LogAvailable)
                    {
                        foreach (string p in triple.FilesEdited)
                        {
                            if (!fileTouchCounts.ContainsKey(p)) fileTouchCounts[p] = 0;
                            fileTouchCounts[p]++;
                        }
                    }
                }
            }

            result.MissionsComplete = complete;
            result.MissionsFailed = failed;
            result.MissionsCancelled = cancelled;
            result.AverageRecoveryAttempts = scope.Count == 0 ? 0.0 : (double)recoveryAttemptsTotal / scope.Count;
            result.SignalMailReceivedTotal = signalMailReceivedTotal;

            foreach (KeyValuePair<string, int> kv in failureTags.OrderByDescending(p => p.Value))
            {
                result.FailureModeTags.Add(new FailureModeTagCount
                {
                    Tag = kv.Key,
                    MissionCount = kv.Value
                });
            }

            foreach (KeyValuePair<string, int> kv in fileTouchCounts.OrderByDescending(p => p.Value).Take(5))
            {
                result.TopTouchedFiles.Add(new FileTouchCount
                {
                    Path = kv.Key,
                    EditCount = kv.Value
                });
            }

            // Judge verdict trend: walk sibling Judge missions in each voyage and count their
            // PASS/FAIL/NEEDS_REVISION explicit verdict markers.
            int pass = 0, fail = 0, needsRevision = 0, pending = 0;
            foreach (Mission m in scope)
            {
                if (String.IsNullOrEmpty(m.VoyageId)) continue;
                List<Mission> voyMissions = await _Database.Missions.EnumerateByVoyageAsync(m.VoyageId, token).ConfigureAwait(false);
                foreach (Mission sib in voyMissions)
                {
                    if (!String.Equals(sib.Persona, "Judge", StringComparison.OrdinalIgnoreCase)) continue;
                    if (sib.DependsOnMissionId != m.Id) continue;
                    string verdict = ParseExplicitVerdictMarker(sib.AgentOutput);
                    switch (verdict)
                    {
                        case "PASS": pass++; break;
                        case "FAIL": fail++; break;
                        case "NEEDS_REVISION": needsRevision++; break;
                        default: pending++; break;
                    }
                }
            }
            result.JudgePassCount = pass;
            result.JudgeFailCount = fail;
            result.JudgeNeedsRevisionCount = needsRevision;
            result.JudgePendingCount = pending;
        }

        private async Task PopulateCaptainBreakdownAsync(HabitPatternResult result, List<Mission> scope, CancellationToken token)
        {
            Dictionary<string, List<Mission>> byCaptain = new Dictionary<string, List<Mission>>(StringComparer.Ordinal);
            foreach (Mission m in scope)
            {
                string key = m.CaptainId ?? "_unassigned";
                if (!byCaptain.TryGetValue(key, out List<Mission>? lst))
                {
                    lst = new List<Mission>();
                    byCaptain[key] = lst;
                }
                lst.Add(m);
            }

            foreach (KeyValuePair<string, List<Mission>> kv in byCaptain.OrderByDescending(p => p.Value.Count))
            {
                CaptainContribution contribution = new CaptainContribution
                {
                    CaptainId = kv.Key,
                    MissionCount = kv.Value.Count,
                    CompleteCount = kv.Value.Count(x => x.Status == MissionStatusEnum.Complete),
                    FailedCount = kv.Value.Count(x => x.Status == MissionStatusEnum.Failed),
                    CancelledCount = kv.Value.Count(x => x.Status == MissionStatusEnum.Cancelled)
                };

                if (!String.Equals(kv.Key, "_unassigned", StringComparison.Ordinal))
                {
                    Captain? captain = await _Database.Captains.ReadAsync(kv.Key, token).ConfigureAwait(false);
                    if (captain != null)
                    {
                        contribution.Runtime = captain.Runtime.ToString();
                        contribution.Model = captain.Model;
                    }
                }
                result.CaptainContributions.Add(contribution);
            }
        }

        private async Task<int> CountSignalsForMissionAsync(string missionId, CancellationToken token)
        {
            try
            {
                EnumerationQuery query = new EnumerationQuery
                {
                    MissionId = missionId,
                    EventType = "signal.received",
                    PageNumber = 1,
                    PageSize = 100
                };
                EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
                return page.Objects.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.Cancelled;
        }

        private static string ParseExplicitVerdictMarker(string? agentOutput)
        {
            if (String.IsNullOrEmpty(agentOutput)) return "PENDING";
            foreach (string raw in agentOutput.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                Match m = Regex.Match(line, @"^\[ARMADA:VERDICT\]\s+(?<v>PASS|FAIL|NEEDS_REVISION)\s*$");
                if (m.Success) return m.Groups["v"].Value;
            }
            return "PENDING";
        }

        #endregion
    }

    /// <summary>
    /// Scope of a <see cref="HabitPatternMiner"/> aggregation.
    /// </summary>
    public enum HabitPatternScope
    {
        /// <summary>Persona-role scope: cross-captain aggregation for one persona name.</summary>
        Persona = 0,

        /// <summary>Captain scope: cross-vessel aggregation for one captain id.</summary>
        Captain = 1,

        /// <summary>Fleet scope: cross-vessel aggregation across all active vessels in one fleet (Reflections v2-F3).</summary>
        Fleet = 2,
    }

    /// <summary>
    /// Aggregated behavior signals produced by <see cref="HabitPatternMiner"/>.
    /// </summary>
    public sealed class HabitPatternResult
    {
        /// <summary>Scope of this aggregation.</summary>
        public HabitPatternScope Scope { get; set; } = HabitPatternScope.Persona;

        /// <summary>Persona name (when <see cref="Scope"/> is Persona) or captain id.</summary>
        public string TargetId { get; set; } = "";

        /// <summary>Number of terminal missions in scope.</summary>
        public int MissionsExamined { get; set; }

        /// <summary>Of those missions, how many completed successfully.</summary>
        public int MissionsComplete { get; set; }

        /// <summary>Of those missions, how many failed.</summary>
        public int MissionsFailed { get; set; }

        /// <summary>Of those missions, how many were cancelled.</summary>
        public int MissionsCancelled { get; set; }

        /// <summary>Average <see cref="Mission.RecoveryAttempts"/> across the in-scope missions.</summary>
        public double AverageRecoveryAttempts { get; set; }

        /// <summary>Total signal-mail-received events across the in-scope missions.</summary>
        public int SignalMailReceivedTotal { get; set; }

        /// <summary>Judge sibling missions verdicting PASS.</summary>
        public int JudgePassCount { get; set; }

        /// <summary>Judge sibling missions verdicting FAIL.</summary>
        public int JudgeFailCount { get; set; }

        /// <summary>Judge sibling missions verdicting NEEDS_REVISION.</summary>
        public int JudgeNeedsRevisionCount { get; set; }

        /// <summary>Judge sibling missions with no explicit verdict marker.</summary>
        public int JudgePendingCount { get; set; }

        /// <summary>Recurring failure-mode tags parsed from FailureReason and judge notes.</summary>
        public List<FailureModeTagCount> FailureModeTags { get; set; } = new List<FailureModeTagCount>();

        /// <summary>Top edited files across the in-scope missions (signal: what kind of work this identity does).</summary>
        public List<FileTouchCount> TopTouchedFiles { get; set; } = new List<FileTouchCount>();

        /// <summary>Per-captain mission contributions for persona-curate scope.</summary>
        public List<CaptainContribution> CaptainContributions { get; set; } = new List<CaptainContribution>();

        /// <summary>Persona-role distribution for captain-curate scope.</summary>
        public List<PersonaRoleCount> PersonaRoleDistribution { get; set; } = new List<PersonaRoleCount>();

        /// <summary>Per-vessel contribution breakdown for fleet-curate scope (Reflections v2-F3).</summary>
        public List<VesselContribution> VesselContributions { get; set; } = new List<VesselContribution>();
    }

    /// <summary>Per-vessel contribution to a fleet-scoped aggregation (Reflections v2-F3).</summary>
    public sealed class VesselContribution
    {
        /// <summary>Vessel id.</summary>
        public string VesselId { get; set; } = "";

        /// <summary>Vessel name (resolved from the fleet membership query).</summary>
        public string VesselName { get; set; } = "";

        /// <summary>Total in-scope missions on this vessel.</summary>
        public int MissionCount { get; set; }

        /// <summary>Of those, how many completed successfully.</summary>
        public int CompleteCount { get; set; }

        /// <summary>Of those, how many failed.</summary>
        public int FailedCount { get; set; }

        /// <summary>Of those, how many were cancelled.</summary>
        public int CancelledCount { get; set; }
    }

    /// <summary>Failure-mode tag count entry.</summary>
    public sealed class FailureModeTagCount
    {
        /// <summary>Tag identifier from the curated taxonomy.</summary>
        public string Tag { get; set; } = "";

        /// <summary>Missions whose FailureReason matched the tag pattern.</summary>
        public int MissionCount { get; set; }
    }

    /// <summary>Top-touched-file entry.</summary>
    public sealed class FileTouchCount
    {
        /// <summary>Normalized file path.</summary>
        public string Path { get; set; } = "";

        /// <summary>Number of in-scope missions that Edited this file.</summary>
        public int EditCount { get; set; }
    }

    /// <summary>Per-captain contribution to a persona-scoped aggregation.</summary>
    public sealed class CaptainContribution
    {
        /// <summary>Captain id (or "_unassigned" placeholder).</summary>
        public string CaptainId { get; set; } = "";

        /// <summary>Optional captain runtime tag.</summary>
        public string? Runtime { get; set; }

        /// <summary>Optional captain model string.</summary>
        public string? Model { get; set; }

        /// <summary>Total in-scope missions assigned to this captain.</summary>
        public int MissionCount { get; set; }

        /// <summary>Of those, how many completed successfully.</summary>
        public int CompleteCount { get; set; }

        /// <summary>Of those, how many failed.</summary>
        public int FailedCount { get; set; }

        /// <summary>Of those, how many were cancelled.</summary>
        public int CancelledCount { get; set; }
    }

    /// <summary>Persona-role count for captain-scoped aggregation.</summary>
    public sealed class PersonaRoleCount
    {
        /// <summary>Persona name.</summary>
        public string PersonaName { get; set; } = "";

        /// <summary>Mission count for this persona role.</summary>
        public int MissionCount { get; set; }
    }
}
