namespace Armada.Server.Mcp
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Result of a captain read that the evidence scope refuses. <see cref="Error"/> keeps the plain
    /// not-found text; <see cref="Reason"/> says why. A record of another owner, an absent record and a
    /// purged record all read as <see cref="NotFound"/>, so a denial never reveals another owner's record.
    /// </summary>
    public sealed class McpScopeDenial
    {
        #region Public-Members

        /// <summary>Reason when no record with the id is visible to the caller's owner.</summary>
        public const string NotFound = "not_found";

        /// <summary>Reason when the caller's owner holds the record but no link reaches it from the calling mission.</summary>
        public const string OutOfScope = "out_of_scope";

        /// <summary>Plain error text, such as "Mission not found".</summary>
        public string Error { get; set; } = "";

        /// <summary>Reason when an abbreviated id names more than one record the caller can see.</summary>
        public const string Ambiguous = Armada.Core.Authorization.AbbreviatedIdResolver.AmbiguousReason;

        /// <summary><see cref="NotFound"/>, <see cref="OutOfScope"/> or <see cref="Ambiguous"/>.</summary>
        public string Reason { get; set; } = NotFound;

        /// <summary>What the caller can do about the denial.</summary>
        public string Detail { get; set; } = "";

        /// <summary>Full ids of the visible records an ambiguous abbreviation names; omitted for any other reason.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Candidates { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a denial.</summary>
        /// <param name="error">Plain error text.</param>
        /// <param name="outOfScope">True when the caller's owner holds the record.</param>
        public static McpScopeDenial Create(string error, bool outOfScope)
        {
            return new McpScopeDenial
            {
                Error = error,
                Reason = outOfScope ? OutOfScope : NotFound,
                Detail = outOfScope
                    ? "The record exists but nothing links it to this mission. A captain reads its own report chain and direct dependencies, the objectives that authorize its mission with their ancestors, blockers and children, the records those objectives cite by id, and the missions and voyages those objectives list."
                    : "No record with this id is visible to this caller."
            };
        }

        /// <summary>Create the denial for an abbreviated id that names more than one visible record.</summary>
        /// <param name="error">Plain error text.</param>
        /// <param name="candidates">Full ids of the visible records the abbreviation names.</param>
        public static McpScopeDenial CreateAmbiguous(string error, List<string> candidates)
        {
            return new McpScopeDenial
            {
                Error = error,
                Reason = Ambiguous,
                Candidates = candidates,
                Detail = "The abbreviated id matches more than one record visible to this caller. Pass the full id."
            };
        }

        #endregion
    }
}
