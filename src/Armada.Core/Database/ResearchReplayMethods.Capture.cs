namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class ResearchReplayMethods
    {
        private async Task<Snapshot> CaptureAsync(DbConnection connection, DbTransaction transaction, Voyage voyage, CancellationToken token)
        {
            Snapshot snapshot = new Snapshot { Voyage = voyage };
            snapshot.Rows["voyage"] = await RowsAsync(connection, transaction, "voyages", "id=@value", voyage.Id, "id", token).ConfigureAwait(false);
            using (DbCommand command = Command(connection, transaction, "SELECT * FROM missions WHERE voyage_id=@id ORDER BY stage_order,id;"))
            {
                Add(command, "@id", voyage.Id);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    List<SortedDictionary<string, object?>> rows = new List<SortedDictionary<string, object?>>();
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        snapshot.Missions.Add(MissionColumns.Read(reader, _Sql.Values));
                        rows.Add(RawRow(reader));
                    }
                    snapshot.Rows["missions"] = rows;
                }
            }
            snapshot.Rows["voyage_playbooks"] = await RowsAsync(connection, transaction, "voyage_playbooks", "voyage_id=@value", voyage.Id, "selection_order", token).ConfigureAwait(false);
            foreach (Mission mission in snapshot.Missions)
            {
                await CapturePlaybooksAsync(connection, transaction, snapshot, mission, token).ConfigureAwait(false);
            }
            snapshot.Rows["checks"] = await RowsAsync(connection, transaction, "check_runs", "voyage_id=@value", voyage.Id, "id", token).ConfigureAwait(false);
            if (snapshot.Missions.Count > 0 && !String.IsNullOrEmpty(snapshot.Missions[0].VesselId))
                snapshot.Rows["vessel"] = await RowsAsync(connection, transaction, "vessels", "id=@value", snapshot.Missions[0].VesselId!, "id", token,
                    "id,tenant_id,user_id,fleet_id,name,local_path,working_directory,default_branch,default_pipeline_id").ConfigureAwait(false);
            // Capture linked objective criteria and declared inputs without changing their acceptance or dispatch state.
            List<SortedDictionary<string, object?>> objectives = await RowsAsync(connection, transaction, "objectives",
                "voyage_ids_json LIKE @value", "%" + voyage.Id + "%", "id", token).ConfigureAwait(false);
            snapshot.Rows["objectives"] = objectives.Where(row =>
                JsonSerializer.Deserialize<List<string>>(Convert.ToString(row["voyage_ids_json"]) ?? "[]")?.Contains(voyage.Id) == true).ToList();
            return snapshot;
        }

        private async Task CapturePlaybooksAsync(DbConnection connection, DbTransaction transaction,
            Snapshot snapshot, Mission mission, CancellationToken token)
        {
            List<SortedDictionary<string, object?>> rows = new List<SortedDictionary<string, object?>>();
            using (DbCommand command = Command(connection, transaction,
                "SELECT * FROM mission_playbook_snapshots WHERE mission_id=@id ORDER BY selection_order;"))
            {
                Add(command, "@id", mission.Id);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        mission.PlaybookSnapshots.Add(PlaybookColumns.ReadSnapshot(reader, _Sql.Values));
                        rows.Add(RawRow(reader));
                    }
                    snapshot.Rows["playbooks:" + mission.Id] = rows;
                }
            }
        }

        private async Task RefuseActiveWorkAsync(DbConnection connection, DbTransaction transaction, string voyageId,
            List<Mission> stages, CancellationToken token)
        {
            foreach (Mission mission in stages)
            {
                if (await AnyAsync(connection, transaction, "captains", "current_mission_id=@value AND state<>'Idle'", mission.Id, token).ConfigureAwait(false)
                    || await AnyAsync(connection, transaction, "harbor_jobs", "mission_id=@value", mission.Id, token).ConfigureAwait(false)
                    || await AnyAsync(connection, transaction, "merge_entries", "mission_id=@value", mission.Id, token).ConfigureAwait(false)
                    || await AnyAsync(connection, transaction, "landing_jobs", "mission_id=@value", mission.Id, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Research replay refuses active captains or any landing lineage.");
            }
            if (await AnyAsync(connection, transaction, "check_runs", "voyage_id=@value AND status IN ('Pending','Running')", voyageId, token).ConfigureAwait(false))
                throw new InvalidOperationException("Research replay waits for every unresolved Check.");
        }

        private async Task<bool> AnyAsync(DbConnection connection, DbTransaction transaction, string table,
            string where, string value, CancellationToken token)
        {
            using (DbCommand command = Command(connection, transaction, "SELECT COUNT(*) FROM " + table + " WHERE " + where + ";"))
            {
                Add(command, "@value", value);
                return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
            }
        }

        private static async Task<List<SortedDictionary<string, object?>>> RowsAsync(DbConnection connection, DbTransaction transaction,
            string table, string where, string value, string order, CancellationToken token, string columns = "*")
        {
            using (DbCommand command = Command(connection, transaction, "SELECT " + columns + " FROM " + table + " WHERE " + where + " ORDER BY " + order + ";"))
            {
                Add(command, "@value", value);
                List<SortedDictionary<string, object?>> rows = new List<SortedDictionary<string, object?>>();
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(RawRow(reader));
                    return rows;
                }
            }
        }

        private static SortedDictionary<string, object?> RawRow(DbDataReader reader)
        {
            SortedDictionary<string, object?> row = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            for (int index = 0; index < reader.FieldCount; index++)
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            return row;
        }
    }
}
