namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Writes the trace record of each record data expiry is about to delete, while its row still exists.
    /// An expiring voyage, mission, merge entry, check run or deployment row is recorded again, so the
    /// stored record holds its last state; an expiring event row records the record it names when that
    /// record has no trace record yet. A failed write throws, which stops the expiry of that table so no
    /// row leaves without its trace record.
    /// </summary>
    public sealed class TraceExpiryObserver : IDataExpiryObserver
    {
        #region Private-Members

        private readonly TraceRecorder _Recorder;
        private readonly HashSet<string> _Seen = new HashSet<string>(StringComparer.Ordinal);
        private const int _SeenLimit = 100000;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="recorder">Trace recorder.</param>
        public TraceExpiryObserver(TraceRecorder recorder)
        {
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task OnRowExpiringAsync(string table, IReadOnlyDictionary<string, object?> row, CancellationToken token)
        {
            if (row == null) return;
            TraceEntityKindEnum kind;
            string? id;
            bool onlyWhenUntraced;
            switch (table)
            {
                case "missions": kind = TraceEntityKindEnum.Mission; id = Read(row, "id"); onlyWhenUntraced = false; break;
                case "voyages": kind = TraceEntityKindEnum.Voyage; id = Read(row, "id"); onlyWhenUntraced = false; break;
                case "merge_entries": kind = TraceEntityKindEnum.MergeEntry; id = Read(row, "id"); onlyWhenUntraced = false; break;
                case "check_runs": kind = TraceEntityKindEnum.CheckRun; id = Read(row, "id"); onlyWhenUntraced = false; break;
                case "deployments": kind = TraceEntityKindEnum.Deployment; id = Read(row, "id"); onlyWhenUntraced = false; break;
                case "events":
                    id = Read(row, "entity_id");
                    if (!TraceRecorder.TryResolveKind(id, out kind)) return;
                    onlyWhenUntraced = true;
                    break;
                default:
                    return;
            }
            if (String.IsNullOrWhiteSpace(id)) return;

            // Many expiring events name the same record; one check per record per run is enough.
            if (onlyWhenUntraced)
            {
                if (_Seen.Count >= _SeenLimit) _Seen.Clear();
                if (!_Seen.Add(id)) return;
                await _Recorder.RecordUntracedBeforeExpiryAsync(kind, id, token).ConfigureAwait(false);
                return;
            }
            await _Recorder.RecordAsync(kind, id, true, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static string? Read(IReadOnlyDictionary<string, object?> row, string column)
        {
            if (!row.TryGetValue(column, out object? value) || value == null || value == DBNull.Value) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
