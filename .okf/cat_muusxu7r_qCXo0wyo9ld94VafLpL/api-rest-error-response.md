---
type: "Reference"
title: "REST errors: ApiErrorResponse and stable Error codes"
description: "Every REST error is ApiErrorResponse {Error, StatusCode, Description, Message, Data}; branch on HTTP status then Error (BadRequest, DeserializationError, NotAuthorized, Forbidden, NotFound, Conflict, RequestTimeout, SlowDown, InternalError); never parse Message; cross-tenant is 404."
timestamp: "2026-10-05T05:48:37.0619510Z"
created: "2026-10-05T05:48:36.4753890Z"
slug: "api-rest-error-response"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-route-error-mapper", "api-rest-conventions", "decision-typed-not-found"]
version: "1"
salience: "0.5"
---
docs/REST_API.md "Error Responses" (frozen for 1.0). Fields: Error (stable code, enum ApiResultEnum from Watson), StatusCode (int), Description (standard text), Message (human detail, not stable), Data (optional structured detail, e.g. vessel import {Code, Path} with VesselImportCodes InvalidRequest, HarborNotSupported, PathNotAllowed, DirectoryNotFound, BatchNotFound, BatchBusy).
Codes -> status: BadRequest 400 (invalid input, invalid transition), DeserializationError 400, NotAuthorized 401, Forbidden 403 (visible but role cannot act; includes forced password change), NotFound 404 (missing route/entity, or another tenant's/user's entity; also an id referenced in a body such as VesselId on deployment create), Conflict 409 (state conflict; delete routes), RequestTimeout 408, SlowDown 429 (with Retry-After), InternalError 500 (Message carries the exception message). Statuses with no dedicated code are sent with Error "BadRequest": 422 (vessel push/merge rejected), 501 (planning/refinement on an unsupported runtime or provider, model context unavailable), 503 (git unavailable), 504 (model context build timeout) - use the HTTP status. Frozen exceptions to the shape: POST /api/v1/authenticate -> AuthenticateResult {Success:false} with 401; POST /api/v1/onboarding -> OnboardingResult {Success:false, ErrorMessage}; vessel git-status/branches report git failures in an Error field of a 200. Status selection rules: [[api-route-error-mapper]].
