namespace Armada.Core.Database
{
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Lossless conversion of the known historical PostgreSQL operational schema.</summary>
    internal static class PostgresqlLegacyOperationalRepair
    {
        internal const string RepairId = "postgres-operational-types-v1";
        internal const string Sql = @"
DO $repair$
DECLARE
    item record;
    shape record;
    bad boolean;
    relation text;
    fk record;
BEGIN
    FOR relation IN SELECT unnest(ARRAY['check_runs','deployments','environments','objective_refinement_sessions','releases','workflow_profiles']) LOOP
        IF to_regclass(format('%I.%I',current_schema(),relation)) IS NOT NULL THEN
            EXECUTE format('LOCK TABLE %I.%I IN ACCESS EXCLUSIVE MODE',current_schema(),relation);
        END IF;
    END LOOP;
    FOR item IN SELECT * FROM (VALUES
                ('workflow_profiles','created_utc'),
                ('workflow_profiles','last_update_utc'),
                ('check_runs','started_utc'),
                ('check_runs','completed_utc'),
                ('check_runs','created_utc'),
                ('check_runs','last_update_utc'),
                ('environments','created_utc'),
                ('environments','last_update_utc'),
                ('releases','created_utc'),
                ('releases','last_update_utc'),
                ('releases','published_utc'),
                ('deployments','approved_utc'),
                ('deployments','created_utc'),
                ('deployments','started_utc'),
                ('deployments','completed_utc'),
                ('deployments','verified_utc'),
                ('deployments','rolled_back_utc'),
                ('deployments','monitoring_window_ends_utc'),
                ('deployments','last_monitored_utc'),
                ('deployments','last_regression_alert_utc'),
                ('deployments','last_update_utc')
    ) AS known(table_name,column_name) LOOP
        SELECT data_type,column_default,is_generated,is_identity INTO shape
          FROM information_schema.columns WHERE table_schema=current_schema()
          AND table_name=item.table_name AND column_name=item.column_name;
        IF FOUND AND shape.data_type='text' THEN
            IF shape.column_default IS NOT NULL OR shape.is_generated<>'NEVER' OR shape.is_identity<>'NO' THEN
                RAISE EXCEPTION 'Incompatible legacy timestamp shape %.%',item.table_name,item.column_name;
            END IF;
            EXECUTE format('SELECT EXISTS(SELECT 1 FROM %I.%I WHERE %I IS NOT NULL AND %I !~ %L)',
                current_schema(),item.table_name,item.column_name,item.column_name,
                '^[0-9]{4}-[0-9]{2}-[0-9]{2}[T ]([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6}0*)?(Z|[+-][0-9]{2}(:?[0-9]{2})?)$') INTO bad;
            IF bad THEN RAISE EXCEPTION 'Incompatible legacy timestamp value %.%',item.table_name,item.column_name; END IF;
            EXECUTE format('ALTER TABLE %I.%I ALTER COLUMN %I TYPE timestamptz USING %I::timestamptz',
                current_schema(),item.table_name,item.column_name,item.column_name);
        END IF;
    END LOOP;
    SELECT data_type,column_default,is_generated,is_identity INTO shape FROM information_schema.columns
      WHERE table_schema=current_schema() AND table_name='check_runs' AND column_name='duration_ms';
    IF FOUND AND shape.data_type='integer' THEN
        IF shape.column_default IS NOT NULL OR shape.is_generated<>'NEVER' OR shape.is_identity<>'NO' THEN
            RAISE EXCEPTION 'Incompatible legacy duration shape';
        END IF;
        ALTER TABLE check_runs ALTER COLUMN duration_ms TYPE bigint;
    END IF;
    SELECT data_type,column_default,is_generated,is_identity INTO shape FROM information_schema.columns
      WHERE table_schema=current_schema() AND table_name='deployments' AND column_name='approval_required';
    IF FOUND AND shape.data_type='integer' THEN
        IF shape.column_default IS DISTINCT FROM '0' OR shape.is_generated<>'NEVER' OR shape.is_identity<>'NO' THEN
            RAISE EXCEPTION 'Incompatible legacy approval shape';
        END IF;
        IF EXISTS(SELECT 1 FROM deployments WHERE approval_required NOT IN (0,1)) THEN
            RAISE EXCEPTION 'Incompatible legacy approval value';
        END IF;
        ALTER TABLE deployments ALTER COLUMN approval_required DROP DEFAULT;
        ALTER TABLE deployments ALTER COLUMN approval_required TYPE boolean USING approval_required=1;
        ALTER TABLE deployments ALTER COLUMN approval_required SET DEFAULT false;
    END IF;
    FOR fk IN SELECT p.*,s.attname AS source_column,d.attname AS target_column,
        tn.nspname AS target_schema,t.relname AS target_table
        FROM pg_constraint p JOIN pg_class r ON r.oid=p.conrelid JOIN pg_namespace n ON n.oid=r.relnamespace
        JOIN pg_class t ON t.oid=p.confrelid JOIN pg_namespace tn ON tn.oid=t.relnamespace
        JOIN pg_attribute s ON s.attrelid=r.oid AND s.attnum=p.conkey[1]
        JOIN pg_attribute d ON d.attrelid=t.oid AND d.attnum=p.confkey[1]
        WHERE n.nspname=current_schema() AND r.relname='objective_refinement_sessions'
          AND p.contype='f' AND s.attname='captain_id' AND p.confdeltype='n'
    LOOP
        IF cardinality(fk.conkey)<>1 OR cardinality(fk.confkey)<>1 OR fk.target_schema<>current_schema()
          OR fk.target_table<>'captains' OR fk.target_column<>'id' OR fk.confupdtype<>'a'
          OR fk.confmatchtype<>'s' OR NOT fk.convalidated OR fk.condeferrable
          OR EXISTS(SELECT 1 FROM pg_trigger WHERE tgconstraint=fk.oid AND tgenabled<>'O') THEN
            RAISE EXCEPTION 'Incompatible legacy captain foreign key';
        END IF;
        EXECUTE format('ALTER TABLE %I.objective_refinement_sessions DROP CONSTRAINT %I',current_schema(),fk.conname);
        EXECUTE format('ALTER TABLE %I.objective_refinement_sessions ADD CONSTRAINT %I FOREIGN KEY(captain_id) REFERENCES %I.captains(id) ON DELETE NO ACTION',current_schema(),fk.conname,current_schema());
    END LOOP;
END $repair$;";

        internal static async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
