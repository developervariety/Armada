namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        #region Private-Members

        private const string _ArchitectFanOutCompleteMarker = "<!-- armada:fanout-complete -->";
        private readonly ConcurrentDictionary<string, byte> _ArchitectRecoveries = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private sealed class ArchitectRecoveryUnsafeException : Exception
        {
            internal ArchitectRecoveryUnsafeException(string reason) : base(reason) { }
        }

        #endregion

        #region Private-Methods

        private static string? RemoveArchitectFanOutCompletionRecords(string? description)
        {
            if (description == null) return null;
            const string planPrefix = "<!-- armada:fanout-plan:";
            const string planSuffix = " -->";
            return String.Join("\n", description.Replace("\r\n", "\n").Split('\n').Where(line =>
            {
                if (line == _ArchitectFanOutCompleteMarker) return false;
                if (!line.StartsWith(planPrefix, StringComparison.Ordinal)
                    || !line.EndsWith(planSuffix, StringComparison.Ordinal)
                    || line.Length != planPrefix.Length + 64 + planSuffix.Length) return true;
                return !line.Substring(planPrefix.Length, 64).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
            }));
        }

        // Stable identities make a create whose acknowledgement was lost safe to repeat.
        private static string ArchitectFanOutId(string ownerId, string memberId)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(ownerId + "\n" + memberId));
            return "msn_" + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
        }

        private string ArchitectFanOutCompletionBlock(Mission architect)
        {
            string plan = System.Text.Json.JsonSerializer.Serialize(ParseArchitectOutput(architect, out string? _));
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan))).ToLowerInvariant();
            return _ArchitectFanOutCompleteMarker + "\n<!-- armada:fanout-plan:" + digest + " -->";
        }

        private bool IsArchitectFanOutComplete(Mission architect)
        {
            return (architect.Description ?? String.Empty).TrimEnd().EndsWith(ArchitectFanOutCompletionBlock(architect), StringComparison.Ordinal);
        }

        private static string ArchitectSliceId(string architectId, string templateId, int index)
        {
            return ArchitectFanOutId(architectId, templateId + ":slice:" + index.ToString(CultureInfo.InvariantCulture));
        }

        private static bool CanRepairArchitectStage(Mission mission)
        {
            return mission.Status == MissionStatusEnum.Pending && mission.StartedUtc == null
                && String.IsNullOrEmpty(mission.CaptainId) && String.IsNullOrEmpty(mission.DockId)
                && String.IsNullOrEmpty(mission.BranchName);
        }

        private static bool IsArchitectSliceRoot(Mission mission)
        {
            return String.Equals(mission.Persona, "Worker", StringComparison.OrdinalIgnoreCase)
                && (mission.Description ?? String.Empty).Contains(ArchitectHandoffMarker, StringComparison.Ordinal);
        }

        private static bool BelongsToArchitect(List<Mission> rows, Mission root, string architectId)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Mission? current = root;
            while (current != null && visited.Add(current.Id))
            {
                if (current.Id == architectId) return true;
                if (String.Equals(current.Persona, "Architect", StringComparison.OrdinalIgnoreCase)) return false;
                current = rows.FirstOrDefault(m => m.Id == current.DependsOnMissionId);
            }
            return false;
        }

        private static List<Mission> ArchitectTemplateChildren(List<Mission> rows, Mission parent)
        {
            return rows.Where(m => m.DependsOnMissionId == parent.Id && !IsChainBoundaryDependent(m) && !IsArchitectSliceRoot(m))
                .OrderBy(m => m.CreatedUtc).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        }

        private async Task<bool> MarkArchitectFanOutCompleteAsync(Mission architect, CancellationToken token)
        {
            Mission? current = await _Database.Missions.ReadAsync(architect.Id, token).ConfigureAwait(false);
            if (current == null || (current.Status != MissionStatusEnum.WorkProduced && current.Status != MissionStatusEnum.Complete)) return false;
            if (ArchitectFanOutCompletionBlock(current) != ArchitectFanOutCompletionBlock(architect))
            {
                await HoldUnsafeArchitectRecoveryAsync(current, "the plan changed during fan-out", token).ConfigureAwait(false);
                throw new ArchitectRecoveryUnsafeException("the plan changed during fan-out");
            }
            if (IsArchitectFanOutComplete(current)) return false;
            current.Description = (current.Description ?? String.Empty).TrimEnd() + "\n" + ArchitectFanOutCompletionBlock(current);
            if (!await _Database.Missions.TryUpdateIfStatusAsync(current, current.Status, token).ConfigureAwait(false)) return false;
            architect.Description = current.Description;
            return true;
        }

        private async Task<bool> TryRecoverArchitectFanOutAsync(Mission architect, CancellationToken token, bool allowOwnCompletion = false)
        {
            if (architect.HeldForOperatorReview || (!allowOwnCompletion && IsCompletionInFlight(architect.Id))
                || IsArchitectFanOutComplete(architect)
                || !_ArchitectRecoveries.TryAdd(architect.Id, 0)) return false;
            try
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(architect.VoyageId!, token).ConfigureAwait(false);
                if (voyage == null || voyage.Status == VoyageStatusEnum.Complete || voyage.Status == VoyageStatusEnum.Failed || voyage.Status == VoyageStatusEnum.Cancelled) return false;
                List<Mission> rows = await _Database.Missions.EnumerateByVoyageAsync(architect.VoyageId!, token).ConfigureAwait(false);
                List<Mission> prepared = rows.Where(m => IsArchitectSliceRoot(m) && BelongsToArchitect(rows, m, architect.Id)).ToList();
                List<Mission> templates = rows.Where(m => m.DependsOnMissionId == architect.Id
                    && String.Equals(m.Persona, "Worker", StringComparison.OrdinalIgnoreCase) && !IsArchitectSliceRoot(m)).ToList();
                if (prepared.Count == 0 && templates.Count == 1)
                {
                    if (!CanRepairArchitectStage(templates[0]) || ArchitectChainHasStarted(rows, templates[0], new HashSet<string>(StringComparer.Ordinal))) throw new ArchitectRecoveryUnsafeException("the unprepared Worker has started");
                    // The normal path owns parsing, plan evidence, mailbox notes and planner-code policy.
                    return await TryHandoffToNextStageAsync(architect, token).ConfigureAwait(false);
                }
                if (templates.Count > 0) throw new ArchitectRecoveryUnsafeException("the first Worker template is ambiguous");
                ArchitectAsyncParseResult parseResult = await ParseArchitectOutputAsync(architect, token).ConfigureAwait(false);
                List<ParsedArchitectMission> parsed = parseResult.Missions;
                if (parsed.Count == 0) throw new ArchitectRecoveryUnsafeException("the stored Architect output has no valid plan");
                if (await FailPlannerThatCommittedCodeAsync(architect, token).ConfigureAwait(false)) return false;

                HashSet<string> expectedTitles = new HashSet<string>(parsed.Select(m => m.Title + " [Worker]"), StringComparer.OrdinalIgnoreCase);
                if (prepared.Any(m => !expectedTitles.Contains(m.Title))) throw new ArchitectRecoveryUnsafeException("stored slices differ from the plan");
                List<Mission?> found = new List<Mission?>();
                for (int index = 0; index < parsed.Count; index++)
                {
                    string title = parsed[index].Title + " [Worker]";
                    List<Mission> matches = prepared.Where(m => String.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count > 1) throw new ArchitectRecoveryUnsafeException("multiple Workers match plan slice '" + parsed[index].Title + "'");
                    if (matches.Count == 0 && index == 0) throw new ArchitectRecoveryUnsafeException("the first Worker template is missing");
                    if (matches.Count == 1 && !(matches[0].Description ?? String.Empty).Contains(parsed[index].Description, StringComparison.Ordinal))
                        throw new ArchitectRecoveryUnsafeException("a stored slice brief differs from the plan");
                    found.Add(matches.SingleOrDefault());
                }
                ValidateBarrierSliceTitlesBeforeRepair(rows, found, parsed);
                Mission firstTemplate = found[0]!;
                ValidateStartedArchitectTemplate(rows, firstTemplate, parsed[0], new HashSet<string>(StringComparer.Ordinal));
                for (int index = 1; index < found.Count; index++)
                {
                    Mission? existing = found[index];
                    if (existing != null && (!CanRepairArchitectStage(existing) || ArchitectChainHasStarted(rows, existing, new HashSet<string>(StringComparer.Ordinal))))
                        await RepairArchitectCloneAsync(rows, firstTemplate, existing, parsed[index], true, new HashSet<string>(StringComparer.Ordinal), token).ConfigureAwait(false);
                }
                ValidateStartedArchitectDependencies(architect, rows, found, parsed);
                List<Mission> roots = new List<Mission>();
                string? startRef = await ResolvePlannerFanOutStartRefAsync(architect, token).ConfigureAwait(false);
                List<Signal> unreadSignals = await LoadUnreadMailboxSignalsAsync(token).ConfigureAwait(false);
                HashSet<string> appliedSignals = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < parsed.Count; index++)
                {
                    if (found[index] != null) { roots.Add(found[index]!); continue; }
                    string id = ArchitectSliceId(architect.Id, firstTemplate.Id, index);
                    if (rows.Any(m => m.Id == id)) throw new ArchitectRecoveryUnsafeException("a slice identity has conflicting stored data");
                    string description = ArchitectHandoffMarker + "\n" + parsed[index].Description;
                    List<Signal> applicable = GetApplicableMailboxSignals(unreadSignals, null, architect.VoyageId);
                    if (applicable.Count > 0)
                    {
                        description = BuildMailboxNotesBlock(applicable) + "\n\n" + description;
                        foreach (Signal signal in applicable) appliedSignals.Add(signal.Id);
                    }
                    Mission worker = new Mission(parsed[index].Title + " [Worker]", description);
                    worker.Id = id;
                    worker.TenantId = architect.TenantId;
                    worker.UserId = architect.UserId;
                    worker.VoyageId = architect.VoyageId;
                    worker.VesselId = architect.VesselId;
                    worker.Persona = "Worker";
                    worker.DependsOnMissionId = architect.Id;
                    worker.Mode = architect.Mode;
                    worker.IsEvaluation = architect.IsEvaluation;
                    worker.StartFromRef = startRef;
                    if (parsed[index].WaitForOtherMissions)
                        worker.IntegrationBarrier = CreatePreparingIntegrationBarrier(architect, worker, parsed);
                    worker = await _Database.Missions.CreateAsync(worker, token).ConfigureAwait(false);
                    roots.Add(worker);
                    rows.Add(worker);
                }
                Mission template = roots[0];
                await RepairArchitectTemplateAsync(rows, template, parsed[0], new HashSet<string>(StringComparer.Ordinal), token).ConfigureAwait(false);
                for (int index = 1; index < parsed.Count; index++)
                {
                    bool frozen = !CanRepairArchitectStage(roots[index]) || ArchitectChainHasStarted(rows, roots[index], new HashSet<string>(StringComparer.Ordinal));
                    await RepairArchitectCloneAsync(rows, template, roots[index], parsed[index], frozen,
                        new HashSet<string>(StringComparer.Ordinal), token).ConfigureAwait(false);
                }
                if (parsed[parsed.Count - 1].WaitForOtherMissions
                    && !await EnsurePreparingIntegrationBarrierAsync(architect, roots[roots.Count - 1], parsed, token).ConfigureAwait(false))
                    throw new ArchitectRecoveryUnsafeException("the integration root has no matching preparing barrier");
                List<Mission> acceptedTerminals = GetArchitectBarrierTerminals(rows, roots);
                await ApplyRecoveredArchitectDependenciesAsync(architect, rows, roots, parsed, token).ConfigureAwait(false);
                if (parsed[parsed.Count - 1].WaitForOtherMissions)
                    await AcceptArchitectIntegrationBarrierAsync(architect, parsed, roots, acceptedTerminals, token).ConfigureAwait(false);
                foreach (Signal signal in unreadSignals)
                {
                    List<Mission> applicableRoots = roots.Where(root => GetApplicableMailboxSignals(new List<Signal> { signal }, root.Id, architect.VoyageId).Count > 0).ToList();
                    if (applicableRoots.Count > 0 && applicableRoots.All(root => ArchitectBriefContainsMailboxSignal(root.Description, signal))) appliedSignals.Add(signal.Id);
                }
                foreach (string signalId in appliedSignals) await _Database.Signals.MarkReadAsync(signalId, token).ConfigureAwait(false);
                bool marked = await MarkArchitectFanOutCompleteAsync(architect, token).ConfigureAwait(false);
                if (marked) _Logging.Info(_Header + "finished interrupted Architect fan-out for mission " + architect.Id);
                return marked;
            }
            catch (ArchitectRecoveryUnsafeException ex)
            {
                await HoldUnsafeArchitectRecoveryAsync(architect, ex.Message, token).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "Architect fan-out recovery will retry mission " + architect.Id + ": " + ex.Message);
                return false;
            }
            finally { _ArchitectRecoveries.TryRemove(architect.Id, out _); }
        }

        private static bool ArchitectBriefContainsMailboxSignal(string? description, Signal signal)
        {
            VoyageMailboxSignalPayload? payload = TryParseMailboxPayload(signal.Payload);
            if (String.IsNullOrEmpty(payload?.Message) || String.IsNullOrEmpty(description)) return false;
            string text = description.Replace("\r\n", "\n");
            const string open = "[ORCHESTRATOR NOTES]";
            const string close = "[/ORCHESTRATOR NOTES]";
            int start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return false;
            int end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
            if (end < 0) return false;
            string notes = "\n" + text.Substring(start + open.Length, end - start - open.Length).Trim('\r', '\n') + "\n";
            return notes.Contains("\n" + payload.Message.Replace("\r\n", "\n") + "\n", StringComparison.Ordinal);
        }

        private static bool ArchitectChainHasStarted(List<Mission> rows, Mission parent, HashSet<string> visited)
        {
            if (!visited.Add(parent.Id)) return true;
            foreach (Mission child in ArchitectTemplateChildren(rows, parent))
                if (!CanRepairArchitectStage(child) || ArchitectChainHasStarted(rows, child, visited)) return true;
            return false;
        }

        private static void ValidateStartedArchitectTemplate(List<Mission> rows, Mission parent, ParsedArchitectMission parsed, HashSet<string> visited)
        {
            if (!visited.Add(parent.Id)) throw new ArchitectRecoveryUnsafeException("the template chain contains a cycle");
            foreach (Mission child in ArchitectTemplateChildren(rows, parent))
            {
                if (!CanRepairArchitectStage(child) && (child.Title != parsed.Title + " [" + child.Persona + "]"
                    || !(child.Description ?? String.Empty).Contains(parsed.Description, StringComparison.Ordinal)))
                    throw new ArchitectRecoveryUnsafeException("an unprepared template stage has started");
                ValidateStartedArchitectTemplate(rows, child, parsed, visited);
            }
        }

        private static Mission ArchitectRecoveryTerminal(List<Mission> rows, Mission root)
        {
            Mission terminal = root;
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(terminal.Id))
            {
                Mission? next = ArchitectTemplateChildren(rows, terminal).FirstOrDefault();
                if (next == null) return terminal;
                terminal = next;
            }
            throw new ArchitectRecoveryUnsafeException("a slice chain contains a cycle");
        }

        private static void ValidateStartedArchitectDependencies(Mission architect, List<Mission> rows, List<Mission?> roots, List<ParsedArchitectMission> parsed)
        {
            Dictionary<int, Mission> byIndex = new Dictionary<int, Mission>();
            Dictionary<string, Mission> byTitle = new Dictionary<string, Mission>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < roots.Count; index++)
            {
                if (roots[index] == null) continue;
                Mission terminal = ArchitectRecoveryTerminal(rows, roots[index]!);
                byIndex[index + 1] = terminal;
                byTitle[parsed[index].Title] = terminal;
            }
            for (int index = 0; index < roots.Count; index++)
            {
                Mission? root = roots[index];
                if (root == null || (CanRepairArchitectStage(root) && !ArchitectChainHasStarted(rows, root, new HashSet<string>(StringComparer.Ordinal)))) continue;
                string? reference = parsed[index].DependsOnReference;
                Mission? dependency = String.IsNullOrWhiteSpace(reference) ? architect
                    : ResolveArchitectDependencyTerminalStage(byIndex, byTitle, index + 1, reference);
                if (dependency == null || root.DependsOnMissionId != dependency.Id)
                    throw new ArchitectRecoveryUnsafeException("a started slice has a different dependency");
            }
        }

        private async Task RepairArchitectTemplateAsync(List<Mission> rows, Mission parent, ParsedArchitectMission parsed,
            HashSet<string> visited, CancellationToken token)
        {
            if (!visited.Add(parent.Id)) throw new ArchitectRecoveryUnsafeException("the template chain contains a cycle");
            foreach (Mission child in ArchitectTemplateChildren(rows, parent))
            {
                string expectedTitle = parsed.Title + " [" + child.Persona + "]";
                if (CanRepairArchitectStage(child))
                {
                    if (child.Title != expectedTitle || child.Description != parsed.Description)
                    {
                        child.Title = expectedTitle;
                        child.Description = parsed.Description;
                        if (!await _Database.Missions.TryUpdateIfStatusAsync(child, MissionStatusEnum.Pending, token).ConfigureAwait(false))
                            throw new ArchitectRecoveryUnsafeException("a template stage started during repair");
                    }
                }
                else if (child.Title != expectedTitle || !(child.Description ?? String.Empty).Contains(parsed.Description, StringComparison.Ordinal)) throw new ArchitectRecoveryUnsafeException("an unprepared template stage has started");
                await RepairArchitectTemplateAsync(rows, child, parsed, visited, token).ConfigureAwait(false);
            }
        }

        private async Task RepairArchitectCloneAsync(List<Mission> rows, Mission template, Mission parent, ParsedArchitectMission parsed,
            bool frozen, HashSet<string> visited, CancellationToken token)
        {
            if (!visited.Add(template.Id)) throw new ArchitectRecoveryUnsafeException("the template chain contains a cycle");
            List<Mission> expectedChildren = ArchitectTemplateChildren(rows, template);
            HashSet<string> claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (Mission expected in expectedChildren)
            {
                string id = ArchitectFanOutId(parent.Id, expected.Id);
                string title = parsed.Title + " [" + expected.Persona + "]";
                List<Mission> children = ArchitectTemplateChildren(rows, parent);
                Mission? child = children.FirstOrDefault(m => m.Id == id);
                if (child == null)
                {
                    if (rows.Any(m => m.Id == id)) throw new ArchitectRecoveryUnsafeException("a cloned stage has moved to a different dependency");
                    List<Mission> candidates = children.Where(m => !claimed.Contains(m.Id) && m.Title == title
                        && String.Equals(m.Persona, expected.Persona, StringComparison.OrdinalIgnoreCase) && m.Mode == expected.Mode
                        && m.VesselId == expected.VesselId && m.StageOrder == null).ToList();
                    int identicalTemplates = expectedChildren.Count(m => m.Persona == expected.Persona);
                    if (candidates.Count > 1 || (candidates.Count > 0 && identicalTemplates > 1))
                        throw new ArchitectRecoveryUnsafeException("cloned stages cannot be matched unambiguously");
                    child = candidates.SingleOrDefault();
                    if (child != null && !(child.Description ?? String.Empty).Contains(parsed.Description, StringComparison.Ordinal))
                        throw new ArchitectRecoveryUnsafeException("a legacy cloned stage has an unrecognised brief");
                }
                if (child == null)
                {
                    if (frozen) throw new ArchitectRecoveryUnsafeException("a started slice has an incomplete stage chain");
                    child = new Mission(title, parsed.Description);
                    child.Id = id;
                    child.TenantId = expected.TenantId;
                    child.UserId = expected.UserId;
                    child.VoyageId = expected.VoyageId;
                    child.VesselId = expected.VesselId;
                    child.Persona = expected.Persona;
                    child.DependsOnMissionId = parent.Id;
                    child.Mode = expected.Mode;
                    child.IsEvaluation = expected.IsEvaluation;
                    child.StageOrder = null;
                    child = await _Database.Missions.CreateAsync(child, token).ConfigureAwait(false);
                    rows.Add(child);
                }
                else if (!frozen && child.Description != parsed.Description)
                    throw new ArchitectRecoveryUnsafeException("a cloned stage has an edited brief");
                claimed.Add(child.Id);
                await RepairArchitectCloneAsync(rows, expected, child, parsed, frozen, visited, token).ConfigureAwait(false);
            }
            if (ArchitectTemplateChildren(rows, parent).Any(m => !claimed.Contains(m.Id)))
                throw new ArchitectRecoveryUnsafeException("a slice has unmatched additional stages");
        }

        private async Task ApplyRecoveredArchitectDependenciesAsync(Mission architect, List<Mission> rows, List<Mission> roots,
            List<ParsedArchitectMission> parsed, CancellationToken token)
        {
            Dictionary<int, Mission> terminalsByIndex = new Dictionary<int, Mission>();
            Dictionary<string, Mission> terminalsByTitle = new Dictionary<string, Mission>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < roots.Count; index++)
            {
                Mission terminal = ArchitectRecoveryTerminal(rows, roots[index]);
                terminalsByIndex[index + 1] = terminal;
                terminalsByTitle[parsed[index].Title] = terminal;
            }
            for (int index = 0; index < roots.Count; index++)
            {
                string? reference = parsed[index].DependsOnReference;
                Mission? dependency = String.IsNullOrWhiteSpace(reference) ? architect
                    : ResolveArchitectDependencyTerminalStage(terminalsByIndex, terminalsByTitle, index + 1, reference);
                if (dependency == null || dependency.Id == roots[index].Id)
                    throw new ArchitectRecoveryUnsafeException("a plan dependency cannot be resolved safely");
                Mission root = roots[index];
                if (root.DependsOnMissionId == dependency.Id) continue;
                if (!CanRepairArchitectStage(root) || ArchitectChainHasStarted(rows, root, new HashSet<string>(StringComparer.Ordinal)))
                    throw new ArchitectRecoveryUnsafeException("a started slice has a different dependency");
                root.DependsOnMissionId = dependency.Id;
                if (!await _Database.Missions.TryUpdateIfStatusAsync(root, MissionStatusEnum.Pending, token).ConfigureAwait(false))
                    throw new ArchitectRecoveryUnsafeException("a slice started during dependency repair");
            }
        }

        private async Task<Mission> ClearArchitectRecoveryHoldAsync(Mission mission, string operatorName, string reason, CancellationToken token)
        {
            string previousHold = mission.HeldForOperatorReviewReason ?? String.Empty;
            ClearOperatorReviewHold(mission);
            if (!await _Database.Missions.TryUpdateIfStatusAsync(mission, mission.Status, token).ConfigureAwait(false))
                throw new InvalidOperationException("The Architect changed status before its recovery hold could be cleared.");
            await TryRecoverArchitectFanOutAsync(mission, token).ConfigureAwait(false);
            Mission current = (await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false))!;
            if (!IsArchitectFanOutComplete(current))
            {
                await HoldUnsafeArchitectRecoveryAsync(current, "the graph still cannot be reconciled safely", token).ConfigureAwait(false);
                throw new InvalidOperationException("Architect fan-out recovery is still unsafe. The hold remains; repair the graph or fail the held mission.");
            }
            await RecordHoldEventAsync(current, OperatorHoldClearedEventType, "Architect recovery hold cleared after safe reconciliation",
                operatorName.Trim(), reason.Trim(), previousHold, token).ConfigureAwait(false);
            await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
            return current;
        }

        private async Task HoldUnsafeArchitectRecoveryAsync(Mission architect, string reason, CancellationToken token)
        {
            Mission? current = await _Database.Missions.ReadAsync(architect.Id, token).ConfigureAwait(false);
            if (current == null || current.HeldForOperatorReview || (current.Status != MissionStatusEnum.WorkProduced && current.Status != MissionStatusEnum.Complete)) return;
            current.HeldForOperatorReview = true;
            current.HeldForOperatorReviewReason = "architect_fanout_recovery: " + reason;
            if (!await _Database.Missions.TryUpdateIfStatusAsync(current, current.Status, token).ConfigureAwait(false)) return;
            architect.HeldForOperatorReview = current.HeldForOperatorReview;
            architect.HeldForOperatorReviewReason = current.HeldForOperatorReviewReason;
            await AppendMissionActivityAsync(current.Id, "held for operator review: " + current.HeldForOperatorReviewReason, token).ConfigureAwait(false);
            await RecordHoldEventAsync(current, "mission.architect_fanout_recovery_held", "Architect fan-out needs operator review", "recovery",
                reason, current.HeldForOperatorReviewReason, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "Architect fan-out held for mission " + current.Id + ": " + reason);
        }

        #endregion
    }
}
