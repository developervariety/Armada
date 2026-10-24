namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Validated, versioned Harbor checkout lifecycle storage and compare-and-set writes.</summary>
    internal static partial class HarborCheckoutLifecyclePersistence
    {
        internal static async Task<bool> TryWriteAsync(DbConnection connection, DatabaseTypeEnum provider,
            string dockId, string expectedBindingJson, HarborCheckoutLifecycleIntent? expected,
            HarborCheckoutLifecycleIntent next, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(dockId)) throw new ArgumentNullException(nameof(dockId));
            if (String.IsNullOrWhiteSpace(expectedBindingJson)) throw new ArgumentNullException(nameof(expectedBindingJson));
            if (next == null || !Valid(next) || !String.Equals(next.DockId, dockId, StringComparison.Ordinal)) throw InvalidIntent();
            HarborCheckoutBinding? binding = JsonSerializer.Deserialize<HarborCheckoutBinding>(expectedBindingJson);
            if (binding == null || !binding.IsComplete || binding.Revision != next.BindingRevision)
                throw InvalidIntent();
            string? expectedJson = expected?.StoredJson ?? (expected == null ? null : Serialize(expected));
            if (expected != null && !ValidTransition(expected, next)) throw InvalidIntent();
            string nextJson = Serialize(next);
            using (DbCommand command = connection.CreateCommand())
            {
                string compare = expectedJson == null
                    ? "host_checkout_lifecycle_json IS NULL"
                    : BinaryEquals("host_checkout_lifecycle_json", "@expected", provider);
                command.CommandText = "UPDATE docks SET host_checkout_lifecycle_json = @next, last_update_utc = @updated WHERE id = @id AND "
                    + BinaryEquals("host_checkout_json", "@binding", provider) + " AND " + compare + ";";
                StoredValueBinder.Value(command, "@id", dockId);
                StoredValueBinder.Value(command, "@binding", expectedBindingJson);
                StoredValueBinder.Value(command, "@next", nextJson);
                StoredValueBinder.For(provider).For(command, "docks").Utc("@updated", "last_update_utc", DateTime.UtcNow);
                if (expectedJson != null) StoredValueBinder.Value(command, "@expected", expectedJson);
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    bool written = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                    if (written) next.StoredJson = nextJson;
                    return written;
                }
            }
        }

    }
}
