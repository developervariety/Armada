namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>
    /// Statements that delete three named built-in reviewer personas and their built-in prompt templates. A persona
    /// is deleted only when no pipeline stage and no captain names it; a template is deleted only when no remaining
    /// persona names it. Every provider migration uses these definitions, so all providers delete exactly the same
    /// rows.
    /// </summary>
    internal static class ReviewerPersonaPruneSchema
    {
        #region Private-Members

        private static readonly string[] _PersonaNames = new string[]
        {
            "MigrationDataReviewer", "PerformanceMemoryReviewer", "FrontendWorkflowReviewer"
        };

        private static readonly string[] _TemplateNames = new string[]
        {
            "persona.migration_data_reviewer", "persona.performance_memory_reviewer", "persona.frontend_workflow_reviewer"
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
            string builtIn = provider == DatabaseTypeEnum.Postgresql ? "TRUE" : "1";
            List<string> statements = new List<string>();

            statements.Add("DELETE FROM personas WHERE name IN (" + NameList(_PersonaNames) + ") AND is_built_in = " + builtIn + " "
                + "AND NOT EXISTS (SELECT 1 FROM pipeline_stages WHERE pipeline_stages.persona_name = personas.name) "
                + "AND NOT EXISTS (SELECT 1 FROM captains WHERE captains.preferred_persona = personas.name "
                + "OR captains.allowed_personas LIKE " + QuotedNamePattern(provider) + ");");

            statements.Add("DELETE FROM prompt_templates WHERE name IN (" + NameList(_TemplateNames) + ") AND is_built_in = " + builtIn + " "
                + "AND NOT EXISTS (SELECT 1 FROM personas WHERE personas.prompt_template_name = prompt_templates.name);");

            return statements.ToArray();
        }

        private static string NameList(string[] names)
        {
            return String.Join(", ", names.Select(name => "'" + name + "'"));
        }

        /// <summary>
        /// A LIKE pattern matching the persona name as a quoted entry of the captain allowed-persona JSON list.
        /// </summary>
        private static string QuotedNamePattern(DatabaseTypeEnum provider)
        {
            switch (provider)
            {
                case DatabaseTypeEnum.Sqlite:
                case DatabaseTypeEnum.Postgresql:
                    return "'%\"' || personas.name || '\"%'";
                case DatabaseTypeEnum.Mysql:
                    return "CONCAT('%\"', personas.name, '\"%')";
                case DatabaseTypeEnum.SqlServer:
                    return "'%\"' + personas.name + '\"%'";
                default:
                    throw new NotSupportedException("Unsupported database provider: " + provider);
            }
        }

        #endregion
    }
}
