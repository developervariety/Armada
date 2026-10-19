# Model provider smoke simulation

The fixture models the native xAI Responses and embeddings contracts used by
PolyPrompt 3.4.0, following the upstream PolyPrompt `postman/xai.openapi.json` and
its xAI fixture. The owner's published mock search was unavailable because the
Postman CLI was signed out. This fixture runs locally without login or real keys.

```sh
postman simulation run postman/simulations/model-providers.sim.yaml --port 4918 --no-history --output ndjson
```

In another terminal:

```sh
ARMADA_MODEL_PROVIDER_SIMULATION=http://127.0.0.1:4918/xai \
  dotnet run --project src/Test.Automated -f net10.0 -- \
  --suites Services.ModelEndpoint,Runtimes.ApiAgent
```

The optional HTTP cases exercise real PolyPrompt clients, endpoint validation,
and an API captain's local file tool execution with encrypted reasoning replay.
Other paths return 501. This proves local contract behavior, not live provider
availability. Ollama Cloud's fixed HTTPS destination is not redirected; its
factory, storage, authentication, and form behavior have offline coverage.
Use an accessible hosted model for Cloud, and an explicit catalog model for xAI
embeddings. Model access and Cloud embedding support depend on the account.
