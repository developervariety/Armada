---
type: "Reference"
title: "RuntimeExitInfo and RuntimeFailureKindEnum: classifying agent exits"
description: "RuntimeFailureClassifier.Decide(exitCode, RuntimeProviderError) -> RuntimeExitInfo with FailureKind Clean, UsageLimit, AuthFailure, Crash, ModelUnavailable, from exit code plus structured provider error (type or HTTP status), never output text."
timestamp: "2026-10-05T05:35:47.8173260Z"
created: "2026-10-05T05:35:46.9809970Z"
slug: "rt-runtime-exit-info"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-quarantine-and-crash-loop", "rt-interrupted-exit-redispatch", "rt-mission-failure-kind"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Core/Models/RuntimeExitInfo.cs (ExitCode, FailureKind, ProviderError, Describe()), RuntimeProviderError.cs (HttpStatusCode, ErrorType, Message, RetryAfterSeconds, ResetUtc), src/Armada.Core/Services/RuntimeFailureClassifier.cs, RuntimeProviderErrorParser.cs, ProviderResetParser.cs.

Classify: exit 0 -> Clean; no provider error -> Crash; ErrorType rate_limit_error | rate_limit_exceeded | insufficient_quota | usage_limit_reached | billing_error | overloaded_error -> UsageLimit; authentication_error | permission_error | invalid_api_key -> AuthFailure; not_found_error | model_not_found -> ModelUnavailable; else HTTP 429/402/529 -> UsageLimit, 401/403 -> AuthFailure, 404 -> ModelUnavailable; otherwise Crash. IsCaptainUnavailable = UsageLimit, AuthFailure, ModelUnavailable.
Provider errors arrive on IAgentRuntime.OnProviderError from each runtime's machine channel (Claude text/stream-json error lines, ApiEndpoint HTTP status via RuntimeProviderErrorParser.FromHttpStatus). AdmiralService.HandleProcessExitAsync uses FailureKind once for quarantine/crash-loop decisions ([[rt-quarantine-and-crash-loop]]); the health-check path only knows the exit code (Decide(exitCode, null)). Negative exit codes are interruptions, handled before classification ([[rt-interrupted-exit-redispatch]]).
