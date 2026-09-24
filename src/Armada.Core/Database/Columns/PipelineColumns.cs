namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The pipelines and pipeline_stages row-to-model contracts, shared by every provider. The ownership scope
    /// follows the shared ownership rule; a review-deny action outside the model reads as retrying the stage.
    /// Stages live in their own table and are attached by the method set.
    /// </summary>
    internal static class PipelineColumns
    {
        /// <summary>
        /// Read a pipelines row.
        /// </summary>
        /// <param name="record">Reader positioned on a pipelines row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The pipeline, without its stages.</returns>
        internal static Pipeline Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Pipeline");
            Pipeline pipeline = new Pipeline();
            pipeline.Id = row.Text("id");
            pipeline.TenantId = row.NullableText("tenant_id");
            pipeline.UserId = row.NullableText("user_id");
            pipeline.OwnershipScope = OwnershipColumns.ParseScope(row.TextOrNull("ownership_scope"));
            pipeline.Name = row.Text("name");
            pipeline.Description = row.NullableText("description");
            pipeline.IsBuiltIn = row.Bool("is_built_in");
            pipeline.Active = row.Bool("active");
            pipeline.CreatedUtc = row.Utc("created_utc");
            pipeline.LastUpdateUtc = row.Utc("last_update_utc");
            return pipeline;
        }

        /// <summary>
        /// Read a pipeline_stages row.
        /// </summary>
        /// <param name="record">Reader positioned on a pipeline_stages row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The pipeline stage.</returns>
        internal static PipelineStage ReadStage(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "PipelineStage");
            PipelineStage stage = new PipelineStage();
            stage.Id = row.Text("id");
            stage.PipelineId = row.NullableText("pipeline_id");
            stage.Order = row.Int("stage_order");
            stage.PersonaName = row.Text("persona_name");
            stage.IsOptional = row.Bool("is_optional");
            stage.Description = row.NullableText("description");
            stage.PreferredModel = row.NullableText("preferred_model");
            stage.RequiresReview = row.Bool("requires_review");
            stage.ReviewDenyAction = row.EnumOrFallback("review_deny_action", ReviewDenyActionEnum.RetryStage);
            return stage;
        }
    }
}
