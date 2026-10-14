---
type: "Project"
title: "Database access: interface-per-entity methods (IFleetMethods, IVesselMethods, ...)"
description: "Each entity has an I<Entity>Methods interface implemented once per provider (SQLite, PostgreSQL, MySQL, SQL Server); every new method must be implemented for all four and covered by the Database suites."
timestamp: "2026-10-05T05:43:36.4577330Z"
created: "2026-10-05T05:43:36.3204510Z"
slug: "database-interface-per-entity"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["db-parity-script", "upgrade-migrations-and-backups"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md Patterns): database access follows an interface-per-entity pattern (IFleetMethods, IVesselMethods, IMissionMethods, ...) exposed from the DatabaseDriver (e.g. _Database.Fleets). Each interface has an implementation per provider, so adding a method means four implementations plus Database.* test cases that run unchanged on every provider (run-db-parity-tests.sh). Migrations must be registered for all four providers and stay re-runnable and additive (Database.MigrationHygiene). Details of providers and migrations are in the data-and-migrations category.
