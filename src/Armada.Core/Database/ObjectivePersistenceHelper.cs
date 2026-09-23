namespace Armada.Core.Database
{
    using System.Data.Common;
    using System.Text.Json;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Shared JSON and enum helpers for normalized objective persistence.
    /// </summary>
    /// <remarks>
    /// A stored objective value that cannot be read raises <see cref="StoredObjectiveDataException"/>
    /// naming the row and the field. It is never read as an empty list or a default enum value: the
    /// next update would write that value back, and an unreadable blocker list read as empty lets
    /// the scheduler dispatch a blocked objective. Null or empty stored values read as empty.
    /// </remarks>
    internal static class ObjectivePersistenceHelper
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        internal static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, _JsonOptions);
        }

        internal static List<string> DeserializeList(object? value, string objectiveId, string field)
        {
            string? json = value?.ToString();
            if (String.IsNullOrWhiteSpace(json))
                return new List<string>();

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json, _JsonOptions) ?? new List<string>();
            }
            catch (JsonException ex)
            {
                throw new StoredObjectiveDataException(objectiveId, field, "is not a valid JSON string list", ex);
            }
        }

        internal static List<SelectedPlaybook> DeserializePlaybooks(object? value, string objectiveId, string field)
        {
            string? json = value?.ToString();
            if (String.IsNullOrWhiteSpace(json))
                return new List<SelectedPlaybook>();

            try
            {
                return JsonSerializer.Deserialize<List<SelectedPlaybook>>(json, _JsonOptions) ?? new List<SelectedPlaybook>();
            }
            catch (JsonException ex)
            {
                throw new StoredObjectiveDataException(objectiveId, field, "is not a valid JSON playbook list", ex);
            }
        }

        internal static ObjectivePreparation DeserializePreparation(object? value, string objectiveId, string field)
        {
            string? json = value?.ToString();
            if (String.IsNullOrWhiteSpace(json))
                return new ObjectivePreparation();

            try
            {
                ObjectivePreparation preparation = JsonSerializer.Deserialize<ObjectivePreparation>(json, _JsonOptions)
                    ?? new ObjectivePreparation();
                preparation.RequiredClaimKinds ??= new List<Armada.Core.Enums.ObjectivePreparationClaimKindEnum>();
                preparation.RequiredSiblingInputs ??= new List<ObjectivePreparationSiblingInput>();
                foreach (ObjectivePreparationSiblingInput? sibling in preparation.RequiredSiblingInputs)
                {
                    if (sibling != null) sibling.RequiredArtifactPaths ??= new List<string>();
                }
                preparation.Claims ??= new List<ObjectivePreparationClaim>();
                foreach (ObjectivePreparationClaim? claim in preparation.Claims)
                {
                    if (claim != null) claim.EvidenceLinks ??= new List<string>();
                }
                preparation.Preflight ??= new ObjectivePreflight();
                preparation.Preflight.Questions ??= new List<ObjectivePreflightAnswer>();
                return preparation;
            }
            catch (JsonException ex)
            {
                throw new StoredObjectiveDataException(objectiveId, field, "is not a valid JSON preparation document", ex);
            }
        }

        /// <summary>
        /// Read a stored objective enum column. Null or empty reads as <paramref name="whenEmpty"/>;
        /// any other value must name a defined member.
        /// </summary>
        internal static TEnum ParseObjectiveEnum<TEnum>(object? value, TEnum whenEmpty, string objectiveId, string field) where TEnum : struct, Enum
        {
            string? raw = value?.ToString();
            if (String.IsNullOrWhiteSpace(raw))
                return whenEmpty;

            if (Enum.TryParse<TEnum>(raw, true, out TEnum parsed) && Enum.IsDefined(parsed))
                return parsed;

            throw new StoredObjectiveDataException(objectiveId, field, "holds '" + raw + "', which is not a " + typeof(TEnum).Name + " value");
        }

        /// <summary>
        /// Lenient enum read for objective refinement-session rows: an unknown value reads as the fallback.
        /// </summary>
        internal static TEnum ParseEnum<TEnum>(object? value, TEnum fallback) where TEnum : struct
        {
            string? raw = value?.ToString();
            if (String.IsNullOrWhiteSpace(raw))
                return fallback;

            return Enum.TryParse<TEnum>(raw, true, out TEnum parsed) ? parsed : fallback;
        }

        /// <summary>
        /// Read every objective row from <paramref name="reader"/>. A row whose stored data cannot be read
        /// is left out of the list and logged as a warning that counts the skipped rows and names each
        /// objective and field, so one unreadable row neither fails the whole list nor reads with its
        /// data replaced by empty values. A single-row read raises the error instead.
        /// </summary>
        internal static async Task<List<Objective>> ReadRowsAsync(
            DbDataReader reader,
            Func<Objective> decodeCurrentRow,
            LoggingModule logging,
            CancellationToken token)
        {
            List<Objective> results = new List<Objective>();
            List<StoredObjectiveDataException> skipped = new List<StoredObjectiveDataException>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                try
                {
                    results.Add(decodeCurrentRow());
                }
                catch (StoredObjectiveDataException ex)
                {
                    skipped.Add(ex);
                }
            }

            if (skipped.Count > 0)
            {
                string names = String.Join("; ", skipped.Select(ex => ex.ObjectiveId + " (" + ex.Field + ")"));
                string message = "[Objectives] skipped " + skipped.Count + " objective row(s) whose stored data cannot be read; "
                    + "the other " + results.Count + " row(s) are returned. Repair: " + names;
                logging.Warn(message);
            }

            return results;
        }
    }
}
