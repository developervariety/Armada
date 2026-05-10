namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// A context-pack curation hint for a vessel: pre-selection rules that <see cref="ContextPackResponse"/>
    /// applies before lexical/semantic ranking. Hints are produced by Reflections v2-F1 pack-curate
    /// missions and applied when the dispatch goal text matches <see cref="GoalPattern"/>.
    /// </summary>
    public sealed class VesselPackHint
    {
        #region Public-Members

        /// <summary>Unique identifier (vph_ prefix).</summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>Owning vessel identifier.</summary>
        public string VesselId
        {
            get => _VesselId;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(VesselId));
                _VesselId = value;
            }
        }

        /// <summary>Regex applied to the dispatch goal text. Case-insensitive at evaluation time.</summary>
        public string GoalPattern
        {
            get => _GoalPattern;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(GoalPattern));
                _GoalPattern = value;
            }
        }

        /// <summary>JSON-serialized array of glob paths the pack must include when this hint matches.</summary>
        public string MustIncludeJson { get; set; } = "[]";

        /// <summary>JSON-serialized array of glob paths the pack must exclude when this hint matches.</summary>
        public string MustExcludeJson { get; set; } = "[]";

        /// <summary>Higher priority hints are applied first; equal-priority conflicts resolve to exclude-wins.</summary>
        public int Priority { get; set; } = 0;

        /// <summary>Confidence rating: high | medium | low.</summary>
        public string Confidence { get; set; } = "medium";

        /// <summary>JSON-serialized array of mission ids that produced this hint (traceability).</summary>
        public string SourceMissionIdsJson { get; set; } = "[]";

        /// <summary>Free-text rationale recorded by the consolidator.</summary>
        public string? Justification { get; set; } = null;

        /// <summary>Soft-disable flag. Inactive hints are not applied at pack time but are kept for audit.</summary>
        public bool Active { get; set; } = true;

        /// <summary>Creation timestamp in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Last update timestamp in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.VesselPackHintIdPrefix, 24);
        private string _VesselId = "";
        private string _GoalPattern = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        public VesselPackHint()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>Parse <see cref="MustIncludeJson"/> as a list of glob paths. Returns empty list on parse error.</summary>
        public List<string> GetMustInclude()
        {
            return DeserializeStringArray(MustIncludeJson);
        }

        /// <summary>Parse <see cref="MustExcludeJson"/> as a list of glob paths. Returns empty list on parse error.</summary>
        public List<string> GetMustExclude()
        {
            return DeserializeStringArray(MustExcludeJson);
        }

        /// <summary>Parse <see cref="SourceMissionIdsJson"/> as a list of mission ids. Returns empty list on parse error.</summary>
        public List<string> GetSourceMissionIds()
        {
            return DeserializeStringArray(SourceMissionIdsJson);
        }

        /// <summary>Serialize a list of glob paths into the canonical JSON string array form.</summary>
        public static string SerializeStringList(IReadOnlyList<string>? values)
        {
            if (values == null) return "[]";
            return JsonSerializer.Serialize(values);
        }

        #endregion

        #region Private-Methods

        private static List<string> DeserializeStringArray(string? json)
        {
            if (String.IsNullOrWhiteSpace(json)) return new List<string>();
            try
            {
                List<string>? list = JsonSerializer.Deserialize<List<string>>(json);
                return list ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        #endregion
    }
}
