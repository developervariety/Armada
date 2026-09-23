namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>
    /// Statements that prune a named part of the catalog from an existing database: scope-named
    /// playbooks with every link and default-playbook entry that names them, two named pipelines with
    /// their stages and every reference to them, one named persona with its prompt templates, the
    /// vessel_pack_hints table, and the threshold and playbook reference columns. Every provider
    /// migration uses these definitions, so all providers delete exactly the same rows. Operator rows
    /// outside these names and native captain memory are never touched.
    /// </summary>
    internal static class CatalogAndColumnPruneSchema
    {
        #region Private-Members

        // Scope-named playbooks: vessel-, persona-, captain- and fleet-<name>-learned.md.
        private static readonly string[] _ScopedPlaybookShapes = new string[]
        {
            "vessel-%-learned.md", "persona-%-learned.md", "captain-%-learned.md", "fleet-%-learned.md"
        };

        // Pipelines deleted outright: the two named pipelines, and any pipeline whose every stage names the pruned
        // persona, because deleting those stages would leave it with none.
        private static readonly string _PrunedPipelinePredicate =
            "pipelines.name IN ('Reflections', 'ReflectionsDualJudge') OR ("
            + "EXISTS (SELECT 1 FROM pipeline_stages AS consolidator WHERE consolidator.pipeline_id = pipelines.id AND consolidator.persona_name = 'MemoryConsolidator') "
            + "AND NOT EXISTS (SELECT 1 FROM pipeline_stages AS kept WHERE kept.pipeline_id = pipelines.id AND kept.persona_name <> 'MemoryConsolidator'))";

        private static readonly string _PrunedPipelineIds = "SELECT id FROM pipelines WHERE " + _PrunedPipelinePredicate;

        private static readonly string[] _DefaultPlaybookOwners = new string[] { "fleets", "vessels", "personas", "captains" };

        private static readonly string[] _PipelineReferences = new string[]
        {
            "fleets.default_pipeline_id", "vessels.default_pipeline_id", "project_profiles.default_pipeline_id", "objectives.suggested_pipeline_id"
        };

        private static readonly string[] _DroppedColumns = new string[]
        {
            "vessels.last_reflection_mission_id", "vessels.reflection_threshold", "vessels.reorganize_threshold", "vessels.pack_curate_threshold",
            "personas.curate_threshold", "personas.learned_playbook_id",
            "captains.curate_threshold", "captains.learned_playbook_id",
            "fleets.curate_threshold", "fleets.learned_playbook_id"
        };

        #endregion

        #region Public-Members

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = Build(DatabaseTypeEnum.Sqlite);

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = Build(DatabaseTypeEnum.Postgresql);

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = Build(DatabaseTypeEnum.Mysql);

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = Build(DatabaseTypeEnum.SqlServer);

        #endregion

        #region Private-Methods

        private static string[] Build(DatabaseTypeEnum provider)
        {
            string scopedIds = "SELECT id FROM playbooks WHERE " + ScopedPlaybook("playbooks");
            List<string> statements = new List<string>();

            statements.Add("DELETE FROM mission_playbook_snapshots WHERE playbook_id IN (" + scopedIds + ") OR (playbook_id IS NULL AND " + ScopedPlaybook("mission_playbook_snapshots") + ");");
            statements.Add("DELETE FROM voyage_playbooks WHERE playbook_id IN (" + scopedIds + ");");
            foreach (string owner in _DefaultPlaybookOwners)
                statements.Add(RemoveScopedDefaults(provider, owner));

            List<string> references = new List<string>(_PipelineReferences);
            // At this migration only the SQLite schema holds planning sessions, so only it carries this pipeline reference.
            if (provider == DatabaseTypeEnum.Sqlite) references.Add("planning_sessions.pipeline_id");
            foreach (string reference in references)
            {
                string[] parts = reference.Split('.');
                statements.Add("UPDATE " + parts[0] + " SET " + parts[1] + " = NULL WHERE " + parts[1] + " IN (" + _PrunedPipelineIds + ");");
            }
            statements.Add("DELETE FROM pipeline_stages WHERE pipeline_id IN (SELECT id FROM pipelines WHERE name IN ('Reflections', 'ReflectionsDualJudge'));");
            statements.Add("DELETE FROM pipelines WHERE " + _PrunedPipelinePredicate + ";");
            // Renumber the surviving stages before the pruned persona stages go, so a restarted run still finds the
            // affected pipelines. Parallel stages that share an order keep sharing it.
            statements.Add(RenumberStagesWithoutPrunedPersona(provider));
            statements.Add("DELETE FROM pipeline_stages WHERE persona_name = 'MemoryConsolidator';");

            statements.Add("UPDATE captains SET preferred_persona = NULL WHERE preferred_persona = 'MemoryConsolidator';");
            statements.Add(RemovePrunedPersonaFromAllowedPersonas(provider));
            statements.Add("DELETE FROM personas WHERE name = 'MemoryConsolidator';");
            statements.Add("DELETE FROM prompt_templates WHERE name IN ('persona.memory_consolidator', 'mission.model_context_updates');");
            statements.Add("DELETE FROM playbooks WHERE " + ScopedPlaybook("playbooks") + ";");

            statements.Add(provider == DatabaseTypeEnum.SqlServer
                ? "IF OBJECT_ID('vessel_pack_hints', 'U') IS NOT NULL DROP TABLE vessel_pack_hints;"
                : "DROP TABLE IF EXISTS vessel_pack_hints;");
            foreach (string dropped in _DroppedColumns)
            {
                string[] parts = dropped.Split('.');
                statements.Add(provider switch
                {
                    DatabaseTypeEnum.SqlServer => "IF COL_LENGTH('" + parts[0] + "', '" + parts[1] + "') IS NOT NULL ALTER TABLE " + parts[0] + " DROP COLUMN " + parts[1] + ";",
                    DatabaseTypeEnum.Postgresql => "ALTER TABLE " + parts[0] + " DROP COLUMN IF EXISTS " + parts[1] + ";",
                    _ => "ALTER TABLE " + parts[0] + " DROP COLUMN " + parts[1] + ";"
                });
            }

            return statements.ToArray();
        }

        private static string RenumberStagesWithoutPrunedPersona(DatabaseTypeEnum provider)
        {
            string ranked = "SELECT id, DENSE_RANK() OVER (PARTITION BY pipeline_id ORDER BY stage_order) AS new_order FROM pipeline_stages "
                + "WHERE persona_name <> 'MemoryConsolidator' AND pipeline_id IN (SELECT pipeline_id FROM pipeline_stages WHERE persona_name = 'MemoryConsolidator')";
            switch (provider)
            {
                case DatabaseTypeEnum.Sqlite:
                case DatabaseTypeEnum.Postgresql:
                    return "UPDATE pipeline_stages SET stage_order = ranked.new_order FROM (" + ranked + ") AS ranked "
                        + "WHERE ranked.id = pipeline_stages.id AND pipeline_stages.stage_order <> ranked.new_order;";
                case DatabaseTypeEnum.Mysql:
                    return "UPDATE pipeline_stages AS target JOIN (" + ranked + ") AS ranked ON ranked.id = target.id "
                        + "SET target.stage_order = ranked.new_order WHERE target.stage_order <> ranked.new_order;";
                case DatabaseTypeEnum.SqlServer:
                    return "UPDATE target SET stage_order = ranked.new_order FROM pipeline_stages AS target JOIN (" + ranked + ") AS ranked "
                        + "ON ranked.id = target.id WHERE target.stage_order <> ranked.new_order;";
                default:
                    throw new NotSupportedException("Unsupported database provider: " + provider);
            }
        }

        /// <summary>
        /// Rewrite a captain's allowed-persona JSON list without the pruned persona, keeping the other entries in
        /// order. A list that held only that persona becomes an empty list, never null, because a null list allows
        /// every persona.
        /// </summary>
        private static string RemovePrunedPersonaFromAllowedPersonas(DatabaseTypeEnum provider)
        {
            switch (provider)
            {
                case DatabaseTypeEnum.Sqlite:
                    return "UPDATE captains SET allowed_personas = (SELECT '[' || COALESCE(group_concat(kept.item, ','), '') || ']' FROM ("
                        + "SELECT CASE WHEN element.type IN ('object', 'array') THEN element.value ELSE json_quote(element.value) END AS item FROM json_each(captains.allowed_personas) AS element "
                        + "WHERE NOT (element.type = 'text' AND element.value = 'MemoryConsolidator') ORDER BY element.key) AS kept) "
                        + "WHERE CASE WHEN json_valid(captains.allowed_personas) AND json_type(captains.allowed_personas) = 'array' "
                        + "THEN instr(captains.allowed_personas, 'MemoryConsolidator') > 0 ELSE 0 END;";
                case DatabaseTypeEnum.Postgresql:
                    return "UPDATE captains SET allowed_personas = (SELECT COALESCE(jsonb_agg(element.item ORDER BY element.position), '[]'::jsonb)::text "
                        + "FROM jsonb_array_elements(captains.allowed_personas::jsonb) WITH ORDINALITY AS element(item, position) "
                        + "WHERE element.item <> to_jsonb('MemoryConsolidator'::text)) "
                        + "WHERE strpos(captains.allowed_personas, 'MemoryConsolidator') > 0;";
                case DatabaseTypeEnum.Mysql:
                    return "UPDATE captains SET allowed_personas = (SELECT COALESCE(JSON_ARRAYAGG(element.item), JSON_ARRAY()) "
                        + "FROM JSON_TABLE(captains.allowed_personas, '$[*]' COLUMNS (item_position FOR ORDINALITY, item JSON PATH '$')) AS element "
                        + "WHERE NOT JSON_CONTAINS(element.item, JSON_QUOTE('MemoryConsolidator'))) "
                        + "WHERE JSON_VALID(captains.allowed_personas) AND LOCATE('MemoryConsolidator', captains.allowed_personas) > 0;";
                case DatabaseTypeEnum.SqlServer:
                    return "UPDATE captains SET allowed_personas = COALESCE((SELECT '[' + STRING_AGG(CAST(CASE WHEN element.[type] = 1 THEN '\"' + STRING_ESCAPE(element.[value], 'json') + '\"' ELSE element.[value] END AS NVARCHAR(MAX)), ',') "
                        + "WITHIN GROUP (ORDER BY CAST(element.[key] AS INT)) + ']' FROM OPENJSON(captains.allowed_personas) AS element "
                        + "WHERE NOT (element.[type] = 1 AND element.[value] = N'MemoryConsolidator')), '[]') "
                        + "WHERE ISJSON(captains.allowed_personas) = 1 AND CHARINDEX('MemoryConsolidator', captains.allowed_personas) > 0;";
                default:
                    throw new NotSupportedException("Unsupported database provider: " + provider);
            }
        }

        private static string ScopedPlaybook(string table)
        {
            return "(" + String.Join(" OR ", _ScopedPlaybookShapes.Select(shape => table + ".file_name LIKE '" + shape + "'")) + ")";
        }

        /// <summary>
        /// Rewrite a default-playbook JSON list without its scope-named entries, keeping every other entry
        /// in order. Only rows whose text names a scope-named playbook id are rewritten. The playbook key is
        /// matched in camel and Pascal case because the list is parsed case-insensitively.
        /// </summary>
        private static string RemoveScopedDefaults(DatabaseTypeEnum provider, string owner)
        {
            string scopedIds = "SELECT id FROM playbooks WHERE " + ScopedPlaybook("playbooks");
            string namesScoped = "SELECT 1 FROM playbooks AS learned WHERE " + ScopedPlaybook("learned");
            switch (provider)
            {
                case DatabaseTypeEnum.Sqlite:
                    return "UPDATE " + owner + " SET default_playbooks = (SELECT '[' || COALESCE(group_concat(kept.item, ','), '') || ']' FROM ("
                        + "SELECT CASE WHEN element.type IN ('object', 'array') THEN element.value ELSE json_quote(element.value) END AS item FROM json_each(" + owner + ".default_playbooks) AS element "
                        + "WHERE (CASE WHEN element.type = 'object' THEN COALESCE(json_extract(element.value, '$.playbookId'), json_extract(element.value, '$.PlaybookId'), '') ELSE '' END) NOT IN (" + scopedIds + ") "
                        + "ORDER BY element.key) AS kept) "
                        + "WHERE CASE WHEN json_valid(" + owner + ".default_playbooks) AND json_type(" + owner + ".default_playbooks) = 'array' "
                        + "THEN EXISTS (" + namesScoped + " AND instr(" + owner + ".default_playbooks, learned.id) > 0) ELSE 0 END;";

                case DatabaseTypeEnum.Postgresql:
                    return "UPDATE " + owner + " SET default_playbooks = (SELECT COALESCE(jsonb_agg(element.item ORDER BY element.position), '[]'::jsonb)::text "
                        + "FROM jsonb_array_elements(" + owner + ".default_playbooks::jsonb) WITH ORDINALITY AS element(item, position) "
                        + "WHERE COALESCE(element.item->>'playbookId', element.item->>'PlaybookId', '') NOT IN (" + scopedIds + ")) "
                        + "WHERE EXISTS (" + namesScoped + " AND strpos(" + owner + ".default_playbooks, learned.id) > 0);";

                case DatabaseTypeEnum.Mysql:
                    // JSON comparison is binary, so matching through JSON_CONTAINS avoids mixing column collations.
                    return "UPDATE " + owner + " SET default_playbooks = (SELECT COALESCE(JSON_ARRAYAGG(element.item), JSON_ARRAY()) "
                        + "FROM JSON_TABLE(" + owner + ".default_playbooks, '$[*]' COLUMNS (item_position FOR ORDINALITY, item JSON PATH '$')) AS element "
                        + "WHERE NOT EXISTS (" + namesScoped + " AND (JSON_CONTAINS(element.item, JSON_QUOTE(learned.id), '$.playbookId') OR JSON_CONTAINS(element.item, JSON_QUOTE(learned.id), '$.PlaybookId')))) "
                        + "WHERE JSON_VALID(" + owner + ".default_playbooks) AND EXISTS (" + namesScoped + " AND LOCATE(learned.id, " + owner + ".default_playbooks) > 0);";

                case DatabaseTypeEnum.SqlServer:
                    return "UPDATE target SET default_playbooks = COALESCE((SELECT '[' + STRING_AGG(CAST(CASE WHEN element.[type] = 1 THEN '\"' + STRING_ESCAPE(element.[value], 'json') + '\"' ELSE element.[value] END AS NVARCHAR(MAX)), ',') "
                        + "WITHIN GROUP (ORDER BY CAST(element.[key] AS INT)) + ']' FROM OPENJSON(target.default_playbooks) AS element "
                        + "WHERE COALESCE(CASE WHEN element.[type] = 5 THEN COALESCE(JSON_VALUE(element.[value], '$.playbookId'), JSON_VALUE(element.[value], '$.PlaybookId')) END, '') NOT IN (" + scopedIds + ")), '[]') "
                        + "FROM " + owner + " AS target WHERE ISJSON(target.default_playbooks) = 1 "
                        + "AND EXISTS (" + namesScoped + " AND CHARINDEX(learned.id, target.default_playbooks) > 0);";

                default:
                    throw new NotSupportedException("Unsupported database provider: " + provider);
            }
        }

        #endregion
    }
}
