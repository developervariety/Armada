---
type: "Reference"
title: "Ownership columns: tenant_id and user_id on every operational table"
description: "Since v0.3 every operational row carries tenant_id and user_id, indexed and protected by foreign keys on all providers; Category A entities are scoped per user, Category B config rows carry a TenantWide/UserSpecific scope column."
timestamp: "2026-10-05T05:47:02.9180930Z"
created: "2026-10-05T05:47:02.4378820Z"
slug: "data-tenant-ownership-columns"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["term-tenant-user-credential", "data-methods-pattern", "mcp-tool-caller-scoping"]
version: "1"
salience: "0.5"
---
Schema: v0.3.0 added tenants (TenantMetadata), users (UserMaster), credentials, and TenantId + UserId on all operational tables; existing rows were assigned to tenant "default" and the earliest user of the tenant (SQLite migration v15 rebuilt tables to add the FKs; it is one of the two reviewed destructive-looking migrations and copies every row). Ownership columns are indexed and enforced by foreign keys on SQLite, PostgreSQL, MySQL, SQL Server (SQLite FK enforcement relies on ForeignKeys=true in the connection string).

Access patterns in I*Methods: unscoped ReadAsync(id) (system paths such as the process-exit handler, which only knows ids), tenant-scoped ReadAsync(tenantId, id), user-scoped ReadAsync(tenantId, userId, id), and the same for Enumerate/Delete/Exists. Routes/MCP choose the overload from the caller's AuthContext (global admin any, tenant admin own tenant, regular user own rows; cross-tenant -> 404). Category B tables (personas, pipelines, playbooks, prompt templates, skills, workflow/project profiles, model endpoints, memories, runbooks) store a scope (ScopeEnum TenantWide / UserSpecific; profiles name it ownership_scope). Protected seed rows (IsProtected) cannot be deleted. Plain (non-FK) references from events and planning_sessions to parents are cleaned by CascadeCleanup on hard delete. Some recovery code uses a system AuthContext with no tenant so incident snapshots carry no owner FK.
