---
type: "Reference"
title: "Tenant, User, Credential: the multi-tenant identity model"
description: "TenantMetadata (ten_), UserMaster (usr_), Credential (crd_ bearer token); roles IsAdmin (global) / IsTenantAdmin / regular; every operational row carries TenantId and UserId."
timestamp: "2026-10-05T05:30:38.7948780Z"
created: "2026-10-05T05:30:38.4318900Z"
slug: "term-tenant-user-credential"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["api-rest-auth-headers", "data-tenant-ownership-columns"]
version: "1"
salience: "0.5"
---
Terms (src/Armada.Core/Models): TenantMetadata (ten_), UserMaster (usr_; Email, password hash, IsAdmin, IsTenantAdmin, IsProtected), Credential (crd_; 64-char bearer token linked to tenant+user, Active, IsProtected).

Roles: IsAdmin=true global system admin (all tenants); IsTenantAdmin=true admin within one tenant (users, credentials, all tenant rows); regular user = own rows plus tenant-wide config. Seed: default tenant "default", user admin@armada / password, credential bearer token "default" (disabled once the default password is changed). Creating a tenant seeds a protected admin@armada tenant admin. The local API key (settings ApiKey, generated as "ak_..." on first start for the CLI) maps to synthetic protected identities ten_system / usr_system (global admin).
Data scoping: Category A owned entities (fleets, vessels, voyages, captains, missions, docks, signals, events, merge entries, objectives) - regular users see own rows; Category B shared config (model endpoints, playbooks, skills, prompt templates, personas, pipelines, workflow/project profiles, runbooks, memories) carry ScopeEnum TenantWide or UserSpecific. Cross-tenant access returns 404. Auth details belong to the security category. See [[api-rest-auth-headers]].
