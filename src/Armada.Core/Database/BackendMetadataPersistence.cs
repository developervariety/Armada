namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Persist model metadata without changing routing or landing policy.</summary>
    internal static class BackendMetadataPersistence
    {
        internal static void AddVoyage(DbCommand command, Voyage voyage)
        {
            Add(command, "source_planning_session_id", voyage.SourcePlanningSessionId);
            Add(command, "source_planning_message_id", voyage.SourcePlanningMessageId);
        }

        private static void Add(DbCommand command, string name, string? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.DbType = DbType.String;
            parameter.Value = (object?)value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
