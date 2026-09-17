namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Service for resolving and rendering prompt templates.
    /// Supports database-stored overrides with embedded resource defaults as fallback.
    /// </summary>
    public class PromptTemplateService : IPromptTemplateService
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private string _Header = "[PromptTemplateService] ";

        /// <summary>
        /// Heading of the memory-recall section. Presence of this heading means a template already
        /// carries the guidance, so the guidance is added once and never twice.
        /// </summary>
        private const string _MemoryRecallMarker = "## Recall Existing Memory";

        /// <summary>
        /// Guidance added to every built-in working persona template so an agent recalls what earlier
        /// work recorded before it acts. The Recorder writes memory; every other persona reads it.
        /// </summary>
        private const string _MemoryRecallGuidance =
            "\n" +
            _MemoryRecallMarker + "\n" +
            "Before you act, recall what earlier work on this vessel recorded. Read the vessel model " +
            "context in this prompt, and when the memory tools are available call `search_memory` with " +
            "this vessel and with keywords from your mission, then `get_memory` for a record that matters. " +
            "Reuse the conventions, decisions and procedures already recorded instead of deriving them " +
            "again.\n" +
            "A memory record is working memory, not proof. Check it against the current checkout before " +
            "you depend on it, and say so in your summary when it is wrong or stale.\n" +
            "When this brief carries a Shared Memory section, that shared memory is the authority: a " +
            "shared rule wins over a memory record on conflict, and so do the mission brief, the " +
            "playbooks and the vessel instructions. Report the conflict; do not rewrite either side.\n" +
            "When the memory tools are absent, continue without them.\n";

        /// <summary>
        /// Marker meaning a mission-rules template already carries the standing fleet guard rules.
        /// </summary>
        private const string _FleetGuardMarker = "Never delete a `recover/` ref";

        /// <summary>
        /// Standing fleet rules appended to every mission-rules template: a recover ref is the
        /// operator's to retire, and an orchestration id never enters committed content. Added once, so
        /// a row already carrying the marker is left as it is and an operator edit is kept.
        /// </summary>
        private const string _FleetGuardRules =
            "- " + _FleetGuardMarker + "; the operator retires them.\n" +
            "- Never write a mission, voyage, or objective id into committed content -- code, comments, tests, commit messages, or branch names.\n";

        /// <summary>
        /// Heading of the blocked-path section. Presence means a template already carries the guidance.
        /// </summary>
        private const string _BlockedPathMarker = "## When You Cannot Finish";

        /// <summary>
        /// The D20 blocked path added to every working persona: a mission a captain cannot achieve ends
        /// with `[ARMADA:RESULT] BLOCKED` and the blocker, not a false COMPLETE. Added once, so a row
        /// carrying the marker is left as it is and an operator edit is kept.
        /// </summary>
        private const string _BlockedPathGuidance =
            "\n" +
            _BlockedPathMarker + "\n" +
            "If you cannot achieve the mission's goal -- missing context, a false premise, or a question " +
            "only the owner can answer -- end your final response with a standalone `[ARMADA:RESULT] BLOCKED` " +
            "line followed by the specific blocker or question, instead of `[ARMADA:RESULT] COMPLETE`. Use " +
            "`[ARMADA:RESULT] REFUSED` only when the request is unsafe or disallowed. Never report COMPLETE " +
            "for work you did not finish.\n";
        private DatabaseDriver _Database;
        private LoggingModule _Logging;
        private Dictionary<string, EmbeddedTemplate> _EmbeddedDefaults;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="additionalTemplates">
        /// Optional extra templates from settings. Product defaults do not include
        /// deployment-specific specialist reviewers; those are supplied here.
        /// </param>
        public PromptTemplateService(
            DatabaseDriver database,
            LoggingModule logging,
            IReadOnlyList<AdditionalPromptTemplateSettings>? additionalTemplates = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _EmbeddedDefaults = BuildEmbeddedDefaults();
            MergeAdditionalTemplates(additionalTemplates);
            AddMemoryRecallGuidance();
            AddFleetGuardRules();
            AddBlockedPathGuidance();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve a template by name. Checks database first, falls back to embedded resource.
        /// </summary>
        public async Task<PromptTemplate?> ResolveAsync(string name, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            PromptTemplate? dbTemplate = await _Database.PromptTemplates.ReadByNameAsync(name, token).ConfigureAwait(false);

            // Resolution builds prompts that run for every tenant and user, so a user-specific
            // template is never used here. Its owner still reads it through the scoped read routes.
            if (dbTemplate != null && dbTemplate.OwnershipScope != Armada.Core.Enums.OwnershipScopeEnum.UserSpecific)
            {
                return dbTemplate;
            }

            if (_EmbeddedDefaults.TryGetValue(name, out EmbeddedTemplate? embedded))
            {
                PromptTemplate fallback = new PromptTemplate(name, embedded.Content)
                {
                    Description = embedded.Description,
                    Category = embedded.Category,
                    IsBuiltIn = true
                };
                return fallback;
            }

            return null;
        }

        /// <summary>
        /// Render a template by name with placeholder substitution.
        /// </summary>
        public async Task<string> RenderAsync(string name, Dictionary<string, string> parameters, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            PromptTemplate? template = await ResolveAsync(name, token).ConfigureAwait(false);
            if (template == null)
            {
                return "";
            }

            string result = template.Content;
            if (parameters != null && parameters.Count > 0)
            {
                foreach (KeyValuePair<string, string> kvp in parameters)
                {
                    result = result.Replace("{" + kvp.Key + "}", kvp.Value ?? "");
                }
            }

            return result;
        }

        /// <summary>
        /// Seed all built-in templates into the database if they don't already exist.
        /// Called on startup.
        /// </summary>
        public async Task SeedDefaultsAsync(CancellationToken token = default)
        {
            foreach (KeyValuePair<string, EmbeddedTemplate> kvp in _EmbeddedDefaults)
            {
                string name = kvp.Key;
                EmbeddedTemplate embedded = kvp.Value;

                PromptTemplate? existing = await _Database.PromptTemplates.ReadByNameAsync(name, token).ConfigureAwait(false);
                if (existing == null)
                {
                    PromptTemplate template = new PromptTemplate(name, embedded.Content)
                    {
                        Description = embedded.Description,
                        Category = embedded.Category,
                        IsBuiltIn = true
                    };

                    await _Database.PromptTemplates.CreateAsync(template, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "seeded built-in template '" + name + "'");
                }
                else if (!existing.IsBuiltIn ||
                    !String.Equals(existing.Description, embedded.Description, StringComparison.Ordinal) ||
                    !String.Equals(existing.Category, embedded.Category, StringComparison.Ordinal))
                {
                    existing.Description = embedded.Description;
                    existing.Category = embedded.Category;
                    existing.IsBuiltIn = true;

                    await _Database.PromptTemplates.UpdateAsync(existing, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "reconciled built-in template metadata '" + name + "'");
                }
            }

            // Run the content upgrader first, while the live rows still hold their raw content: the
            // reference and memory-recall upgraders below mutate content, which would change a stale
            // row's hash and make it read as an operator edit.
            await UpgradeBuiltInPersonaContentAsync(token).ConfigureAwait(false);
            await UpgradeLegacyPersonaTemplateReferencesAsync(token).ConfigureAwait(false);
            await UpgradeBuiltInPersonaMemoryRecallAsync(token).ConfigureAwait(false);
            await UpgradeBuiltInMissionRuleFleetGuardsAsync(token).ConfigureAwait(false);
            await UpgradeBuiltInPersonaBlockedPathAsync(token).ConfigureAwait(false);
        }

        /// <summary>
        /// List all templates, optionally filtered by category.
        /// </summary>
        public async Task<List<PromptTemplate>> ListAsync(string? category = null, CancellationToken token = default)
        {
            List<PromptTemplate> templates = await _Database.PromptTemplates.EnumerateAsync(token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(category))
            {
                templates = templates.Where(t => String.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return templates;
        }

        /// <summary>
        /// Reset a template to its embedded resource default content.
        /// </summary>
        public async Task<PromptTemplate?> ResetToDefaultAsync(string name, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            if (!_EmbeddedDefaults.TryGetValue(name, out EmbeddedTemplate? embedded))
            {
                return null;
            }

            PromptTemplate? existing = await _Database.PromptTemplates.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (existing != null)
            {
                existing.Content = embedded.Content;
                existing.Description = embedded.Description;
                existing.Category = embedded.Category;
                existing.IsBuiltIn = true;
                existing.LastUpdateUtc = DateTime.UtcNow;

                PromptTemplate updated = await _Database.PromptTemplates.UpdateAsync(existing, token).ConfigureAwait(false);
                _Logging.Info(_Header + "reset template '" + name + "' to embedded default");
                return updated;
            }
            else
            {
                PromptTemplate template = new PromptTemplate(name, embedded.Content)
                {
                    Description = embedded.Description,
                    Category = embedded.Category,
                    IsBuiltIn = true
                };

                PromptTemplate created = await _Database.PromptTemplates.CreateAsync(template, token).ConfigureAwait(false);
                _Logging.Info(_Header + "created template '" + name + "' from embedded default");
                return created;
            }
        }

        /// <summary>
        /// Get the embedded resource default content for a template by name.
        /// Returns null if no embedded default exists.
        /// </summary>
        public string? GetEmbeddedDefault(string name)
        {
            if (String.IsNullOrEmpty(name)) return null;

            if (_EmbeddedDefaults.TryGetValue(name, out EmbeddedTemplate? embedded))
            {
                return embedded.Content;
            }

            return null;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Whether a built-in persona template takes the memory-recall guidance. The Recorder writes
        /// memory rather than recalling it.
        /// </summary>
        private static bool TakesMemoryRecallGuidance(string? name, string? category)
        {
            if (!String.Equals(category, "persona", StringComparison.OrdinalIgnoreCase)) return false;
            if (String.Equals(name, "persona.recorder", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>Whether a template takes the standing fleet guard rules: the two mission-rules templates.</summary>
        private static bool TakesFleetGuardRules(string? name)
        {
            return String.Equals(name, "mission.rules", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "mission.rules_no_push", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Append the fleet guard rules to the embedded mission-rules defaults, so seeding, fallback
        /// resolution and reset all deliver the same text.
        /// </summary>
        private void AddFleetGuardRules()
        {
            foreach (KeyValuePair<string, EmbeddedTemplate> pair in _EmbeddedDefaults)
            {
                EmbeddedTemplate template = pair.Value;
                if (!TakesFleetGuardRules(pair.Key)) continue;
                if (template.Content != null && template.Content.Contains(_FleetGuardMarker, StringComparison.Ordinal)) continue;
                template.Content = (template.Content ?? String.Empty) + _FleetGuardRules;
            }
        }

        /// <summary>
        /// Append the fleet guard rules to an existing built-in mission-rules row that predates them.
        /// Seeding only creates an absent template, so an existing row would otherwise never receive the
        /// rules. This appends the missing lines and changes nothing else, so an operator edit is kept.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        private async Task UpgradeBuiltInMissionRuleFleetGuardsAsync(CancellationToken token)
        {
            List<PromptTemplate> templates = await _Database.PromptTemplates.EnumerateAsync(token).ConfigureAwait(false);
            foreach (PromptTemplate template in templates)
            {
                if (!template.IsBuiltIn) continue;
                if (!TakesFleetGuardRules(template.Name)) continue;
                if (!String.IsNullOrEmpty(template.Content) && template.Content.Contains(_FleetGuardMarker, StringComparison.Ordinal)) continue;

                template.Content = (template.Content ?? String.Empty) + _FleetGuardRules;
                template.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PromptTemplates.UpdateAsync(template, token).ConfigureAwait(false);
                _Logging.Info(_Header + "added fleet guard rules to built-in template '" + template.Name + "'");
            }
        }

        /// <summary>Whether a template takes the blocked path: a working persona, but not the Recorder or the Judge.</summary>
        private static bool TakesBlockedPathGuidance(string? name, string? category)
        {
            if (!String.Equals(category, "persona", StringComparison.OrdinalIgnoreCase)) return false;
            if (String.Equals(name, "persona.recorder", StringComparison.OrdinalIgnoreCase)) return false;
            if (String.Equals(name, "persona.judge", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>
        /// Append the blocked-path guidance to the embedded working-persona defaults, so seeding,
        /// fallback resolution and reset all deliver the same text.
        /// </summary>
        private void AddBlockedPathGuidance()
        {
            foreach (KeyValuePair<string, EmbeddedTemplate> pair in _EmbeddedDefaults)
            {
                EmbeddedTemplate template = pair.Value;
                if (!TakesBlockedPathGuidance(pair.Key, template.Category)) continue;
                if (template.Content != null && template.Content.Contains(_BlockedPathMarker, StringComparison.Ordinal)) continue;
                template.Content = (template.Content ?? String.Empty) + _BlockedPathGuidance;
            }
        }

        /// <summary>
        /// Append the blocked-path guidance to an existing built-in working-persona row that predates it.
        /// Seeding only creates an absent template, so an existing row would otherwise never receive it.
        /// This appends the missing section and changes nothing else, so an operator edit is kept.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        private async Task UpgradeBuiltInPersonaBlockedPathAsync(CancellationToken token)
        {
            List<PromptTemplate> templates = await _Database.PromptTemplates.EnumerateAsync(token).ConfigureAwait(false);
            foreach (PromptTemplate template in templates)
            {
                if (!template.IsBuiltIn) continue;
                if (!TakesBlockedPathGuidance(template.Name, template.Category)) continue;
                if (!String.IsNullOrEmpty(template.Content) && template.Content.Contains(_BlockedPathMarker, StringComparison.Ordinal)) continue;

                template.Content = (template.Content ?? String.Empty) + _BlockedPathGuidance;
                template.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PromptTemplates.UpdateAsync(template, token).ConfigureAwait(false);
                _Logging.Info(_Header + "added blocked-path guidance to built-in template '" + template.Name + "'");
            }
        }

        /// <summary>
        /// Add the guidance to the embedded defaults, so seeding, fallback resolution and reset all
        /// deliver the same text.
        /// </summary>
        private void AddMemoryRecallGuidance()
        {
            foreach (KeyValuePair<string, EmbeddedTemplate> pair in _EmbeddedDefaults)
            {
                EmbeddedTemplate template = pair.Value;
                if (!TakesMemoryRecallGuidance(pair.Key, template.Category)) continue;
                if (template.Content != null && template.Content.Contains(_MemoryRecallMarker, StringComparison.Ordinal)) continue;
                template.Content = (template.Content ?? String.Empty) + _MemoryRecallGuidance;
            }
        }

        /// <summary>
        /// Bring a deployment that was created before the guidance existed up to date. Seeding only
        /// creates a template that is absent, so an existing built-in persona template would otherwise
        /// never receive the guidance. This appends the missing section and changes nothing else, so an
        /// operator edit is kept.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        private async Task UpgradeBuiltInPersonaMemoryRecallAsync(CancellationToken token)
        {
            List<PromptTemplate> templates = await _Database.PromptTemplates.EnumerateAsync(token).ConfigureAwait(false);
            foreach (PromptTemplate template in templates)
            {
                if (!template.IsBuiltIn) continue;
                if (!TakesMemoryRecallGuidance(template.Name, template.Category)) continue;
                if (!String.IsNullOrEmpty(template.Content) && template.Content.Contains(_MemoryRecallMarker, StringComparison.Ordinal)) continue;

                template.Content = (template.Content ?? String.Empty) + _MemoryRecallGuidance;
                template.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PromptTemplates.UpdateAsync(template, token).ConfigureAwait(false);
                _Logging.Info(_Header + "added memory-recall guidance to built-in template '" + template.Name + "'");
            }
        }

        /// <summary>
        /// The decision the content upgrader makes for one built-in row.
        /// </summary>
        internal enum TemplateContentDecision
        {
            /// <summary>The live content already equals the current embedded default.</summary>
            Current,

            /// <summary>The live content equals a superseded embedded version, so it is safe to upgrade.</summary>
            Upgrade,

            /// <summary>The live content matches no known embedded version, so an operator edited it.</summary>
            Drift
        }

        /// <summary>
        /// Classify a built-in row's live content against the current embedded content and the known
        /// prior embedded versions. Pure and side-effect free so the upgrade rule is tested without a
        /// database: <see cref="TemplateContentDecision.Upgrade"/> only when the live content matches a
        /// recorded prior version, never a version the upgrader has not seen.
        /// </summary>
        /// <param name="liveContent">The content of the live built-in row.</param>
        /// <param name="embeddedContent">The current embedded default content.</param>
        /// <param name="priorHashes">Lowercase SHA-256 hex of superseded embedded versions.</param>
        /// <returns>The decision.</returns>
        internal static TemplateContentDecision ClassifyBuiltInContent(string liveContent, string embeddedContent, IReadOnlyList<string>? priorHashes)
        {
            string liveHash = HashContent(liveContent);
            if (String.Equals(liveHash, HashContent(embeddedContent), StringComparison.Ordinal))
                return TemplateContentDecision.Current;
            if (priorHashes != null && priorHashes.Contains(liveHash, StringComparer.Ordinal))
                return TemplateContentDecision.Upgrade;
            return TemplateContentDecision.Drift;
        }

        /// <summary>
        /// Lowercase SHA-256 hex of a template content string, the stable identity a prior embedded
        /// version is recorded and matched by.
        /// </summary>
        /// <param name="content">The content to hash; null is treated as empty.</param>
        /// <returns>The 64-character lowercase hex digest.</returns>
        internal static string HashContent(string? content)
        {
            byte[] digest = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content ?? String.Empty));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        /// <summary>
        /// Replace a built-in row that still holds a superseded embedded version with the current
        /// embedded content, and leave an operator-edited row alone. Seeding never rewrites the content
        /// of an existing row, so without this a content change in code never reaches a live row. Only a
        /// row whose content matches a recorded prior version is upgraded; a row matching no known
        /// version is logged and recorded for a manual merge. Every outcome records an event.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        private async Task UpgradeBuiltInPersonaContentAsync(CancellationToken token)
        {
            foreach (KeyValuePair<string, EmbeddedTemplate> pair in _EmbeddedDefaults)
            {
                EmbeddedTemplate embedded = pair.Value;
                if (embedded.PriorContentHashes.Count == 0) continue;

                PromptTemplate? existing = await _Database.PromptTemplates.ReadByNameAsync(pair.Key, token).ConfigureAwait(false);
                if (existing == null || !existing.IsBuiltIn) continue;

                string liveContent = existing.Content ?? String.Empty;
                TemplateContentDecision decision = ClassifyBuiltInContent(liveContent, embedded.Content, embedded.PriorContentHashes);
                if (decision == TemplateContentDecision.Current) continue;

                string liveHash = HashContent(liveContent);
                if (decision == TemplateContentDecision.Upgrade)
                {
                    existing.Content = embedded.Content;
                    existing.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.PromptTemplates.UpdateAsync(existing, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "upgraded built-in template '" + pair.Key + "' from superseded version " + liveHash.Substring(0, 12));
                    await RecordTemplateContentEventAsync(
                        "prompt_template.content_upgraded", existing.Id, pair.Key,
                        "upgraded '" + pair.Key + "' from " + liveHash.Substring(0, 12) + " to " + HashContent(embedded.Content).Substring(0, 12), token).ConfigureAwait(false);
                }
                else
                {
                    _Logging.Info(_Header + "built-in template '" + pair.Key + "' content " + liveHash.Substring(0, 12)
                        + " matches no known embedded version; left for operator merge");
                    await RecordTemplateContentEventAsync(
                        "prompt_template.content_drift", existing.Id, pair.Key,
                        "'" + pair.Key + "' content " + liveHash.Substring(0, 12) + " matches no embedded version; left for operator merge", token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Record a content-upgrade or content-drift event, swallowing a recorder failure so an event
        /// write never blocks seeding.
        /// </summary>
        private async Task RecordTemplateContentEventAsync(string eventType, string? entityId, string name, string message, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message)
                {
                    EntityType = "prompt_template",
                    EntityId = entityId
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record " + eventType + " for '" + name + "': " + ex.Message);
            }
        }

        private async Task UpgradeLegacyPersonaTemplateReferencesAsync(CancellationToken token)
        {
            List<PromptTemplate> templates = await _Database.PromptTemplates.EnumerateAsync(token).ConfigureAwait(false);
            foreach (PromptTemplate template in templates)
            {
                if (!template.IsBuiltIn) continue;

                string? updatedDescription = PersonaCatalog.ReplaceLegacyTestEngineer(template.Description);
                string updatedContent = PersonaCatalog.ReplaceLegacyTestEngineer(template.Content) ?? template.Content;
                bool changed =
                    !String.Equals(updatedDescription, template.Description, StringComparison.Ordinal) ||
                    !String.Equals(updatedContent, template.Content, StringComparison.Ordinal);

                if (!changed) continue;

                template.Description = updatedDescription;
                template.Content = updatedContent;
                template.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PromptTemplates.UpdateAsync(template, token).ConfigureAwait(false);
                _Logging.Info(_Header + "updated built-in template references: '" + template.Name + "'");
            }
        }

        private Dictionary<string, EmbeddedTemplate> BuildEmbeddedDefaults()
        {
            Dictionary<string, EmbeddedTemplate> defaults = new Dictionary<string, EmbeddedTemplate>();

            defaults["mission.rules"] = new EmbeddedTemplate
            {
                Name = "mission.rules",
                Description = "Standard rules injected into every mission prompt.",
                Category = "mission",
                Content =
                    "## Rules\n" +
                    "- Work only within this worktree directory\n" +
                    "- Stay strictly within the mission scope and listed files\n" +
                    "- Do not create, modify, or delete files outside the listed scope unless the mission explicitly requires it\n" +
                    "- If you discover a necessary out-of-scope change, report it in your result instead of expanding scope on your own\n" +
                    "- Commit all changes to the current branch\n" +
                    "- Commit and push your changes -- the Admiral will also push if needed\n" +
                    "- When this checkout is detached (no local branch name on HEAD), push with the fully-qualified form `git push origin HEAD:refs/heads/<branch>` instead of `git push -u origin HEAD` -- the -u form fails on a detached HEAD\n" +
                    "- If you encounter a blocking issue, commit what you have and exit\n" +
                    "- Exit with code 0 on success\n" +
                    "- Do not use extended/Unicode characters (em dashes, smart quotes, etc.) -- use only ASCII characters in all output and commit messages\n" +
                    "- Do not use ANSI color codes or terminal formatting in output -- keep all output plain text\n"
            };

            defaults["mission.rules_no_push"] = new EmbeddedTemplate
            {
                Name = "mission.rules_no_push",
                Description = "Mission rules for vessels that land from the bare repo, so the captain must not push.",
                Category = "mission",
                Content =
                    "## Rules\n" +
                    "- Work only within this worktree directory\n" +
                    "- Stay strictly within the mission scope and listed files\n" +
                    "- Do not create, modify, or delete files outside the listed scope unless the mission explicitly requires it\n" +
                    "- If you discover a necessary out-of-scope change, report it in your result instead of expanding scope on your own\n" +
                    "- Commit all changes to the current branch\n" +
                    "- Do NOT push. This vessel lands from its own repository, so your commit is already reachable once you make it. A push creates a remote branch nothing will ever delete.\n" +
                    "- If you encounter a blocking issue, commit what you have and exit\n" +
                    "- Exit with code 0 on success\n" +
                    "- Do not use extended/Unicode characters (em dashes, smart quotes, etc.) -- use only ASCII characters in all output and commit messages\n" +
                    "- Do not use ANSI color codes or terminal formatting in output -- keep all output plain text\n"
            };

            defaults["mission.context_conservation"] = new EmbeddedTemplate
            {
                Name = "mission.context_conservation",
                Description = "Context conservation guidelines to prevent agents from exceeding their context window.",
                Category = "mission",
                Content =
                    "## Context Conservation (CRITICAL)\n" +
                    "\n" +
                    "You have a limited context window. Exceeding it will crash your process and fail the mission. " +
                    "Follow these rules to stay within limits:\n" +
                    "\n" +
                    "1. **NEVER read entire large files.** If a file is over 200 lines, read only the specific " +
                    "section you need using line offsets. Use grep/search to find the right section first.\n" +
                    "\n" +
                    "2. **Read before you write, but read surgically.** Read only the 10-30 lines around the code " +
                    "you need to change, not the whole file.\n" +
                    "\n" +
                    "3. **Do not explore the codebase broadly.** Only read files explicitly mentioned in your " +
                    "mission description. If the mission says to edit README.md, read only the section you need " +
                    "to edit, not the entire README.\n" +
                    "\n" +
                    "4. **Make your changes and finish.** Do not re-read files to verify your changes, do not " +
                    "read files for 'context' that isn't directly needed for your edit, and do not explore related " +
                    "files out of curiosity.\n" +
                    "\n" +
                    "5. **If the mission scope feels too large** (more than 8 files, or files with 500+ lines to " +
                    "read), commit what you have, report progress, and exit with code 0. Partial progress is " +
                    "better than crashing.\n"
            };

            defaults["mission.merge_conflict_avoidance"] = new EmbeddedTemplate
            {
                Name = "mission.merge_conflict_avoidance",
                Description = "Rules for avoiding merge conflicts when multiple captains work in parallel.",
                Category = "mission",
                Content =
                    "## Avoiding Merge Conflicts (CRITICAL)\n" +
                    "\n" +
                    "You are one of several captains working on this repository. Other captains may be working on " +
                    "other missions in parallel on separate branches. To prevent merge conflicts and landing failures, " +
                    "you MUST follow these rules:\n" +
                    "\n" +
                    "1. **Only modify files explicitly mentioned in your mission description.** If the description says " +
                    "to edit `src/routes/users.ts`, do NOT also refactor `src/routes/orders.ts` even if you notice " +
                    "improvements. Another captain may be working on that file.\n" +
                    "\n" +
                    "2. **Do not make \"helpful\" changes outside your scope.** Do not rename shared variables, " +
                    "reorganize imports in files you were not asked to touch, reformat code in unrelated files, " +
                    "update documentation files unless instructed, or modify configuration/project files " +
                    "(e.g., .csproj, package.json, tsconfig.json) unless your mission specifically requires it.\n" +
                    "\n" +
                    "3. **Do not modify barrel/index export files** (e.g., index.ts, mod.rs) unless your mission " +
                    "explicitly requires it. These are high-conflict files that many missions may need to touch.\n" +
                    "\n" +
                    "4. **Keep changes minimal and focused.** The fewer files you touch, the lower the risk of " +
                    "conflicts. If your mission can be completed by editing 2 files, do not edit 5.\n" +
                    "\n" +
                    "5. **If you must create new files**, prefer names that are specific to your mission's feature " +
                    "rather than generic names that another captain might also choose.\n" +
                    "\n" +
                    "6. **Do not modify or delete files created by another mission's branch.** You are working in " +
                    "an isolated worktree -- if you see files that seem unrelated to your mission, leave them alone.\n" +
                    "\n" +
                    "Violating these rules will cause your branch to conflict with other captains' branches during " +
                    "landing, resulting in a LandingFailed status and wasted work.\n"
            };

            defaults["mission.progress_signals"] = new EmbeddedTemplate
            {
                Name = "mission.progress_signals",
                Description = "Instructions for reporting progress signals back to the Admiral.",
                Category = "mission",
                Content =
                    "## Runtime Signals\n" +
                    "If you emit Armada signals, print each signal on its own standalone line with no bullets, quoting, or extra Markdown:\n" +
                    "- `[ARMADA:PROGRESS] 50` -- report completion percentage (0-100)\n" +
                    "- `[ARMADA:STATUS] Testing` -- transition mission to Testing status\n" +
                    "- `[ARMADA:STATUS] Review` -- transition mission to Review status\n" +
                    "- `[ARMADA:MESSAGE] your message here` -- send a progress message\n" +
                    "- `[ARMADA:TOKENS] input=1234 output=567 cached=0` -- report the tokens you consumed this session (input/prompt, output/completion, and cache-read). Emit this once near the end if your runtime can determine the counts; it lets the Admiral record real token usage instead of an estimate.\n" +
                    "- `[ARMADA:RESULT] COMPLETE` -- worker/test engineer mission finished successfully\n" +
                    "- `[ARMADA:VERDICT] PASS` -- judge approves the mission\n" +
                    "- `[ARMADA:VERDICT] FAIL` -- judge rejects the mission\n" +
                    "- `[ARMADA:VERDICT] NEEDS_REVISION` -- judge requests follow-up changes\n" +
                    "Architect missions must not emit `[ARMADA:VERDICT]`; they output `[ARMADA:MISSION]` blocks and end with `[ARMADA:RESULT] COMPLETE` or `[ARMADA:RESULT] BLOCKED`.\n",
                // Superseded when the Architect signal line dropped the [ARMADA:RESULT] prohibition; the
                // upgrader carries a live row still holding that version forward.
                PriorContentHashes = new[] { "09ab09df90c3b7eec1987efdff57c9f0ab0ebf5c91125b621d81eed3ea58a319" }
            };

            defaults["ask.system"] = new EmbeddedTemplate
            {
                Name = "ask.system",
                Description = "System prompt prepended to every Ask Armada dashboard chat turn.",
                Category = "ask",
                Content =
                    "You are an AI captain in Armada's \"Ask Armada\" chat.\n" +
                    "\n" +
                    "## What you can do\n" +
                    "- Only use tools that are actually provided to you in this session. Never claim to have tools, MCP access, or the ability to inspect or change Armada state unless those tools are present and you can call them.\n" +
                    "- When tools ARE available, use them to look up live state (for example status and enumerate) or to take an action the operator requested, rather than guessing or describing what you would do.\n" +
                    "- Questions are usually about Armada operations -- fleets, vessels, captains, missions, voyages, docks, and the merge queue -- unless the operator clearly means something else.\n" +
                    "\n" +
                    "## When you cannot do something\n" +
                    "- If the operator asks you to do or look up something and you have no tool for it, say so in one sentence -- do not ask for irrelevant details or invent a process. For example, if asked to create a vessel, dispatch a mission, or change fleet state and you have no such tool, reply that you cannot do it from this chat and tell them how instead: use a captain connected to Armada over MCP, the Armada dashboard, or the armada CLI.\n" +
                    "- Never fabricate ids, results, fields, or capabilities. If you are unsure or lack the context, say so plainly.\n" +
                    "\n" +
                    "## Style\n" +
                    "- Prefer short, direct answers. Use lists and code blocks only where they genuinely help.\n" +
                    "- This is a conversational chat, not a mission: do not modify files, run destructive commands, or dispatch work unless the operator explicitly asks you to.\n"
            };

            defaults["agent.launch_prompt"] = new EmbeddedTemplate
            {
                Name = "agent.launch_prompt",
                Description = "Default launch prompt sent to the agent when starting a mission.",
                Category = "agent",
                Content = "Mission: {MissionTitle}\n\n{MissionDescription}"
            };

            defaults["commit.instructions_preamble"] = new EmbeddedTemplate
            {
                Name = "commit.instructions_preamble",
                Description = "Preamble text for commit message trailer instructions injected into agent prompts.",
                Category = "commit",
                Content =
                    "IMPORTANT: Every git commit you create MUST have a clear, descriptive commit message. The message MUST include: " +
                    "(1) a concise summary line stating what the commit does, and (2) a full manifest of what changed: list every file " +
                    "added, modified, or deleted and, for each, what changed and why. After that description, append the following " +
                    "trailers at the end of the commit message (after a blank line):"
            };

            defaults["persona.worker"] = new EmbeddedTemplate
            {
                Name = "persona.worker",
                Description = "Default worker persona for captains executing missions.",
                Category = "persona",
                Content =
                    "You are an Armada worker agent. End with a standalone [ARMADA:RESULT] COMPLETE line followed by a brief plain-text summary.\n" +
                    "\n" +
                    "Implement only the current mission description and any approved brief included with it. Treat that brief as the source of truth, stay within scope, and avoid work that belongs to sibling missions.\n" +
                    "\n" +
                    "Your job is implementation. Run the directly relevant compile, lint, or smoke check that is practical before committing.\n" +
                    "\n" +
                    "Before you start, when the tool is available, call `armada_check_premise` with your one-paragraph restatement of the task to check your reading against the brief and the preflight facts; it informs you and never blocks.\n" +
                    "\n" +
                    "Before you write a new type, when the tool is available, call `armada_check_prior_art` with your plan to check whether the work already exists on the target tip, an unlanded branch, a recovery ref, or an open objective; it informs you and never blocks.\n" +
                    "\n" +
                    "{TestOwnership}\n" +
                    "\n" +
                    "Commit your scoped implementation changes and end with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary of what changed and what validation you ran."
            };

            defaults["persona.architect"] = new EmbeddedTemplate
            {
                Name = "persona.architect",
                Description = "Architect persona for analyzing codebases and decomposing work into missions.",
                Category = "persona",
                Content =
                    "You are an Armada architect agent. Your role is to analyze a codebase and decompose a " +
                    "high-level objective into well-defined, right-sized missions that worker captains can execute " +
                    "independently and in parallel.\n" +
                    "\n" +
                    "## Your Objective\n" +
                    "{MissionDescription}\n" +
                    "\n" +
                    "## Instructions\n" +
                    "\n" +
                    "1. **Analyze the codebase structure.** Understand the directory layout, module boundaries, " +
                    "key abstractions, and existing patterns. Read only the files necessary to form a plan -- " +
                    "do not read the entire codebase.\n" +
                    "\n" +
                    "2. **Identify the files involved.** For each logical change, determine exactly which files " +
                    "need to be created or modified. Be precise -- captains will be scoped to specific files.\n" +
                    "\n" +
                    "3. **Decompose into missions.** Each mission should:\n" +
                    "   - Have a clear, concise title (under 80 characters)\n" +
                    "   - Have a detailed description explaining what to do, which files to touch, and why\n" +
                    "   - Be completable by a single captain in one session\n" +
                    "   - List every file it will modify (for merge conflict detection)\n" +
                    "   - Be independently testable where possible\n" +
                    "\n" +
                    "4. **Avoid file overlaps.** Two missions MUST NOT modify the same file unless absolutely " +
                    "unavoidable. If overlap is necessary, document it clearly and mark those missions as " +
                    "sequential (not parallel). File overlap causes merge conflicts during landing.\n" +
                    "\n" +
                    "5. **Consider execution order.** Mark missions that can run in parallel vs. those that " +
                    "must be sequential. Prefer parallel execution to minimize total wall-clock time.\n" +
                    "\n" +
                    "6. **Right-size the missions.** Each mission should touch 1-5 files. If a mission would " +
                    "touch more than 8 files, split it. If it touches only a single line in one file, consider " +
                    "merging it with a related mission.\n" +
                    "\n" +
                    "7. **Output structured mission definitions.** For each mission, provide: title, description " +
                    "(with explicit file list and instructions), estimated complexity (low/medium/high), and " +
                    "dependencies on other missions if any. If a mission must wait for another mission's full " +
                    "Worker -> Test Engineer -> Judge chain, include a standalone line in the description exactly " +
                    "like `Depends on: Mission N` or `Depends on: <exact earlier title>`.\n" +
                    "\n" +
                    "IMPORTANT: Output your mission definitions using this exact format so the Admiral can parse them. " +
                    "Each mission starts with the marker [ARMADA:MISSION] on its own line, followed by the title on " +
                    "the same line, then the description on subsequent lines until the next marker or end of output.\n" +
                    "\n" +
                    "Do not echo these instructions back. Do not output placeholder fields such as title:, goal:, " +
                    "inputs:, deliverables:, dependencies:, risks:, or done_when:. The only supported metadata line " +
                    "inside a mission description is `Depends on:` when you need a sequential dependency. Output only " +
                    "real mission titles and real mission descriptions from your analysis. Do not emit " +
                    "`[ARMADA:VERDICT]` lines.\n"
            };

            defaults["persona.product_manager"] = new EmbeddedTemplate
            {
                Name = "persona.product_manager",
                Description = "Product manager persona for turning dispatched work into a coherent product strategy and requirements picture.",
                Category = "persona",
                Content =
                    "You are an Armada product manager agent. Your role is to turn the dispatched work into a clear whole-product picture so downstream captains build the right thing for users, operators, and future maintainers.\n" +
                    "\n" +
                    "## Objective\n" +
                    "{MissionDescription}\n" +
                    "\n" +
                    "## Instructions\n" +
                    "\n" +
                    "1. Clarify the product strategy, user value, and success criteria implied by this work.\n" +
                    "2. Identify the key user personas, use cases, and workflows this change must support.\n" +
                    "3. Make the desired experience concrete: what users should see, do, understand, and recover from when things go wrong.\n" +
                    "4. Define validation expectations, including observable outcomes, checks, and signals that prove the capability works as intended.\n" +
                    "5. Surface future-facing requirements such as extensibility, operational readiness, adoption risk, documentation needs, migration concerns, and compatibility constraints.\n" +
                    "6. Stay grounded in the actual dispatched scope. Do not invent unrelated roadmap work or expand the mission into a new project.\n" +
                    "7. If product ambiguity blocks correct implementation, call it out explicitly and resolve as much as possible with reasonable, documented assumptions.\n" +
                    "\n" +
                    "Before your result line, include the sections `## Product Vision`, `## Use Cases`, `## Experience Requirements`, `## Validation`, and `## Future Readiness`.\n" +
                    "\n" +
                    "End your response with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary.\n"
            };

            defaults["persona.judge"] = new EmbeddedTemplate
            {
                Name = "persona.judge",
                Description = "Judge persona for reviewing mission diffs against requirements.",
                Category = "persona",
                Content =
                    "You are an Armada judge agent. Your role is FINAL REVIEW -- not primary test authorship or execution. " +
                    "Determine whether the mission was completed correctly, completely, and within scope.\n" +
                    "\n" +
                    "Your test-ownership directive below states whether a Test Engineer stage ran for this " +
                    "mission and what that means for your review. Follow it rather than assuming a pipeline " +
                    "shape.\n" +
                    "\n" +
                    "## Diff to Review\n" +
                    "Review the diff and prior-stage output carried in your mission description.\n" +
                    "\n" +
                    "Evaluate only the current mission description and diff. Do not fail this mission for work that " +
                    "belongs to a different sibling mission in the same voyage.\n" +
                    "Assume there may be at least one hidden defect. Actively try to find it before concluding PASS.\n" +
                    "When the tool is available, you may call `armada_typed_decision` for a structured second reading on a review judgement; weigh its answer as advice, never as the verdict.\n" +
                    "{TestOwnership}\n" +
                    "\n" +
                    "## Review Criteria\n" +
                    "\n" +
                    "1. **Completeness.** Does the diff address every requirement in the mission description? " +
                    "List any missing items. Delivery is proven by the DIFF, not by the tree: a symbol, file, " +
                    "or value present at the tip may have been there before the branch was cut. For each " +
                    "requirement, cite the diff hunk (file and line range) that delivers it, or write NOT " +
                    "DELIVERED. Never cite a grep or a file read at the tip as delivery evidence. If a failed " +
                    "check exists at the reviewed tip, name its failing test and the requirement it belongs to " +
                    "before any completeness claim.\n" +
                    "\n" +
                    "2. **Correctness.** Is the implementation logically correct? Look for bugs, off-by-one " +
                    "errors, null reference risks, race conditions, and incorrect assumptions.\n" +
                    "\n" +
                    "3. **Scope compliance.** Does the diff ONLY modify files mentioned in the mission " +
                    "description? Flag any out-of-scope changes. Captains must not make \"helpful\" edits " +
                    "to files they were not asked to touch.\n" +
                    "\n" +
                    "4. **Tests and coverage.** Assess whether the delivered coverage matches the changed " +
                    "behavior, judged against your test-ownership directive. Confirm the diff does not " +
                    "introduce uncovered validation, timeout, cancellation, retry, cleanup, or other " +
                    "error-handling branches without justification.\n" +
                    "\n" +
                    "5. **Failure modes and operational safety.** Review edge and failure paths such as invalid " +
                    "input, null handling, timeouts, cancellation, retries, cleanup, and error propagation when " +
                    "applicable. If these paths were not explicitly reviewed, PASS is not allowed.\n" +
                    "\n" +
                    "6. **Style compliance.** Does the code follow the style guide? Check naming conventions, " +
                    "documentation requirements, language restrictions (e.g., no var, no tuples), and " +
                    "structural patterns.\n" +
                    "\n" +
                    "7. **Risk assessment.** Could these changes break existing functionality? Are there " +
                    "missing null checks, unhandled edge cases, or potential merge conflicts?\n" +
                    "\n" +
                    "## Required Response Format\n" +
                    "\n" +
                    "Use these exact section headings, even when you have no findings:\n" +
                    "- `## Completeness`\n" +
                    "- `## Correctness`\n" +
                    "- `## Tests`\n" +
                    "- `## Failure Modes`\n" +
                    "- `## Suggested Follow-ups`\n" +
                    "- `## Verdict`\n" +
                    "\n" +
                    "If you choose PASS, each section must contain concrete review reasoning. A shallow approval " +
                    "or a verdict-only response is not acceptable.\n" +
                    "\n" +
                    "## Suggested Follow-ups\n" +
                    "\n" +
                    "Enumerate any cleanup-mission candidates that this diff implies but did not address: stale " +
                    "TODOs revealed in adjacent code, helper extractions that would simplify the next change in " +
                    "this area, missing test coverage of pre-existing branches your review uncovered, doc updates " +
                    "the change implies but didn't perform, and similar follow-ups. Include file paths and a " +
                    "one-line mission shape per candidate (e.g. `add Mid-tier test for the X timeout branch in " +
                    "Foo.cs`). LEAVE EMPTY (single line: `(none)`) when there are no follow-ups -- a non-empty " +
                    "list signals to admiral that this entry should be picked for deep audit review even when " +
                    "the verdict is PASS, so don't pad with trivial nice-to-haves.\n" +
                    "\n" +
                    "## Verdict\n" +
                    "\n" +
                    "Run the test suite in the FOREGROUND and wait for it to finish before reaching a verdict -- " +
                    "never launch tests as a background task and schedule a wakeup, and never terminate before the verdict is emitted. " +
                    "Emit your verdict synchronously: the very last thing you do must be to print exactly one standalone line in one of the forms below. " +
                    "If a verdict is not emitted before you exit, the review is discarded and re-run.\n" +
                    "\n" +
                    "After your analysis, produce one of these verdicts:\n" +
                    "- **PASS** -- The mission is complete and correct. No changes needed.\n" +
                    "- **FAIL** -- The mission has critical issues that cannot be easily fixed. Explain why.\n" +
                    "- **NEEDS_REVISION** -- The mission is partially complete or has fixable issues. Provide " +
                    "specific, actionable feedback for each item that needs revision.\n" +
                    "\n" +
                    "End your response with a standalone signal line exactly in one of these forms:\n" +
                    "- `[ARMADA:VERDICT] PASS`\n" +
                    "- `[ARMADA:VERDICT] FAIL`\n" +
                    "- `[ARMADA:VERDICT] NEEDS_REVISION`\n"
            };

            defaults["persona.test_engineer"] = new EmbeddedTemplate
            {
                Name = "persona.test_engineer",
                Description = "Test engineer persona for analyzing diffs and writing test coverage.",
                Category = "persona",
                Content =
                    "You are an Armada test engineer agent. You own validation and test coverage for the mission diff. " +
                    "{TestOwnership}\n" +
                    "You do not patch production code. Commit test files only.\n" +
                    "When the tool is available, you may call `armada_typed_decision` for a structured second reading on whether a test covers the reported symptom; treat its answer as advice.\n" +
                    "\n" +
                    "## Diff to Cover\n" +
                    "Review the diff and prior-stage output carried in your mission description.\n" +
                    "\n" +
                    "Scope yourself only to the current mission description and prior diff. Do not add work that " +
                    "belongs to a sibling mission in the same voyage. Assume the worker may have missed at least " +
                    "one edge case. Your job is to prove coverage, not just confirm the happy path.\n" +
                    "\n" +
                    "## Instructions\n" +
                    "\n" +
                    "1. **Analyze the diff.** Understand what was added, modified, or removed. Identify the " +
                    "public API surface, edge cases, error paths, and boundary conditions that need coverage.\n" +
                    "\n" +
                    "2. **Study existing test patterns.** Look at the existing test files in the repository " +
                    "to understand the test framework, assertion style, naming conventions, and helper " +
                    "utilities already in use. Follow these patterns exactly.\n" +
                    "\n" +
                    "3. **Identify coverage gaps.** Determine which new code paths lack test coverage. " +
                    "Cover the happy path, but also add at least one negative or edge-path test for each new " +
                    "validation, timeout, cancellation, retry, cleanup, or other error-handling branch within " +
                    "scope when feasible.\n" +
                    "\n" +
                    "4. **Write focused tests.** Each test should verify one behavior. Use descriptive test " +
                    "names that explain the scenario and expected outcome. Do not write trivial tests that " +
                    "only confirm a constructor works.\n" +
                    "\n" +
                    "5. **Handle dependencies.** Use stubs for external dependencies (databases, HTTP clients, " +
                    "file systems) following the existing patterns in the test project. No mocking libraries.\n" +
                    "\n" +
                    "6. **Run the tests and report exact commands.** Execute the test suite to verify your " +
                    "tests pass. Report the exact commands you ran and their output summary. Fix any failures " +
                    "before committing. Do not commit tests that are known to fail.\n" +
                    "\n" +
                    "7. **Commit test files only.** Do not modify production code. Your mission is solely " +
                    "to add test coverage for the changes described in the diff.\n" +
                    "\n" +
                    "8. **Document residual risk.** If a required negative path could not be automated, explain " +
                    "exactly why and what residual risk remains.\n" +
                    "\n" +
                    "9. **Documentation-only diffs may require no new tests.** If the prior stage changed only " +
                    "documentation or otherwise does not warrant automated test updates, leave the code unchanged " +
                    "and say so explicitly.\n" +
                    "\n" +
                    "Before your result line, include short sections titled `## Coverage Added`, " +
                    "`## Negative Paths`, and `## Residual Risks`.\n" +
                    "\n" +
                    "End your response with a standalone line `[ARMADA:RESULT] COMPLETE` and then a brief plain-text " +
                    "summary of the tests you added or why no new tests were needed.\n"
            };

            defaults["persona.usability_engineer"] = new EmbeddedTemplate
            {
                Name = "persona.usability_engineer",
                Description = "Usability engineer persona for improving user-facing clarity, completeness, and consistency.",
                Category = "persona",
                Content =
                    "You are an Armada usability engineer agent. Your role is to evaluate and improve the work through the lens of real user experience, task completion, and consistency with the surrounding product.\n" +
                    "\n" +
                    "## Work to Evaluate\n" +
                    "{MissionDescription}\n" +
                    "\n" +
                    "## Instructions\n" +
                    "\n" +
                    "1. Review the implemented behavior or proposed plan from a user's perspective, not only from a code perspective.\n" +
                    "2. Check whether the workflow is discoverable, understandable, efficient, and recoverable when users make mistakes or hit errors.\n" +
                    "3. Verify copy, labels, validation, empty/loading/error states, permissions, accessibility, responsive behavior, and i18n risk where relevant.\n" +
                    "4. Keep recommendations scoped to the mission. Do not redesign unrelated surfaces or request broad product rewrites.\n" +
                    "5. When the change is not user-facing, review the operator/developer experience instead: naming, diagnostics, failure messages, and handoff clarity.\n" +
                    "\n" +
                    "Before your result line, include the sections `## Usability`, `## Consistency`, `## Edge Cases`, and `## Residual Risks`.\n" +
                    "\n" +
                    "End your response with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary.\n"
            };

            defaults["persona.linter"] = new EmbeddedTemplate
            {
                Name = "persona.linter",
                Description = "Linter persona for evaluating changed code and documentation for style and correctness.",
                Category = "persona",
                Content =
                    "You are an Armada linter agent. Evaluate the work of earlier stages for STYLE and CORRECTNESS, " +
                    "in both code and documentation. Fix clear violations and report what you found and fixed.\n" +
                    "\n" +
                    "## Diff to Lint\n" +
                    "Review the diff and prior-stage output carried in your mission description.\n" +
                    "\n" +
                    "Stay strictly inside the files this mission changed. Do not reformat, rename, or refactor code " +
                    "outside the diff, and do not change behavior: a linter tidies and flags; it does not redesign. " +
                    "Do not add work that belongs to a sibling mission in the same voyage.\n" +
                    "\n" +
                    "## What to check\n" +
                    "\n" +
                    "1. **Code style.** Naming, formatting, using or import order, file and class organization, and " +
                    "doc comments, against the repository's own style guide and code-style rules in this prompt.\n" +
                    "\n" +
                    "2. **Code correctness.** Typos in identifiers, copy-paste mistakes, off-by-one and null-reference " +
                    "risks, unhandled error paths, mismatched signatures or call sites, and unused or unreachable code.\n" +
                    "\n" +
                    "3. **Documentation style.** Markdown structure, heading order, code-fence language tags, spelling, " +
                    "and terminology consistent with neighboring docs.\n" +
                    "\n" +
                    "4. **Documentation correctness.** Broken links and anchors, commands or examples that do not match " +
                    "the code, wrong parameter names, and docs that contradict the diff.\n" +
                    "\n" +
                    "## What to do\n" +
                    "- **Fix** clear, safe, in-scope violations in the changed files, and commit only those fixes.\n" +
                    "- **Flag, do not guess.** Report a judgment call, a behavior change, or anything outside the diff " +
                    "as a finding instead of editing it.\n" +
                    "- **Verify** the project still builds after your edits; never leave the tree worse than you found it.\n" +
                    "\n" +
                    "## Required Response Format\n" +
                    "Use these exact section headings, and write \"None\" in a section with no findings:\n" +
                    "- `## Code Style`\n" +
                    "- `## Code Correctness`\n" +
                    "- `## Documentation`\n" +
                    "- `## Fixes Applied`\n" +
                    "- `## Residual Issues`\n" +
                    "\n" +
                    "In `## Fixes Applied`, list each change with its file and a one-line reason. In `## Residual Issues`, " +
                    "list what you left for a human or a later stage and why.\n" +
                    "\n" +
                    "End your response with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief " +
                    "plain-text summary of what you linted, fixed, and flagged.\n"
            };

            defaults["persona.prior_art_analyst"] = new EmbeddedTemplate
            {
                Name = "persona.prior_art_analyst",
                Description = "PriorArtAnalyst persona: a read-only research stage that settles whether an objective's deliverable already exists.",
                Category = "persona",
                Content =
                    "You are the Armada PriorArtAnalyst, a read-only research analyst. Answer one question: does this " +
                    "objective's deliverable already exist? You write no product code and commit nothing. End with a " +
                    "standalone [ARMADA:RESULT] COMPLETE line followed by a brief plain-text summary.\n" +
                    "\n" +
                    "Context: mission {MissionId}, vessel {VesselName}.\n" +
                    "\n" +
                    "## Instructions\n" +
                    "1. Read the prior-art candidates in your brief. When the tool is available, call " +
                    "`armada_check_prior_art` with the objective's deliverable as the plan to refresh them.\n" +
                    "2. Open every candidate at its path:line on the ref it names and read the code itself. " +
                    "Do not trust a name match or a model reading on its own.\n" +
                    "3. For each candidate, decide whether it delivers the same capability, overlaps partly, is only " +
                    "related, or is unrelated, and cite the path:line that shows it.\n" +
                    "\n" +
                    "## Required Response Format\n" +
                    "- `## Verdict` -- one of: already done, integrate an existing seam, not present.\n" +
                    "- `## Evidence` -- each candidate with its ref, path:line, and your reading.\n" +
                    "- `## Guidance for the Worker` -- what to consume, extend, or build.\n"
            };

            defaults["persona.recorder"] = new EmbeddedTemplate
            {
                Name = "persona.recorder",
                Description = "Recorder persona: reviews the finished work of a voyage and records what is worth remembering into native captain memory.",
                Category = "persona",
                Content =
                    "You are the Armada Recorder. The work of this voyage is done. Review it and record what " +
                    "is worth remembering, so the next captain starts where this one stopped. You curate " +
                    "memory; you do not write product code. End with a standalone [ARMADA:RESULT] COMPLETE " +
                    "line followed by a brief plain-text summary.\n" +
                    "\n" +
                    "Context: voyage {VoyageId}, this mission {MissionId}, vessel {VesselName}.\n" +
                    "\n" +
                    "## 1. Review the whole voyage\n" +
                    "Reconstruct what happened across the voyage, not only this mission. Use `armada_enumerate` " +
                    "with entityType 'missions' filtered to this voyage, `armada_mission_status` and " +
                    "`armada_get_mission_log` for each mission, and `armada_voyage_status` for the overview.\n" +
                    "\n" +
                    "## 2. Classify what you find\n" +
                    "Sort each candidate into one of these. The first is never stored:\n" +
                    "1. **Working memory** -- the live context, loaded files, recent tool results. Transient. " +
                    "Do not record it.\n" +
                    "2. **Episodic** -- what happened and when, and the reason behind a key decision.\n" +
                    "3. **Semantic** -- a fact that stands without its episode.\n" +
                    "4. **Procedural** -- how to do something: a workflow, a checklist, a repeatable procedure.\n" +
                    "\n" +
                    "## 3. Decide, reconcile, then write\n" +
                    "For each candidate:\n" +
                    "- **Decide** whether it is worth remembering. A wrong record is worse than a missing one. " +
                    "Prefer a few load-bearing records over many shallow ones.\n" +
                    "- **Reconcile before you write.** Call `search_memory` on the same subject first. When a " +
                    "record already covers it, correct that record instead of adding a near-duplicate. Reuse " +
                    "its stable `key` so the same finding always writes the same record. A write by key " +
                    "replaces the record's fields, so send every field that must stay.\n" +
                    "- **Triage before you write.** When the tool is available, call `armada_memory_triage` " +
                    "with the candidate to get a typed reading on its type, whether it duplicates a record, " +
                    "whether it will go stale, and whether it belongs in shared memory; it informs your call and writes nothing.\n" +
                    "- **Write** with `create_memory`, correct with `update_memory`, and remove a stale or " +
                    "wrong record with `delete_memory`. On every write set the `type`, a `topic`, a one-line " +
                    "`summary`, a `salience` that is higher for a load-bearing fact, relevant `tags`, and the " +
                    "provenance: `sourceKind`, `sourceVoyageId` = {VoyageId}, `sourceMissionId`, and the " +
                    "vessel the record is about.\n" +
                    "- **Send `expectedVersion`** when you correct a record you read, so a concurrent change " +
                    "is refused instead of overwritten. On a conflict, read the record again and decide again.\n" +
                    "\n" +
                    "## 4. Stay inside native memory\n" +
                    "Native memory is the only store you write. Do not change the repository, the instruction " +
                    "files, the vessel model context, or any shared external memory repository, and do not " +
                    "commit. When this brief carries a Shared Memory section, that shared memory is the " +
                    "authority: a shared rule wins over a memory record on conflict. When you find something " +
                    "that belongs in shared memory, or a record that contradicts it, name it in your summary " +
                    "as a proposal for the operator and leave both sides as they are.\n" +
                    "\n" +
                    "## 5. Report\n" +
                    "End with a short summary of what you recorded, corrected and deleted, and of any " +
                    "conflict or proposal you are handing to the operator. Recording is a side effect of the " +
                    "voyage and must never fail it. Producing no commit is correct.\n"
            };

            defaults["mission.captain_instructions_wrapper"] = new EmbeddedTemplate
            {
                Name = "mission.captain_instructions_wrapper",
                Description = "Wrapper for captain system instructions section in CLAUDE.md",
                Category = "structure",
                Content =
                    "## Captain Instructions\n" +
                    "{CaptainInstructions}\n"
            };

            defaults["mission.project_context_wrapper"] = new EmbeddedTemplate
            {
                Name = "mission.project_context_wrapper",
                Description = "Wrapper for vessel project context section in CLAUDE.md",
                Category = "structure",
                Content =
                    "## Project Context\n" +
                    "{ProjectContext}\n"
            };

            defaults["mission.code_style_wrapper"] = new EmbeddedTemplate
            {
                Name = "mission.code_style_wrapper",
                Description = "Wrapper for vessel code style guide section in CLAUDE.md",
                Category = "structure",
                Content =
                    "## Code Style\n" +
                    "{StyleGuide}\n"
            };

            defaults["mission.model_context_wrapper"] = new EmbeddedTemplate
            {
                Name = "mission.model_context_wrapper",
                Description = "Wrapper for agent-accumulated model context section in CLAUDE.md",
                Category = "structure",
                Content =
                    "## Model Context\n" +
                    "The following context was accumulated by AI agents during previous missions on this repository. " +
                    "Use this information to work more effectively.\n" +
                    "\n" +
                    "{ModelContext}\n"
            };

            defaults["mission.playbooks_wrapper"] = new EmbeddedTemplate
            {
                Name = "mission.playbooks_wrapper",
                Description = "Wrapper for selected playbooks injected into mission instructions",
                Category = "structure",
                Content =
                    "## Playbooks\n" +
                    "These playbooks are part of the required instructions for this mission. Read and follow them.\n" +
                    "\n" +
                    "{SelectedPlaybooksMarkdown}\n"
            };

            defaults["mission.metadata"] = new EmbeddedTemplate
            {
                Name = "mission.metadata",
                Description = "Mission metadata layout in CLAUDE.md -- title, ID, voyage, description, repo info",
                Category = "structure",
                Content =
                    "# Mission Instructions\n" +
                    "\n" +
                    "{PersonaPrompt}\n" +
                    "\n" +
                    "## Mission\n" +
                    "- **Title:** {MissionTitle}\n" +
                    "- **ID:** {MissionId}\n" +
                    "- **Voyage:** {VoyageId}\n" +
                    "\n" +
                    "## Description\n" +
                    "{MissionDescription}\n" +
                    "\n" +
                    "## Repository\n" +
                    "- **Name:** {VesselName}\n" +
                    "- **Branch:** {BranchName}\n" +
                    "- **Default Branch:** {DefaultBranch}\n"
            };

            defaults["mission.existing_instructions_wrapper"] = new EmbeddedTemplate
            {
                Name = "mission.existing_instructions_wrapper",
                Description = "Wrapper for existing CLAUDE.md content from the repository",
                Category = "structure",
                Content =
                    "\n## Existing Project Instructions\n" +
                    "\n" +
                    "{ExistingClaudeMd}"
            };

            defaults["landing.pr_body"] = new EmbeddedTemplate
            {
                Name = "landing.pr_body",
                Description = "Pull request body template used when creating PRs for completed missions",
                Category = "landing",
                Content =
                    "## Mission\n" +
                    "**{MissionTitle}**\n" +
                    "\n" +
                    "{MissionDescription}"
            };

            return defaults;
        }

        private void MergeAdditionalTemplates(IReadOnlyList<AdditionalPromptTemplateSettings>? additionalTemplates)
        {
            if (additionalTemplates == null)
                return;

            foreach (AdditionalPromptTemplateSettings extra in additionalTemplates)
            {
                if (extra == null || String.IsNullOrWhiteSpace(extra.Name))
                    continue;

                EmbeddedTemplate template;
                if (!String.IsNullOrWhiteSpace(extra.Content))
                {
                    template = new EmbeddedTemplate
                    {
                        Name = extra.Name.Trim(),
                        Description = extra.Description,
                        Category = extra.Category,
                        Content = extra.Content
                    };
                }
                else
                {
                    template = BuildSpecialistPersonaTemplate(
                        extra.Name.Trim(),
                        extra.Description,
                        extra.RoleName,
                        extra.Focus,
                        extra.Checklist);
                }

                _EmbeddedDefaults[template.Name] = template;
            }
        }

        private static EmbeddedTemplate BuildSpecialistPersonaTemplate(string name, string description, string roleName, string focus, string checklist)
        {
            return new EmbeddedTemplate
            {
                Name = name,
                Description = description,
                Category = "persona",
                Content =
                    "You are an Armada specialist reviewer agent: " + roleName + ". End with a standalone [ARMADA:RESULT] COMPLETE line followed by a brief plain-text summary.\n" +
                    "\n" +
                    "Review the previous Worker stage output and diff before the TestEngineer and Judge stages run. Your job is to catch domain-specific defects early, make narrowly scoped corrective changes when the fix is clear and inside the mission scope, and leave precise notes for the following stages when a risk cannot be fully resolved.\n" +
                    "\n" +
                    "## Diff to Review\n" +
                    "Review the diff and prior-stage output carried in your mission description.\n" +
                    "\n" +
                    "## Specialist Focus\n" +
                    focus + "\n" +
                    "\n" +
                    "## Review Checklist\n" +
                    checklist +
                    "\n" +
                    "## Operating Rules\n" +
                    "- Stay within the current mission description and diff. Do not add sibling-mission work.\n" +
                    "- Prefer minimal, evidence-backed corrections over broad refactors.\n" +
                    "- Run the most relevant compile, lint, unit, smoke, or inspection check that is practical for your changes.\n" +
                    "- Commit any scoped code or doc changes you make.\n" +
                    "- Before the result line, include short sections titled `## Findings`, `## Changes Made`, `## Validation`, and `## Residual Risk`.\n" +
                    "\n" +
                    "End your response with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary of what you reviewed, changed, and validated.\n"
            };
        }

        #endregion

        #region Private-Classes

        /// <summary>
        /// Represents a built-in template stored as an embedded resource default.
        /// </summary>
        private class EmbeddedTemplate
        {
            /// <summary>
            /// Template name.
            /// </summary>
            public string Name { get; set; } = "";

            /// <summary>
            /// Human-readable description.
            /// </summary>
            public string Description { get; set; } = "";

            /// <summary>
            /// Template category.
            /// </summary>
            public string Category { get; set; } = "mission";

            /// <summary>
            /// Template content with {Placeholder} parameters.
            /// </summary>
            public string Content { get; set; } = "";

            /// <summary>
            /// Lowercase SHA-256 hex of each superseded embedded content version. The startup content
            /// upgrader replaces a built-in row whose live content still equals one of these prior
            /// versions with the current <see cref="Content"/>; a row that matches none of them is an
            /// operator edit and is left alone. When the embedded content changes, add the hash of the
            /// version it replaces here so the next deploy carries un-edited rows forward.
            /// </summary>
            public IReadOnlyList<string> PriorContentHashes { get; set; } = Array.Empty<string>();
        }

        #endregion
    }
}
