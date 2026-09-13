namespace Armada.Test.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Mysql;
    using Armada.Core.Settings;
    using MySqlConnector;

    /// <summary>Populated pre-repair fixtures. Requires the scenario's empty-database guard.</summary>
    internal sealed class MysqlLegacyCompatibilityTests
    {
        private readonly DatabaseSettings _Settings;
        internal MysqlLegacyCompatibilityTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            using (MySqlConnection connection = new MySqlConnection(_Settings.GetConnectionString()))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                MysqlSchemaCompatibility compatibility = new MysqlSchemaCompatibility(connection);
                await compatibility.ExecuteSqlAsync(@"CREATE TABLE tenants(id VARCHAR(450) NOT NULL PRIMARY KEY);
CREATE TABLE users(id VARCHAR(450) NOT NULL PRIMARY KEY, tenant_id VARCHAR(450) NOT NULL, email VARCHAR(450) NOT NULL, touch_count INT NOT NULL DEFAULT 0, FOREIGN KEY(tenant_id) REFERENCES tenants(id) ON DELETE CASCADE);
CREATE TRIGGER count_updates BEFORE UPDATE ON users FOR EACH ROW SET NEW.touch_count=OLD.touch_count+1;
INSERT INTO tenants VALUES(REPEAT('🚢',450)),('valid');
INSERT INTO users(id,tenant_id,email) VALUES('long-a',REPEAT('🚢',450),CONCAT(REPEAT('🚢',449),'a')),('long-b',REPEAT('🚢',450),CONCAT(REPEAT('🚢',449),'b')),('other','valid',CONCAT(REPEAT('🚢',449),'a'));", token).ConfigureAwait(false);
                await EnsureAsync(compatibility, token).ConfigureAwait(false);
                DatabaseAssert.Equal(0L, await CountAsync(connection, "SELECT COUNT(*) FROM users WHERE touch_count<>1 OR armada_internal_tenant_key IS NULL;", token).ConfigureAwait(false), "Populated installation maps each row once");
                await EnsureAsync(compatibility, token).ConfigureAwait(false);
                DatabaseAssert.Equal(0L, await CountAsync(connection, "SELECT COUNT(*) FROM users WHERE touch_count<>1;", token).ConfigureAwait(false), "Repeated startup does not update application rows");
                await compatibility.ExecuteSqlAsync("DROP TRIGGER tr_armada_users_tenant_update; UPDATE users SET armada_internal_tenant_key=NULL WHERE id='long-a';", token).ConfigureAwait(false);
                await RejectAsync(() => EnsureAsync(compatibility, token), "Corrupt installed mapping").ConfigureAwait(false);
                DatabaseAssert.Equal(1L, await CountAsync(connection, "SELECT COUNT(*) FROM users WHERE id='long-a' AND armada_internal_tenant_key IS NULL;", token).ConfigureAwait(false), "Damaged mapping is rejected without rewriting data");
                await compatibility.ExecuteSqlAsync(@"DROP TABLE users;
CREATE TABLE users(id VARCHAR(450) NOT NULL PRIMARY KEY, tenant_id VARCHAR(450) NOT NULL, email VARCHAR(450) NOT NULL, FOREIGN KEY(tenant_id) REFERENCES tenants(id) ON DELETE CASCADE);
INSERT INTO users VALUES('dupe-a','valid','Résumé'),('dupe-b','valid','resume');", token).ConfigureAwait(false);
                bool duplicate = false;
                try { await EnsureAsync(compatibility, token).ConfigureAwait(false); }
                catch (MySqlException exception) when (exception.Number == 1062) { duplicate = true; }
                DatabaseAssert.True(duplicate, "Preexisting collation duplicates rejected");
                DatabaseAssert.Equal(2L, await CountAsync(connection, "SELECT COUNT(*) FROM users;", token).ConfigureAwait(false), "Rejected duplicate rows retained");
                await compatibility.ExecuteSqlAsync(@"DROP TABLE users;
CREATE TABLE users(id VARCHAR(450) NOT NULL PRIMARY KEY, tenant_id VARCHAR(450) NOT NULL, email VARCHAR(450) NOT NULL);
INSERT INTO users VALUES('orphan-a','missing','same'),('orphan-b','missing','same');", token).ConfigureAwait(false);
                bool orphan = false;
                try { await EnsureAsync(compatibility, token).ConfigureAwait(false); }
                catch (MySqlException exception) when (exception.Number == 1452) { orphan = true; }
                catch (InvalidOperationException exception) when (exception.Message.StartsWith("Incompatible", StringComparison.Ordinal)) { orphan = true; }
                DatabaseAssert.True(orphan, "Missing tenant foreign key cannot admit orphan duplicates");
                await compatibility.ExecuteSqlAsync(@"DROP TABLE users;
ALTER TABLE tenants ADD COLUMN guard INT NOT NULL DEFAULT 0, ADD UNIQUE KEY tenant_pair(id,guard);
CREATE TABLE users(id VARCHAR(450) NOT NULL PRIMARY KEY, tenant_id VARCHAR(450) NOT NULL, email VARCHAR(450) NOT NULL, tenant_guard INT NULL, FOREIGN KEY(tenant_id,tenant_guard) REFERENCES tenants(id,guard) ON DELETE CASCADE);
INSERT INTO users(id,tenant_id,email) VALUES('orphan','missing','same');", token).ConfigureAwait(false);
                await RejectAsync(() => EnsureAsync(compatibility, token), "Composite foreign key is not equivalent").ConfigureAwait(false);
                await compatibility.ExecuteSqlAsync("CREATE TABLE wrong_default(email VARCHAR(450) NOT NULL);", token).ConfigureAwait(false);
                foreach (string value in new[] { "NULL", "", " ", "(x)", "'x'", "((hello))" })
                {
                    string literal = "'" + value.Replace("'", "''") + "'";
                    await compatibility.ExecuteSqlAsync("ALTER TABLE wrong_default MODIFY COLUMN email VARCHAR(450) NOT NULL DEFAULT " + literal + ";", token).ConfigureAwait(false);
                    await RejectAsync(() => compatibility.ExecuteAsync("ALTER TABLE wrong_default ADD COLUMN email VARCHAR(450) NOT NULL;", 0, token), "Literal default differs from absent default").ConfigureAwait(false);
                    await compatibility.ExecuteAsync("ALTER TABLE wrong_default ADD COLUMN email VARCHAR(450) NOT NULL DEFAULT " + literal + ";", 0, token).ConfigureAwait(false);
                }
                await compatibility.ExecuteSqlAsync("ALTER TABLE wrong_default MODIFY COLUMN email VARCHAR(450) NOT NULL;", token).ConfigureAwait(false);
                await compatibility.ExecuteAsync("ALTER TABLE wrong_default ADD COLUMN email VARCHAR(450) NOT NULL;", 0, token).ConfigureAwait(false);
                // These are only the fixture tables created after the empty-database guard.
                // The ordinary migration runner now installs into the empty database.
                await compatibility.ExecuteSqlAsync("DROP TABLE wrong_default; DROP TABLE users; DROP TABLE tenants;", token).ConfigureAwait(false);
                Console.WriteLine("PASS MySQL populated compatibility, no rewrite, duplicate/orphan/FK/default rejection");
            }
        }

        private static Task EnsureAsync(MysqlSchemaCompatibility compatibility, CancellationToken token) =>
            compatibility.EnsureIndexAsync("users", "idx_users_tenant_email", "tenant_id, email", true, token);

        private static async Task RejectAsync(Func<Task> action, string message)
        {
            try { await action().ConfigureAwait(false); }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("Incompatible", StringComparison.Ordinal)) { return; }
            throw new Exception(message + " was not rejected");
        }

        private static async Task<long> CountAsync(MySqlConnection connection, string sql, CancellationToken token)
        {
            using (MySqlCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
            }
        }
    }
}
