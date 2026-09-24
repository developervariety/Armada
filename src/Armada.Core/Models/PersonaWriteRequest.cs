namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// The fields a caller may set when creating or updating a persona. Every surface deserializes its input
    /// into this one allow-list, so ownership, built-in status, identifiers and timestamps can never come from
    /// a request body. On update a field left out keeps its stored value.
    /// </summary>
    public class PersonaWriteRequest
    {
        #region Public-Members

        /// <summary>
        /// Code that starts the refusal of the retired specialist flag.
        /// </summary>
        public const string SpecialistRetiredErrorCode = "specialist_retired";

        /// <summary>
        /// Persona name. Required on create; an update is addressed by name and never renames.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// Description. Null leaves it unchanged on update; an empty string clears it.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Prompt template name. Required on create; null leaves it unchanged on update.
        /// </summary>
        public string? PromptTemplateName { get; set; } = null;

        /// <summary>
        /// Minimum capability tier. Omitted leaves it unchanged; an explicit null clears it.
        /// </summary>
        public CaptainTierEnum? MinimumTier
        {
            get => _MinimumTier;
            set
            {
                _MinimumTier = value;
                MinimumTierSupplied = true;
            }
        }

        /// <summary>
        /// True when the request named <see cref="MinimumTier"/>, including an explicit null.
        /// </summary>
        [JsonIgnore]
        public bool MinimumTierSupplied { get; private set; } = false;

        /// <summary>
        /// Default captain id. Omitted leaves it unchanged; null or empty clears it.
        /// </summary>
        public string? DefaultCaptainId
        {
            get => _DefaultCaptainId;
            set
            {
                _DefaultCaptainId = value;
                DefaultCaptainIdSupplied = true;
            }
        }

        /// <summary>
        /// True when the request named <see cref="DefaultCaptainId"/>, including an explicit null.
        /// </summary>
        [JsonIgnore]
        public bool DefaultCaptainIdSupplied { get; private set; } = false;

        /// <summary>
        /// Default playbooks merged for this persona, as an array or as the JSON text a stored persona carries.
        /// Null leaves them unchanged; an empty list clears them.
        /// </summary>
        [JsonConverter(typeof(SelectedPlaybookListJsonConverter))]
        public List<SelectedPlaybook>? DefaultPlaybooks { get; set; } = null;

        /// <summary>
        /// Whether the persona is active. Null leaves it unchanged (true for a new persona).
        /// </summary>
        public bool? Active { get; set; } = null;

        /// <summary>
        /// Requested ownership scope on create. An administrator's choice is kept (tenant-wide when omitted);
        /// any other caller's record is always user-specific. Ignored on update.
        /// </summary>
        public OwnershipScopeEnum? OwnershipScope { get; set; } = null;

        /// <summary>
        /// Retired specialist flag. Any value is refused with <see cref="SpecialistRetiredErrorCode"/>.
        /// </summary>
        public bool? Specialist { get; set; } = null;

        #endregion

        #region Private-Members

        private CaptainTierEnum? _MinimumTier = null;
        private string? _DefaultCaptainId = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// The refusal for a retired field the request names, or null.
        /// </summary>
        /// <returns>Refusal message, or null.</returns>
        public string? RetiredFieldError()
        {
            if (!Specialist.HasValue) return null;
            return SpecialistRetiredErrorCode + ": the specialist flag no longer changes routing. "
                + "Set minimumTier (Economy, Standard or Premium, or null for no floor) instead.";
        }

        #endregion
    }
}
