---
type: "Reference"
title: "Startup seeding and how built-in records upgrade"
description: "After migrations the Admiral seeds what the database lacks: default tenant/user/credential, prompt templates, personas, pipelines, deployment environments, fleet actions (every tenant); unedited built-ins pick up new defaults, edited ones keep user changes, except built-in FullPipeline stages are reset."
timestamp: "2026-10-05T05:47:27.8534030Z"
created: "2026-10-05T05:47:27.3766690Z"
slug: "data-seeding-and-builtin-upgrades"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["term-pipeline", "term-prompt-template", "term-fleet-action", "arch-admiral-startup-sequence"]
version: "1"
salience: "0.5"
---
Order in ArmadaServer.StartAsync after InitializeAsync: auth seeding inside the driver/default credential service (tenant "default", admin@armada, bearer credential "default", all protected; synthetic ten_system/usr_system for the API key), then PromptTemplateService.SeedDefaultsAsync (embedded defaults; unchanged built-ins updated, edited ones kept), PersonaSeedService.SeedAsync (personas Worker, Architect, Product Manager, Usability Engineer, Test Engineer, Linter, Judge, Recorder; pipelines WorkerOnly, Reviewed, Tested, FullPipeline, Recorded; normalizes legacy "TestEngineer" naming; FullPipeline's stage list is reset whenever it differs from the shipped definition), DeploymentEnvironmentService.SeedDefaultsAsync, FleetActionSeedService.SeedAllTenantsAsync (five built-ins per tenant, keyed so a deleted built-in is never re-seeded: soft-delete Active=false). Seeding failures for fleet actions are logged, not fatal. Creating a new tenant seeds its own protected admin@armada + credential (tenant admin only). Upgrades thus deliver new defaults (e.g. Recorder stage, memory-recall template sections) without clobbering customizations; copy FullPipeline to a custom pipeline to change its stages.
