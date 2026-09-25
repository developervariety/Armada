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
        /// Bind every stored pipelines column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the pipelines table.</param>
        /// <param name="pipeline">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Pipeline pipeline)
        {
            parameters
                .Text("id", pipeline.Id)
                .Text("tenant_id", pipeline.TenantId)
                .Text("user_id", pipeline.UserId)
                .Text("name", pipeline.Name)
                .Text("description", pipeline.Description)
                .Bool("is_built_in", pipeline.IsBuiltIn)
                .Bool("active", pipeline.Active)
                .Utc("created_utc", pipeline.CreatedUtc)
                .Utc("last_update_utc", pipeline.LastUpdateUtc)
                .Text("ownership_scope", pipeline.OwnershipScope.ToString());
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

        /// <summary>
        /// Bind every stored pipeline_stages column a stage row holds, each in the form its provider stores it. The
        /// stage's submitted position is bound by the statement that writes it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the pipeline_stages table.</param>
        /// <param name="stage">Stage to bind.</param>
        internal static void WriteStage(StoredParameters parameters, PipelineStage stage)
        {
            parameters
                .Text("id", stage.Id)
                .Text("pipeline_id", stage.PipelineId)
                .Int("stage_order", stage.Order)
                .Text("persona_name", stage.PersonaName)
                .Bool("is_optional", stage.IsOptional)
                .Text("description", stage.Description)
                .Text("preferred_model", stage.PreferredModel)
                .Bool("requires_review", stage.RequiresReview)
                .Text("review_deny_action", stage.ReviewDenyAction.ToString());
        }
    }
}
