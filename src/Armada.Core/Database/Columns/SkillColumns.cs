namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The skills row-to-model contract, shared by every provider.
    /// </summary>
    internal static class SkillColumns
    {
        /// <summary>
        /// Read a skills row.
        /// </summary>
        /// <param name="record">Reader positioned on a skills row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The skill.</returns>
        internal static Skill Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Skill");
            return new Skill
            {
                Id = row.Text("id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                Name = row.Text("name"),
                Description = row.NullableText("description"),
                Category = row.NullableText("category"),
                Content = row.NullableText("content") ?? String.Empty,
                IsBuiltIn = row.Bool("is_built_in"),
                Active = row.Bool("active"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }

        /// <summary>
        /// Bind every stored skills column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the skills table.</param>
        /// <param name="skill">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Skill skill)
        {
            parameters
                .Text("id", skill.Id)
                .Text("tenant_id", skill.TenantId)
                .Text("user_id", skill.UserId)
                .Text("name", skill.Name)
                .Text("description", skill.Description)
                .Text("category", skill.Category)
                .Text("content", skill.Content ?? String.Empty)
                .Bool("is_built_in", skill.IsBuiltIn)
                .Bool("active", skill.Active)
                .Utc("created_utc", skill.CreatedUtc)
                .Utc("last_update_utc", skill.LastUpdateUtc);
        }
    }
}
