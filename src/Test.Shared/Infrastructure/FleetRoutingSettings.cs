namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using Armada.Core;
    using Armada.Core.Settings;

    /// <summary>
    /// Recreates the routing, guard, and specialist-asset values that were previously
    /// hardcoded in product code. Tests that assert today's deployment behavior pass
    /// this fixture; product defaults stay empty and policy-neutral.
    /// </summary>
    public static class FleetRoutingSettings
    {
        /// <summary>
        /// Former built-in specialist persona names.
        /// </summary>
        public static readonly string[] SpecialistPersonaNames = new string[]
        {
            "Judge",
            "Architect",
            "TestEngineer",
            "DiagnosticProtocolReviewer",
            "TenantSecurityReviewer",
            "MigrationDataReviewer",
            "PerformanceMemoryReviewer",
            "PortingReferenceAnalyst",
            "FrontendWorkflowReviewer"
        };

        /// <summary>
        /// Build the former hardcoded model-tier settings, including family rules and
        /// the non-native-first / preference-order policy.
        /// </summary>
        /// <returns>A ModelTierSettings instance that reproduces the previous code defaults.</returns>
        public static ModelTierSettings CreateModelTier()
        {
            return new ModelTierSettings
            {
                SpecialistPersonas = new List<string>(SpecialistPersonaNames),
                ReservedHighTierSlots = 1,
                PreferNonNativeFirst = true,
                WithinTierStrategy = ModelTierSettings.WithinTierStrategyPreferenceOrderThenRandom,
                MidTierModels = new List<string>
                {
                    "gpt-5.6-luna",
                    "opencode-go/deepseek-v4-flash",
                    "opencode-go/qwen3.8-max"
                },
                HighTierModels = new List<string>
                {
                    "claude-fable-5",
                    "gpt-5.6-sol",
                    "claude-opus-5"
                },
                WithinTierPreferenceOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "mid", new List<string>() },
                    { "high", new List<string> { "claude-fable-5" } }
                },
                ModelCapabilityProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    { "claude-fable-5", new ModelCapabilityProfile { TelemetryRichness = 96, AuditReasoningFit = 96, MechanicalThroughput = 55, Cost = 95 } },
                    { "claude-opus-5", new ModelCapabilityProfile { TelemetryRichness = 96, AuditReasoningFit = 96, MechanicalThroughput = 55, Cost = 95 } },
                    { "claude-opus-4-8", new ModelCapabilityProfile { TelemetryRichness = 95, AuditReasoningFit = 95, MechanicalThroughput = 55, Cost = 95 } },
                    { "gpt-5.6-sol", new ModelCapabilityProfile { TelemetryRichness = 96, AuditReasoningFit = 96, MechanicalThroughput = 55, Cost = 95 } },
                    { "gpt-5.6-luna", new ModelCapabilityProfile { TelemetryRichness = 75, AuditReasoningFit = 78, MechanicalThroughput = 70, Cost = 55 } },
                    { "opencode-go/deepseek-v4-flash", new ModelCapabilityProfile { TelemetryRichness = 30, AuditReasoningFit = 30, MechanicalThroughput = 75, Cost = 15 } },
                    { "opencode-go/qwen3.8-max", new ModelCapabilityProfile { TelemetryRichness = 60, AuditReasoningFit = 65, MechanicalThroughput = 68, Cost = 55 } }
                },
                FamilyClassificationRules = new List<ModelFamilyClassificationRule>
                {
                    new ModelFamilyClassificationRule(@"^claude-opus-\d+(?:-\d+)*$", "high"),
                    new ModelFamilyClassificationRule(@"^claude-(?:fable|mythos)-\d+(?:-\d+)*$", "high"),
                    new ModelFamilyClassificationRule(@"^(?:opencode(?:-go)?/)?kimi-k2\.7(?:[-.].*)?$", "mid"),
                    new ModelFamilyClassificationRule(@"^claude-sonnet-\d+(?:-\d+)*$", "mid"),
                    new ModelFamilyClassificationRule(@"^gemini-[\d.]+-pro$", "mid")
                }
            };
        }

        /// <summary>
        /// Build the former hardcoded stage-persona title-prefix guard.
        /// </summary>
        /// <returns>VoyageDispatchSettings with the guard on and the former prefix list.</returns>
        public static VoyageDispatchSettings CreateVoyageDispatch()
        {
            return new VoyageDispatchSettings
            {
                RejectStagePersonaTitlePrefixes = true,
                StagePersonaTitlePrefixes = new List<string>
                {
                    "[" + PersonaCatalog.Worker + "] ",
                    "[" + PersonaCatalog.Architect + "] ",
                    "[" + PersonaCatalog.ProductManager + "] ",
                    "[" + PersonaCatalog.UsabilityEngineer + "] ",
                    "[" + PersonaCatalog.TestEngineer + "] ",
                    "[" + PersonaCatalog.LegacyTestEngineer + "] ",
                    "[" + PersonaCatalog.Judge + "] "
                }
            };
        }

        /// <summary>
        /// Build the former baked specialist-reviewer prompt templates.
        /// </summary>
        /// <returns>The six specialist templates that used to live in PromptTemplateService.</returns>
        public static List<AdditionalPromptTemplateSettings> CreateAdditionalPromptTemplates()
        {
            return new List<AdditionalPromptTemplateSettings>
            {
                new AdditionalPromptTemplateSettings
                {
                    Name = "persona.diagnostic_protocol_reviewer",
                    Description = "Diagnostic protocol reviewer persona for binary/wire protocol and hardware-safety checks.",
                    RoleName = "DiagnosticProtocolReviewer",
                    Focus = "binary/wire protocol parsing, security-sensitive access paths, and high-risk hardware-affecting operations.",
                    Checklist =
                        "- Check protocol-parsing and security-sensitive access code for scope, auditability, and secret handling.\n" +
                        "- Treat high-risk hardware-affecting operations as out of bounds unless the mission explicitly authorizes a guarded analysis-only change.\n" +
                        "- Flag any path that could weaken a high-risk safety boundary.\n"
                },
                new AdditionalPromptTemplateSettings
                {
                    Name = "persona.tenant_security_reviewer",
                    Description = "Tenant security reviewer persona for authorization, tenant isolation, secrets, and auditability.",
                    RoleName = "TenantSecurityReviewer",
                    Focus = "multi-tenant authz/authn, tenant isolation, secrets, auditability, and cross-tenant leak risk.",
                    Checklist =
                        "- Verify authorization and authentication checks are applied at every entry point and background path touched by the diff.\n" +
                        "- Check tenant scoping on queries, events, logs, caches, queues, and identifiers.\n" +
                        "- Look for secrets in logs, exceptions, persisted payloads, test fixtures, and client-visible responses.\n" +
                        "- Confirm audit trails are complete enough to explain sensitive access or cross-tenant administration.\n"
                },
                new AdditionalPromptTemplateSettings
                {
                    Name = "persona.migration_data_reviewer",
                    Description = "Migration and data reviewer persona for schema, provider parity, and data-loss risk.",
                    RoleName = "MigrationDataReviewer",
                    Focus = "migrations, schema/provider parity, indexes, backfills, rollback/restart safety, and data-loss risk.",
                    Checklist =
                        "- Verify every supported provider has equivalent schema, index, nullability, default, and reader/writer behavior.\n" +
                        "- Check backfills and migrations for idempotency, restart safety, ordering, and large-data behavior.\n" +
                        "- Look for data-loss, truncation, casing, collation, timestamp, and enum/string compatibility risks.\n" +
                        "- Confirm rollback or failure behavior is documented or contained when a migration cannot be reversed.\n"
                },
                new AdditionalPromptTemplateSettings
                {
                    Name = "persona.performance_memory_reviewer",
                    Description = "Performance and memory reviewer persona for allocation, retention, throughput, and lifetime risks.",
                    RoleName = "PerformanceMemoryReviewer",
                    Focus = "memory/allocations, retained object graphs, process output/log growth, DB materialization, throughput, and resource lifetime.",
                    Checklist =
                        "- Look for unbounded collections, retained object graphs, large string accumulation, and process output/log growth.\n" +
                        "- Check database materialization, pagination, projection size, streaming, and repeated query patterns.\n" +
                        "- Review allocation-heavy loops, async lifetime, timer/task cleanup, disposal, cancellation, and retry behavior.\n" +
                        "- Validate that throughput-sensitive paths keep resource usage bounded under repeated orchestration operations.\n"
                },
                new AdditionalPromptTemplateSettings
                {
                    Name = "persona.porting_reference_analyst",
                    Description = "Porting reference analyst persona for evidence-based parity work against known references.",
                    RoleName = "PortingReferenceAnalyst",
                    Focus = "approved reference material, decompiler-derived notes, vendor traces, protocol captures, and semantic parity evidence for porting work.",
                    Checklist =
                        "- Compare the implementation to approved reference material, decompiler-derived notes, vendor traces, protocol captures, or other semantic parity evidence cited by the mission.\n" +
                        "- Distinguish evidence-backed parity from guesses, and flag missing references or assumptions explicitly.\n" +
                        "- Check naming, constants, byte layouts, state transitions, error mapping, and edge-case behavior against the cited evidence.\n" +
                        "- Keep changes traceable to the referenced behavior without copying unrelated implementation structure.\n"
                },
                new AdditionalPromptTemplateSettings
                {
                    Name = "persona.frontend_workflow_reviewer",
                    Description = "Frontend workflow reviewer persona for UX, accessibility, responsive states, and design consistency.",
                    RoleName = "FrontendWorkflowReviewer",
                    Focus = "frontend UX/workflow, accessibility, responsive states, i18n, errors, and design consistency.",
                    Checklist =
                        "- Walk the affected user workflow end to end, including empty, loading, error, disabled, success, and permission states.\n" +
                        "- Check accessibility semantics, keyboard flow, focus management, contrast, labels, and screen-reader impact.\n" +
                        "- Review responsive layout, text fit, i18n-ready copy, validation messages, and recoverability from failures.\n" +
                        "- Keep visual changes consistent with the existing design system and avoid introducing workflow dead ends.\n"
                }
            };
        }

        /// <summary>
        /// Build the former baked specialist personas.
        /// </summary>
        /// <returns>The six specialist personas that used to be seeded in code.</returns>
        public static List<AdditionalPersonaSettings> CreateAdditionalPersonas()
        {
            return new List<AdditionalPersonaSettings>
            {
                new AdditionalPersonaSettings { Name = "DiagnosticProtocolReviewer", Description = "Specialist reviewer for binary/wire protocol parsing, security-sensitive access paths, and high-risk hardware-affecting operations.", PromptTemplateName = "persona.diagnostic_protocol_reviewer" },
                new AdditionalPersonaSettings { Name = "TenantSecurityReviewer", Description = "Specialist reviewer for multi-tenant authz/authn, tenant isolation, secrets, auditability, and cross-tenant leak risk.", PromptTemplateName = "persona.tenant_security_reviewer" },
                new AdditionalPersonaSettings { Name = "MigrationDataReviewer", Description = "Specialist reviewer for migrations, schema/provider parity, indexes, backfills, rollback/restart safety, and data-loss risk.", PromptTemplateName = "persona.migration_data_reviewer" },
                new AdditionalPersonaSettings { Name = "PerformanceMemoryReviewer", Description = "Specialist reviewer for memory/allocations, retained object graphs, process output/log growth, DB materialization, throughput, and resource lifetime.", PromptTemplateName = "persona.performance_memory_reviewer" },
                new AdditionalPersonaSettings { Name = "PortingReferenceAnalyst", Description = "Specialist analyst for approved reference material, decompiler-derived notes, vendor traces, protocol captures, and semantic parity evidence for porting work.", PromptTemplateName = "persona.porting_reference_analyst" },
                new AdditionalPersonaSettings { Name = "FrontendWorkflowReviewer", Description = "Specialist reviewer for frontend UX/workflow, accessibility, responsive states, i18n, errors, and design consistency.", PromptTemplateName = "persona.frontend_workflow_reviewer" }
            };
        }

        /// <summary>
        /// Build the former baked specialist pipelines.
        /// </summary>
        /// <returns>The six specialist-tested pipelines that used to be seeded in code.</returns>
        public static List<AdditionalPipelineSettings> CreateAdditionalPipelines()
        {
            return new List<AdditionalPipelineSettings>
            {
                SpecialistTestedPipeline("DiagnosticProtocolTested", "Worker then DiagnosticProtocolReviewer then TestEngineer then Judge.", "DiagnosticProtocolReviewer"),
                SpecialistTestedPipeline("TenantSecurityTested", "Worker then TenantSecurityReviewer then TestEngineer then Judge.", "TenantSecurityReviewer"),
                SpecialistTestedPipeline("MigrationDataTested", "Worker then MigrationDataReviewer then TestEngineer then Judge.", "MigrationDataReviewer"),
                SpecialistTestedPipeline("PerformanceMemoryTested", "Worker then PerformanceMemoryReviewer then TestEngineer then Judge.", "PerformanceMemoryReviewer"),
                SpecialistTestedPipeline("ReferencePortingTested", "Worker then PortingReferenceAnalyst then TestEngineer then Judge.", "PortingReferenceAnalyst"),
                SpecialistTestedPipeline("FrontendWorkflowTested", "Worker then FrontendWorkflowReviewer then TestEngineer then Judge.", "FrontendWorkflowReviewer")
            };
        }

        /// <summary>
        /// Build a full ArmadaSettings instance that reproduces the previous hardcoded
        /// routing, guard, and specialist-asset behavior.
        /// </summary>
        /// <returns>Settings that yield today's decisions.</returns>
        public static ArmadaSettings CreateArmadaSettings()
        {
            return new ArmadaSettings
            {
                ModelTier = CreateModelTier(),
                VoyageDispatch = CreateVoyageDispatch(),
                AdditionalPromptTemplates = CreateAdditionalPromptTemplates(),
                AdditionalPersonas = CreateAdditionalPersonas(),
                AdditionalPipelines = CreateAdditionalPipelines()
            };
        }

        private static AdditionalPipelineSettings SpecialistTestedPipeline(string name, string description, string specialistPersonaName)
        {
            return new AdditionalPipelineSettings
            {
                Name = name,
                Description = description,
                Stages = new List<AdditionalPipelineStageSettings>
                {
                    new AdditionalPipelineStageSettings { Order = 1, PersonaName = "Worker" },
                    new AdditionalPipelineStageSettings { Order = 2, PersonaName = specialistPersonaName, PreferredModel = "high" },
                    new AdditionalPipelineStageSettings { Order = 3, PersonaName = "TestEngineer" },
                    new AdditionalPipelineStageSettings { Order = 4, PersonaName = "Judge" }
                }
            };
        }
    }
}
