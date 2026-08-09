namespace Armada.Core.Services
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Converts papercuts to and from events, and collapses repeated reports into groups.
    ///
    /// Papercuts are stored as events rather than as their own table. The event row already carries
    /// captain, mission, vessel, and voyage, which is every field a papercut needs for context, and it
    /// costs no schema change across the four supported databases.
    /// </summary>
    public static class PapercutService
    {
        #region Public-Members

        /// <summary>
        /// Entity type recorded on a papercut event.
        /// </summary>
        public const string EntityType = "papercut";

        /// <summary>
        /// Maximum number of sample missions retained per group.
        /// </summary>
        public const int MaxSampleMissions = 5;

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        // Grouping must survive the parts of a title that change between captains: generated
        // identifiers, line numbers, and sizes. Without this, the same complaint from nine captains
        // reads as nine unrelated reports, which is the exact signal the feature exists to produce.
        private static readonly Regex _IdPattern = new Regex(
            @"\b(?:flt|vsl|cpt|msn|vyg|dck|sig|art|evt|obj|inc|chk|rel|dep|rbk|rbx|mrg)_[A-Za-z0-9]+\b",
            RegexOptions.Compiled);

        private static readonly Regex _HexPattern = new Regex(@"\b[0-9a-f]{7,40}\b", RegexOptions.Compiled);
        private static readonly Regex _NumberPattern = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex _NonWordPattern = new Regex(@"[^a-z0-9#\s]", RegexOptions.Compiled);

        private const int MaxKeyWords = 12;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the event that stores a papercut.
        /// </summary>
        /// <param name="papercut">Papercut to store.</param>
        /// <returns>Event ready for persistence.</returns>
        public static ArmadaEvent ToEvent(Papercut papercut)
        {
            if (papercut == null) throw new ArgumentNullException(nameof(papercut));

            ArmadaEvent evt = new ArmadaEvent(
                PapercutParser.EventType,
                "[" + papercut.Category + "/" + papercut.Severity + "] " + papercut.Title);

            evt.EntityType = EntityType;
            evt.CaptainId = papercut.CaptainId;
            evt.MissionId = papercut.MissionId;
            evt.VesselId = papercut.VesselId;
            evt.VoyageId = papercut.VoyageId;
            evt.CreatedUtc = papercut.ReportedUtc;
            evt.Payload = JsonSerializer.Serialize(papercut, _JsonOptions);
            return evt;
        }

        /// <summary>
        /// Rebuild a papercut from its event. The event columns win over the stored payload: the
        /// admiral wrote those, the captain wrote the payload.
        /// </summary>
        /// <param name="evt">Event to read.</param>
        /// <returns>Papercut, or null when the event is not a usable papercut.</returns>
        public static Papercut? TryFromEvent(ArmadaEvent? evt)
        {
            if (evt == null) return null;
            if (!String.Equals(evt.EventType, PapercutParser.EventType, StringComparison.OrdinalIgnoreCase)) return null;
            if (String.IsNullOrWhiteSpace(evt.Payload)) return null;

            Papercut? papercut;
            try
            {
                papercut = JsonSerializer.Deserialize<Papercut>(evt.Payload!, _JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }

            if (papercut == null || String.IsNullOrWhiteSpace(papercut.Title)) return null;

            papercut.EventId = evt.Id;
            papercut.CaptainId = evt.CaptainId;
            papercut.MissionId = evt.MissionId;
            papercut.VesselId = evt.VesselId;
            papercut.VoyageId = evt.VoyageId;
            papercut.ReportedUtc = evt.CreatedUtc;
            return papercut;
        }

        /// <summary>
        /// Collapse papercuts into groups of the same vessel, category, and normalized title.
        /// Groups are returned by count, then by most recent report.
        /// </summary>
        /// <param name="papercuts">Papercuts to group.</param>
        /// <returns>Groups, largest first.</returns>
        public static List<PapercutGroup> Group(IEnumerable<Papercut>? papercuts)
        {
            List<PapercutGroup> groups = new List<PapercutGroup>();
            if (papercuts == null) return groups;

            Dictionary<string, PapercutGroup> byKey = new Dictionary<string, PapercutGroup>(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> captainsByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (Papercut papercut in papercuts)
            {
                if (papercut == null || String.IsNullOrWhiteSpace(papercut.Title)) continue;

                string key = BuildKey(papercut);

                PapercutGroup? group;
                if (!byKey.TryGetValue(key, out group))
                {
                    group = new PapercutGroup();
                    group.Key = key;
                    group.VesselId = papercut.VesselId;
                    group.Category = papercut.Category;
                    group.HighestSeverity = papercut.Severity;
                    group.SampleTitle = papercut.Title;
                    group.SampleDetail = papercut.Detail;
                    group.SamplePath = papercut.Path;
                    group.FirstSeenUtc = papercut.ReportedUtc;
                    group.LastSeenUtc = papercut.ReportedUtc;
                    byKey[key] = group;
                    captainsByKey[key] = new HashSet<string>(StringComparer.Ordinal);
                }

                group.Count++;
                if (papercut.Severity > group.HighestSeverity) group.HighestSeverity = papercut.Severity;
                if (papercut.ReportedUtc < group.FirstSeenUtc) group.FirstSeenUtc = papercut.ReportedUtc;

                // The newest report supplies the sample text: a stale first sighting is the least
                // useful sample when an operator reads the group months later.
                if (papercut.ReportedUtc >= group.LastSeenUtc)
                {
                    group.LastSeenUtc = papercut.ReportedUtc;
                    group.SampleTitle = papercut.Title;
                    if (!String.IsNullOrWhiteSpace(papercut.Detail)) group.SampleDetail = papercut.Detail;
                    if (!String.IsNullOrWhiteSpace(papercut.Path)) group.SamplePath = papercut.Path;
                }

                if (!String.IsNullOrEmpty(papercut.CaptainId)) captainsByKey[key].Add(papercut.CaptainId!);
                if (!String.IsNullOrEmpty(papercut.MissionId) &&
                    group.SampleMissionIds.Count < MaxSampleMissions &&
                    !group.SampleMissionIds.Contains(papercut.MissionId!))
                {
                    group.SampleMissionIds.Add(papercut.MissionId!);
                }
            }

            foreach (KeyValuePair<string, PapercutGroup> entry in byKey)
            {
                entry.Value.DistinctCaptainCount = captainsByKey[entry.Key].Count;
                groups.Add(entry.Value);
            }

            return groups
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.LastSeenUtc)
                .ToList();
        }

        /// <summary>
        /// Build the grouping key for a papercut.
        /// </summary>
        /// <param name="papercut">Papercut.</param>
        /// <returns>Grouping key.</returns>
        public static string BuildKey(Papercut papercut)
        {
            if (papercut == null) throw new ArgumentNullException(nameof(papercut));

            return (papercut.VesselId ?? "-") + "|" + papercut.Category + "|" + NormalizeTitle(papercut.Title);
        }

        /// <summary>
        /// Reduce a title to its stable words so that two reports of the same friction share a key.
        /// </summary>
        /// <param name="title">Reported title.</param>
        /// <returns>Normalized title.</returns>
        public static string NormalizeTitle(string? title)
        {
            if (String.IsNullOrWhiteSpace(title)) return "";

            string normalized = title!.ToLowerInvariant();
            normalized = _IdPattern.Replace(normalized, "#id");
            normalized = _HexPattern.Replace(normalized, "#sha");
            normalized = _NumberPattern.Replace(normalized, "#");
            normalized = _NonWordPattern.Replace(normalized, " ");
            normalized = Regex.Replace(normalized, "\\s+", " ").Trim();

            string[] words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= MaxKeyWords) return String.Join(" ", words);
            return String.Join(" ", words.Take(MaxKeyWords));
        }

        #endregion
    }
}
