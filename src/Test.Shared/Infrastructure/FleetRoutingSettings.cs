namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using Armada.Core;
    using Armada.Core.Settings;

    /// <summary>
    /// Fleet routing, guard, and specialist-asset settings supplied through configuration. Tests that
    /// assert fleet deployment behavior pass this fixture; product defaults stay empty and policy-neutral.
    /// </summary>
    public static class FleetRoutingSettings
    {
        /// <summary>
        /// Specialist persona names reserved for the high tier.
        /// </summary>
        public static readonly string[] SpecialistPersonaNames = new string[]
        {
            "Judge",
            "Architect",
            "TestEngineer",
            "DiagnosticProtocolReviewer",
            "TenantSecurityReviewer",
            "PortingReferenceAnalyst"
        };

        /// <summary>
        /// Build the fleet model-tier settings, including family rules and
        /// the non-native-first / preference-order policy.
        /// </summary>
        /// <returns>The fleet ModelTierSettings.</returns>
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
                    "example/mid-audit"
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
                    { "example/mid-audit", new ModelCapabilityProfile { TelemetryRichness = 60, AuditReasoningFit = 65, MechanicalThroughput = 68, Cost = 55 } }
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
        /// Build the stage-persona title-prefix guard.
        /// </summary>
        /// <returns>VoyageDispatchSettings with the guard on and the stage persona prefix list.</returns>
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
        /// Build the specialist reviewer and analyst prompt templates.
        /// </summary>
        /// <returns>The three specialist reviewer and analyst templates.</returns>
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
                    Name = "persona.porting_reference_analyst",
                    Description = "Porting reference analyst persona for evidence-based parity work against known references.",
                    RoleName = "PortingReferenceAnalyst",
                    Focus = "approved reference material, decompiler-derived notes, vendor traces, protocol captures, and semantic parity evidence for porting work.",
                    Checklist =
                        "- Compare the implementation to approved reference material, decompiler-derived notes, vendor traces, protocol captures, or other semantic parity evidence cited by the mission.\n" +
                        "- Distinguish evidence-backed parity from guesses, and flag missing references or assumptions explicitly.\n" +
                        "- Check naming, constants, byte layouts, state transitions, error mapping, and edge-case behavior against the cited evidence.\n" +
                        "- Keep changes traceable to the referenced behavior without copying unrelated implementation structure.\n"
                }
            };
        }

        /// <summary>
        /// Build the specialist reviewer and analyst personas.
        /// </summary>
        /// <returns>The three specialist reviewer and analyst personas.</returns>
        public static List<AdditionalPersonaSettings> CreateAdditionalPersonas()
        {
            return new List<AdditionalPersonaSettings>
            {
                new AdditionalPersonaSettings { Name = "DiagnosticProtocolReviewer", Description = "Specialist reviewer for binary/wire protocol parsing, security-sensitive access paths, and high-risk hardware-affecting operations.", PromptTemplateName = "persona.diagnostic_protocol_reviewer" },
                new AdditionalPersonaSettings { Name = "TenantSecurityReviewer", Description = "Specialist reviewer for multi-tenant authz/authn, tenant isolation, secrets, auditability, and cross-tenant leak risk.", PromptTemplateName = "persona.tenant_security_reviewer" },
                new AdditionalPersonaSettings { Name = "PortingReferenceAnalyst", Description = "Specialist analyst for approved reference material, decompiler-derived notes, vendor traces, protocol captures, and semantic parity evidence for porting work.", PromptTemplateName = "persona.porting_reference_analyst" }
            };
        }

        /// <summary>
        /// Build the specialist-tested pipelines.
        /// </summary>
        /// <returns>The three specialist-tested pipelines.</returns>
        public static List<AdditionalPipelineSettings> CreateAdditionalPipelines()
        {
            return new List<AdditionalPipelineSettings>
            {
                SpecialistTestedPipeline("DiagnosticProtocolTested", "Worker then DiagnosticProtocolReviewer then TestEngineer then Judge.", "DiagnosticProtocolReviewer"),
                SpecialistTestedPipeline("TenantSecurityTested", "Worker then TenantSecurityReviewer then TestEngineer then Judge.", "TenantSecurityReviewer"),
                SpecialistTestedPipeline("ReferencePortingTested", "Worker then PortingReferenceAnalyst then TestEngineer then Judge.", "PortingReferenceAnalyst")
            };
        }

        /// <summary>
        /// Build a full ArmadaSettings instance carrying the fleet
        /// routing, guard, and specialist-asset configuration.
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
