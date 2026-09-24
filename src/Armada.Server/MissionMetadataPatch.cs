namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>
    /// The metadata fields a mission update request named, with their values. A field the request did not name is
    /// left unchanged; a field it named is written, and an empty dependency or parent clears the link. REST, WebSocket
    /// and MCP all build one of these, so every surface updates only what the request carried.
    /// </summary>
    public sealed class MissionMetadataPatch
    {
        #region Public-Members

        /// <summary>Title, when named.</summary>
        public string? Title { get; set; }

        /// <summary>Description, when named.</summary>
        public string? Description { get; set; }

        /// <summary>Priority, when named.</summary>
        public int? Priority { get; set; }

        /// <summary>Branch name, when named.</summary>
        public string? BranchName { get; set; }

        /// <summary>Pull request URL, when named.</summary>
        public string? PrUrl { get; set; }

        /// <summary>Parent mission id, when named; empty clears it.</summary>
        public string? ParentMissionId { get; set; }

        /// <summary>Dependency mission id, when named; empty clears it.</summary>
        public string? DependsOnMissionId { get; set; }

        /// <summary>Persona, when named.</summary>
        public string? Persona { get; set; }

        /// <summary>Whether the request named a vessel id.</summary>
        public bool HasVesselId { get; set; }

        /// <summary>Requested vessel id.</summary>
        public string? VesselId { get; set; }

        /// <summary>Whether the request named a voyage id.</summary>
        public bool HasVoyageId { get; set; }

        /// <summary>Requested voyage id.</summary>
        public string? VoyageId { get; set; }

        /// <summary>The field names the request carried, case-insensitive.</summary>
        public HashSet<string> Named { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Public-Methods

        /// <summary>Whether the request named a field.</summary>
        /// <param name="field">Field name.</param>
        /// <returns>True when named.</returns>
        public bool Has(string field) => Named.Contains(field);

        /// <summary>
        /// Build a patch from a deserialized mission body and the top-level field names the body carried.
        /// </summary>
        /// <param name="incoming">Deserialized body.</param>
        /// <param name="named">Top-level field names of the body.</param>
        /// <returns>The patch.</returns>
        public static MissionMetadataPatch FromBody(Mission incoming, IEnumerable<string> named)
        {
            if (incoming == null) throw new ArgumentNullException(nameof(incoming));
            MissionMetadataPatch patch = new MissionMetadataPatch
            {
                Title = incoming.Title,
                Description = incoming.Description,
                Priority = incoming.Priority,
                BranchName = incoming.BranchName,
                PrUrl = incoming.PrUrl,
                ParentMissionId = incoming.ParentMissionId,
                DependsOnMissionId = incoming.DependsOnMissionId,
                Persona = incoming.Persona,
                VesselId = incoming.VesselId,
                VoyageId = incoming.VoyageId
            };
            foreach (string name in named ?? Array.Empty<string>()) patch.Named.Add(name);
            patch.HasVesselId = patch.Has("vesselId");
            patch.HasVoyageId = patch.Has("voyageId");
            return patch;
        }

        /// <summary>
        /// The top-level field names of a JSON object body; an empty set when the body is not an object.
        /// </summary>
        /// <param name="json">JSON text.</param>
        /// <returns>Field names.</returns>
        public static HashSet<string> ReadFieldNames(string? json)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(json)) return names;
            try
            {
                Dictionary<string, JsonElement>? fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (fields != null)
                {
                    foreach (string name in fields.Keys) names.Add(name);
                }
            }
            catch (JsonException)
            {
                // A body that is not a JSON object names no fields; the typed deserialization reports it.
            }
            return names;
        }

        #endregion
    }
}
