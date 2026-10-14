---
type: "Reference"
title: "Model endpoint (mep_): managed embedding/inference provider"
description: "ModelEndpoint (mep_) registers an external Embedding or Inference model (Ollama, OpenAI, OpenAICompatible, Anthropic, Gemini, VoyageAI, AzureOpenAI, VertexAI, Bedrock); health-checked, validatable, API key write-only; backs ApiEndpoint captains."
timestamp: "2026-10-05T05:32:52.5754870Z"
created: "2026-10-05T05:32:52.2865510Z"
slug: "term-model-endpoint"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["rt-api-endpoint-runtime"]
version: "1"
salience: "0.5"
---
Term: Model endpoint (src/Armada.Core/Models/ModelEndpoint.cs, prefix mep_; ModelEndpointService, ModelEndpointClientFactory). Kind ModelEndpointKindEnum: Embedding, Inference. Provider ModelProviderEnum: Ollama, OpenAI, OpenAICompatible, Anthropic, Gemini, VoyageAI, AzureOpenAI, VertexAI, Bedrock. Health EndpointHealthStatusEnum Unknown/Healthy/Unhealthy with ModelEndpointHealthRecord.

Armada health-checks endpoints deduplicated by base URL (sweep every 10 health-check cycles, CheckHealthAllAsync), lets an operator validate one with a real request (ModelEndpointProbeResult), and never returns the stored API key on reads. An Inference endpoint backs ApiEndpoint captains ([[rt-api-endpoint-runtime]]); the server resolves it via ArmadaServer.ResolveInferenceEndpoint (also passed to Harbor launches as HarborInferenceEndpoint). Writes to own endpoints need only authentication (Category B). REST /api/v1/model-endpoints (GET returns a plain array); MCP get/create/update/delete/validate_model_endpoint, health_check_model_endpoints.
