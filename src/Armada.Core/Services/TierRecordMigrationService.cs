namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// The one-time, idempotent startup step that moves the retired model tier settings onto records, so an
    /// upgrade keeps selecting the same captains. The retired <c>midTierModels</c>, <c>highTierModels</c> and
    /// <c>familyClassificationRules</c> become each captain's pinned tier (high is Premium, mid is Standard,
    /// a model none of them classified is Economy); the retired <c>withinTierPreferenceOrder</c> becomes each
    /// captain's preference rank (the first listed model ranks highest, unlisted models rank 0); and the
    /// retired <c>specialistPersonas</c> become a Premium minimum on matching persona records, except
    /// Test Engineer, which gets a Standard minimum. It writes a
    /// copy of the settings file first, removes the retired keys, stamps
    /// <see cref="ModelTierSettings.TierRecordsMigratedUtc"/>, and logs every record it changed. A settings
    /// file that already carries the stamp is never migrated again; retired keys it still holds are ignored.
    /// </summary>
    public sealed class TierRecordMigrationService
    {
        #region Public-Members

        /// <summary>Suffix inserted before the timestamp of the settings backup file name.</summary>
        public const string BackupInfix = ".pre-tier-migration-";

        #endregion

        #region Private-Members

        private readonly string _Header = "[TierRecordMigrationService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public TierRecordMigrationService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Plan the record changes that reproduce the retired tier settings. Pure: nothing is written.
        /// </summary>
        /// <param name="settings">Settings carrying the retired tier keys.</param>
        /// <param name="captains">Every captain record.</param>
        /// <param name="personas">Every persona record.</param>
        /// <returns>The planned changes, with outcome <see cref="TierRecordMigrationResult.OutcomePlanned"/>.</returns>
        public static TierRecordMigrationResult Plan(ModelTierSettings settings, IReadOnlyList<Captain> captains, IReadOnlyList<Persona> personas)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (captains == null) throw new ArgumentNullException(nameof(captains));
            if (personas == null) throw new ArgumentNullException(nameof(personas));
            TierRecordMigrationResult result = new TierRecordMigrationResult { Outcome = TierRecordMigrationResult.OutcomePlanned };

            List<string> high = settings.RetiredHighTierModels ?? new List<string>();
            List<string> mid = settings.RetiredMidTierModels ?? new List<string>();
            List<ModelFamilyClassificationRule> rules = settings.RetiredFamilyClassificationRules ?? new List<ModelFamilyClassificationRule>();
            bool membershipConfigured = high.Count > 0 || mid.Count > 0 || rules.Count > 0;
            bool ranked = String.Equals(settings.RetiredWithinTierStrategy, ModelTierSettings.WithinTierStrategyPreferenceOrderThenRandom, StringComparison.OrdinalIgnoreCase)
                && settings.RetiredWithinTierPreferenceOrder != null;

            foreach (Captain captain in captains)
            {
                if (captain == null) continue;
                string? retiredTier = membershipConfigured ? ClassifyRetired(captain.Model, high, mid, rules) : null;

                CaptainTierEnum? tier = captain.Tier;
                if (membershipConfigured)
                {
                    CaptainTierEnum mapped = retiredTier == PreferredModelTierSelector.HighTier ? CaptainTierEnum.Premium
                        : retiredTier == PreferredModelTierSelector.MidTier ? CaptainTierEnum.Standard
                        : CaptainTierEnum.Economy;
                    if (CaptainTierSelector.EffectiveTier(captain) != mapped) tier = mapped;
                }

                int rank = captain.PreferenceRank;
                if (ranked && retiredTier != null)
                {
                    List<string>? order = FindOrder(settings.RetiredWithinTierPreferenceOrder!, retiredTier);
                    int index = order == null ? -1 : IndexOfModel(order, captain.Model);
                    rank = index < 0 ? 0 : Math.Min(1000, CountListed(order!) - index);
                }

                if (tier != captain.Tier || rank != captain.PreferenceRank)
                {
                    result.Captains.Add(new CaptainTierMigrationChange
                    {
                        CaptainId = captain.Id,
                        Name = captain.Name,
                        Model = captain.Model,
                        RetiredTier = retiredTier,
                        PreviousTier = captain.Tier,
                        Tier = tier,
                        PreviousRank = captain.PreferenceRank,
                        Rank = rank
                    });
                }
            }

            HashSet<string> matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> retiredMinimumTierPersonas = (settings.RetiredSpecialistPersonas ?? new List<string>())
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Select(name => PersonaCatalog.NormalizeName(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (Persona persona in personas)
            {
                if (persona == null) continue;
                string name = PersonaCatalog.NormalizeName(persona.Name);
                if (!retiredMinimumTierPersonas.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                matched.Add(name);
                CaptainTierEnum minimumTier = MinimumTierForPersona(persona.Name);
                if (persona.MinimumTier == minimumTier) continue;
                result.Personas.Add(new PersonaMinimumTierMigrationChange { PersonaId = persona.Id, Name = persona.Name, TenantId = persona.TenantId });
            }
            result.UnmatchedSpecialistPersonas = retiredMinimumTierPersonas.Where(name => !matched.Contains(name)).ToList();
            return result;
        }

        /// <summary>Map a retired specialist persona to its replacement minimum tier.</summary>
        public static CaptainTierEnum MinimumTierForPersona(string? persona)
        {
            return PersonaCatalog.Matches(persona, PersonaCatalog.TestEngineer)
                ? CaptainTierEnum.Standard : CaptainTierEnum.Premium;
        }

        /// <summary>
        /// Run the migration once: when the settings carry retired tier keys and no migration stamp, apply the
        /// planned record changes, copy the settings file, remove the retired keys, stamp the settings and save
        /// them. Running it again changes nothing.
        /// </summary>
        /// <param name="settings">The live settings.</param>
        /// <param name="settingsPath">Path of the settings file the settings were loaded from.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What the migration found and changed.</returns>
        public async Task<TierRecordMigrationResult> RunAsync(ArmadaSettings settings, string settingsPath, CancellationToken token = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (String.IsNullOrWhiteSpace(settingsPath)) throw new ArgumentNullException(nameof(settingsPath));
            ModelTierSettings tiers = settings.ModelTier;

            if (tiers.TierRecordsMigratedUtc.HasValue)
            {
                if (tiers.HasRetiredTierKeys)
                    _Logging.Warn(_Header + "settings still carry retired model tier keys; they were migrated at " + tiers.TierRecordsMigratedUtc.Value.ToString("o") + " and are ignored");
                return new TierRecordMigrationResult { Outcome = TierRecordMigrationResult.OutcomeAlreadyMigrated };
            }

            if (!tiers.HasRetiredTierKeys)
                return new TierRecordMigrationResult { Outcome = TierRecordMigrationResult.OutcomeNothingToMigrate };

            TierRecordMigrationResult result = await ApplyAsync(tiers, token).ConfigureAwait(false);

            if (File.Exists(settingsPath))
            {
                string backup = settingsPath + BackupInfix + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + ".json";
                File.Copy(settingsPath, backup, false);
                result.SettingsBackupPath = backup;
                _Logging.Info(_Header + "settings backup written to " + backup);
            }

            tiers.ClearRetiredTierKeys();
            tiers.TierRecordsMigratedUtc = DateTime.UtcNow;
            await settings.SaveAsync(settingsPath).ConfigureAwait(false);
            result.Outcome = TierRecordMigrationResult.OutcomeMigrated;
            _Logging.Info(_Header + "moved retired model tier settings onto records: " + result.Captains.Count + " captain(s), "
                + result.Personas.Count + " persona(s); retired keys removed from settings");
            return result;
        }

        /// <summary>
        /// Plan and apply the record changes that reproduce the retired tier settings, without touching the
        /// settings or the settings file. Applying the same settings again changes nothing.
        /// </summary>
        /// <param name="retired">Settings carrying the retired tier keys.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The applied changes.</returns>
        public async Task<TierRecordMigrationResult> ApplyAsync(ModelTierSettings retired, CancellationToken token = default)
        {
            if (retired == null) throw new ArgumentNullException(nameof(retired));
            List<Captain> captains = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            List<Persona> personas = await _Database.Personas.EnumerateAsync(token).ConfigureAwait(false);
            TierRecordMigrationResult result = Plan(retired, captains, personas);

            foreach (CaptainTierMigrationChange change in result.Captains)
            {
                Captain? captain = captains.FirstOrDefault(c => String.Equals(c.Id, change.CaptainId, StringComparison.Ordinal));
                if (captain == null) continue;
                captain.Tier = change.Tier;
                captain.PreferenceRank = change.Rank;
                await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                _Logging.Info(_Header + "captain " + change.CaptainId + " (" + change.Name + ", model " + (change.Model ?? "(runtime default)")
                    + ", retired tier " + (change.RetiredTier ?? "none") + "): tier " + TierText(change.PreviousTier) + " -> " + TierText(change.Tier)
                    + ", preference rank " + change.PreviousRank + " -> " + change.Rank);
            }

            foreach (PersonaMinimumTierMigrationChange change in result.Personas)
            {
                Persona? persona = personas.FirstOrDefault(p => String.Equals(p.Id, change.PersonaId, StringComparison.Ordinal));
                if (persona == null) continue;
                persona.MinimumTier = MinimumTierForPersona(persona.Name);
                persona.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Personas.UpdateAsync(persona, token).ConfigureAwait(false);
                _Logging.Info(_Header + "persona " + change.PersonaId + " (" + change.Name + ") minimum tier set to " + persona.MinimumTier);
            }

            foreach (string name in result.UnmatchedSpecialistPersonas)
                _Logging.Warn(_Header + "retired specialist persona " + name + " matches no persona record; create the persona and set its minimum tier");

            return result;
        }

        #endregion

        #region Private-Methods

        // The retired classifier: explicit high list, then mid list, then the first matching family rule.
        private static string? ClassifyRetired(string? model, List<string> high, List<string> mid, List<ModelFamilyClassificationRule> rules)
        {
            if (String.IsNullOrWhiteSpace(model)) return null;
            string trimmed = model.Trim();
            if (IndexOfModel(high, trimmed) >= 0) return PreferredModelTierSelector.HighTier;
            if (IndexOfModel(mid, trimmed) >= 0) return PreferredModelTierSelector.MidTier;
            foreach (ModelFamilyClassificationRule rule in rules)
            {
                if (rule == null || String.IsNullOrWhiteSpace(rule.Pattern) || String.IsNullOrWhiteSpace(rule.Tier)) continue;
                bool matches;
                try
                {
                    matches = Regex.IsMatch(trimmed, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException)
                {
                    matches = false;
                }
                if (!matches) continue;
                if (String.Equals(rule.Tier, PreferredModelTierSelector.HighTier, StringComparison.OrdinalIgnoreCase)) return PreferredModelTierSelector.HighTier;
                if (String.Equals(rule.Tier, PreferredModelTierSelector.MidTier, StringComparison.OrdinalIgnoreCase)
                    || String.Equals(rule.Tier, PreferredModelTierSelector.LowTier, StringComparison.OrdinalIgnoreCase))
                    return PreferredModelTierSelector.MidTier;
            }
            return null;
        }

        private static List<string>? FindOrder(Dictionary<string, List<string>> orders, string tier)
        {
            foreach (KeyValuePair<string, List<string>> entry in orders)
            {
                if (String.Equals(entry.Key, tier, StringComparison.OrdinalIgnoreCase)) return entry.Value;
            }
            return null;
        }

        // Position of the model among the non-blank listed models, so blank entries do not shift ranks.
        private static int IndexOfModel(List<string> models, string? model)
        {
            if (String.IsNullOrWhiteSpace(model)) return -1;
            int position = 0;
            foreach (string listed in models)
            {
                if (String.IsNullOrWhiteSpace(listed)) continue;
                if (String.Equals(listed.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase)) return position;
                position++;
            }
            return -1;
        }

        private static int CountListed(List<string> models)
        {
            return models.Count(m => !String.IsNullOrWhiteSpace(m));
        }

        private static string TierText(CaptainTierEnum? tier)
        {
            return tier.HasValue ? tier.Value.ToString() : "Auto";
        }

        #endregion
    }
}
