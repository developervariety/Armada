namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The personas row-to-model contract, shared by every provider. The ownership scope follows the shared
    /// ownership rule; a minimum tier that is blank or not a tier name reads as no minimum.
    /// </summary>
    internal static class PersonaColumns
    {
        /// <summary>
        /// Read a personas row.
        /// </summary>
        /// <param name="record">Reader positioned on a personas row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The persona.</returns>
        internal static Persona Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Persona");
            Persona persona = new Persona();
            persona.Id = row.Text("id");
            persona.TenantId = row.NullableText("tenant_id");
            persona.UserId = row.NullableText("user_id");
            persona.OwnershipScope = OwnershipColumns.ParseScope(row.TextOrNull("ownership_scope"));
            persona.Name = row.Text("name");
            persona.Description = row.NullableText("description");
            persona.PromptTemplateName = row.Text("prompt_template_name");
            persona.DefaultCaptainId = row.NullableText("default_captain_id");
            persona.MinimumTier = row.EnumOrNull<CaptainTierEnum>(TierRoutingPersistence.MinimumTierColumn, ignoreCase: true);
            persona.IsBuiltIn = row.Bool("is_built_in");
            persona.DefaultPlaybooks = row.NullableText("default_playbooks");
            persona.Active = row.Bool("active");
            persona.CreatedUtc = row.Utc("created_utc");
            persona.LastUpdateUtc = row.Utc("last_update_utc");
            return persona;
        }
    }
}
