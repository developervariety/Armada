namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The prompt_templates row-to-model contract, shared by every provider. The ownership scope follows the shared
    /// ownership rule: null or blank reads as tenant-wide and an unknown scope is refused.
    /// </summary>
    internal static class PromptTemplateColumns
    {
        /// <summary>
        /// Read a prompt_templates row.
        /// </summary>
        /// <param name="record">Reader positioned on a prompt_templates row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The prompt template.</returns>
        internal static PromptTemplate Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "PromptTemplate");
            PromptTemplate template = new PromptTemplate();
            template.Id = row.Text("id");
            template.TenantId = row.NullableText("tenant_id");
            template.UserId = row.NullableText("user_id");
            template.OwnershipScope = OwnershipColumns.ParseScope(row.TextOrNull("ownership_scope"));
            template.Name = row.Text("name");
            template.Description = row.NullableText("description");
            template.Category = row.Text("category");
            template.Content = row.Text("content");
            template.IsBuiltIn = row.Bool("is_built_in");
            template.Active = row.Bool("active");
            template.CreatedUtc = row.Utc("created_utc");
            template.LastUpdateUtc = row.Utc("last_update_utc");
            return template;
        }

        /// <summary>
        /// Bind every stored prompt_templates column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the prompt_templates table.</param>
        /// <param name="template">Row to bind.</param>
        internal static void Write(StoredParameters parameters, PromptTemplate template)
        {
            parameters
                .Text("id", template.Id)
                .Text("tenant_id", template.TenantId)
                .Text("user_id", template.UserId)
                .Text("name", template.Name)
                .Text("description", template.Description)
                .Text("category", template.Category)
                .Text("content", template.Content)
                .Bool("is_built_in", template.IsBuiltIn)
                .Bool("active", template.Active)
                .Utc("created_utc", template.CreatedUtc)
                .Utc("last_update_utc", template.LastUpdateUtc)
                .Text("ownership_scope", template.OwnershipScope.ToString());
        }
    }
}
