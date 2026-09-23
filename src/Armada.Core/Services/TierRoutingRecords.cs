namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// An immutable snapshot of the routing facts that live on records: persona minimum tiers
    /// and the tier of every model the captain roster runs. Routing reads the snapshot held on
    /// <see cref="ModelTierSettings.Records"/>; <see cref="RefreshAsync"/> replaces it from the database.
    /// </summary>
    public sealed class TierRoutingRecords
    {
        #region Public-Members

        /// <summary>A snapshot with no persona minimum tiers and no known model.</summary>
        public static readonly TierRoutingRecords Empty = new TierRoutingRecords(new Dictionary<string, CaptainTierEnum>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, CaptainTierEnum>(StringComparer.OrdinalIgnoreCase));

        /// <summary>Compatibility property: canonical names of personas with a Premium minimum tier.</summary>
        public IReadOnlyCollection<string> SpecialistPersonas => _PersonaMinimumTiers
            .Where(item => item.Value == CaptainTierEnum.Premium).Select(item => item.Key).ToList();

        #endregion

        #region Private-Members

        private readonly Dictionary<string, CaptainTierEnum> _PersonaMinimumTiers;
        private readonly Dictionary<string, CaptainTierEnum> _ModelTiers;

        #endregion

        #region Constructors-and-Factories

        private TierRoutingRecords(Dictionary<string, CaptainTierEnum> personaMinimumTiers, Dictionary<string, CaptainTierEnum> modelTiers)
        {
            _PersonaMinimumTiers = personaMinimumTiers;
            _ModelTiers = modelTiers;
        }

        /// <summary>
        /// Build a snapshot from persona and captain records.
        /// </summary>
        /// <param name="personas">Persona records with optional minimum capability tiers.</param>
        /// <param name="captains">Captain records; each model takes the highest effective tier of the captains that run it.</param>
        /// <returns>The snapshot.</returns>
        public static TierRoutingRecords From(IEnumerable<Persona>? personas, IEnumerable<Captain>? captains)
        {
            Dictionary<string, CaptainTierEnum> personaMinimumTiers = new Dictionary<string, CaptainTierEnum>(StringComparer.OrdinalIgnoreCase);
            foreach (Persona persona in personas ?? Enumerable.Empty<Persona>())
            {
                if (persona == null || !persona.MinimumTier.HasValue) continue;
                string name = PersonaCatalog.NormalizeName(persona.Name);
                if (name.Length > 0) personaMinimumTiers[name] = persona.MinimumTier.Value;
            }

            Dictionary<string, CaptainTierEnum> modelTiers = new Dictionary<string, CaptainTierEnum>(StringComparer.OrdinalIgnoreCase);
            foreach (Captain captain in captains ?? Enumerable.Empty<Captain>())
            {
                if (captain == null || String.IsNullOrWhiteSpace(captain.Model)) continue;
                string model = captain.Model.Trim();
                CaptainTierEnum tier = CaptainTierSelector.EffectiveTier(captain);
                if (!modelTiers.TryGetValue(model, out CaptainTierEnum existing) || tier > existing)
                    modelTiers[model] = tier;
            }

            return new TierRoutingRecords(personaMinimumTiers, modelTiers);
        }

        /// <summary>
        /// Build a snapshot with explicit persona minimum tiers and no model facts.
        /// </summary>
        /// <param name="minimumTiers">Persona names and their minimum tiers.</param>
        /// <returns>The snapshot.</returns>
        public static TierRoutingRecords ForMinimumTiers(IEnumerable<KeyValuePair<string, CaptainTierEnum>>? minimumTiers)
        {
            Dictionary<string, CaptainTierEnum> personas = new Dictionary<string, CaptainTierEnum>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, CaptainTierEnum> entry in minimumTiers ?? Enumerable.Empty<KeyValuePair<string, CaptainTierEnum>>())
            {
                if (String.IsNullOrWhiteSpace(entry.Key)) continue;
                personas[PersonaCatalog.NormalizeName(entry.Key)] = entry.Value;
            }
            return new TierRoutingRecords(personas, new Dictionary<string, CaptainTierEnum>(StringComparer.OrdinalIgnoreCase));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read personas and captains and replace the snapshot on the settings.
        /// </summary>
        /// <param name="settings">Settings whose snapshot is replaced.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The new snapshot.</returns>
        public static async Task<TierRoutingRecords> RefreshAsync(ModelTierSettings settings, DatabaseDriver database, CancellationToken token = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (database == null) throw new ArgumentNullException(nameof(database));
            List<Persona> personas = await database.Personas.EnumerateAsync(token).ConfigureAwait(false);
            List<Captain> captains = await database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            TierRoutingRecords records = From(personas, captains);
            settings.Records = records;
            return records;
        }

        /// <summary>
        /// The configured minimum capability tier for the persona, if any.
        /// </summary>
        /// <param name="persona">Persona name.</param>
        /// <returns>The minimum tier, or null when the persona has no minimum.</returns>
        public CaptainTierEnum? MinimumTierForPersona(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return null;
            return _PersonaMinimumTiers.TryGetValue(PersonaCatalog.NormalizeName(persona), out CaptainTierEnum tier) ? tier : null;
        }

        /// <summary>Compatibility query for callers that mean a Premium minimum tier.</summary>
        public bool IsSpecialist(string? persona) => MinimumTierForPersona(persona) == CaptainTierEnum.Premium;

        /// <summary>
        /// The tier of a model the roster runs: the highest effective tier among the captains that run it.
        /// Null when no captain runs the model.
        /// </summary>
        /// <param name="model">Model identifier.</param>
        /// <returns>The tier, or null.</returns>
        public CaptainTierEnum? TierOfModel(string? model)
        {
            if (String.IsNullOrWhiteSpace(model)) return null;
            if (_ModelTiers.TryGetValue(model.Trim(), out CaptainTierEnum tier)) return tier;
            return null;
        }

        #endregion
    }
}
