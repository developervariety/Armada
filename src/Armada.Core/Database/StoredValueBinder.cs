namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Globalization;

    /// <summary>
    /// Binds model values to one provider's stored column forms. Each provider configures one instance with how it
    /// stores each timestamp column and which boolean columns it stores as integers; a write names the table and
    /// column, and the binder sends the value in the form that column holds, typed explicitly, so no stored form
    /// depends on a driver's type inference, the host time zone or the database session time zone.
    /// </summary>
    internal sealed class StoredValueBinder
    {
        #region Internal-Members

        /// <summary>
        /// Provider name used in messages.
        /// </summary>
        internal string Provider { get; }

        #endregion

        #region Private-Members

        private const string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        private readonly StoredTimestampEnum _DefaultTimestamp;
        private readonly Dictionary<string, StoredTimestampEnum> _Timestamps;
        private readonly bool _IntegerBooleans;
        private readonly HashSet<string> _IntegerBooleanColumns;
        private readonly DbType _TimestampDbType;
        private readonly DbType _ZonedTimestampDbType;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="provider">Provider name used in messages.</param>
        /// <param name="defaultTimestamp">Storage of every timestamp column not named in <paramref name="timestamps"/>.</param>
        /// <param name="timestamps">Storage of each other timestamp column, keyed "table.column".</param>
        /// <param name="integerBooleans">True when the provider stores every boolean as an integer.</param>
        /// <param name="integerBooleanColumns">Boolean columns, keyed "table.column", stored as integers on a provider
        /// whose other booleans are native.</param>
        /// <param name="timestampDbType">Parameter type that sends a zone-less timestamp on this provider.</param>
        /// <param name="zonedTimestampDbType">Parameter type that sends a zone-aware timestamp on this provider.</param>
        internal StoredValueBinder(
            string provider,
            StoredTimestampEnum defaultTimestamp,
            IDictionary<string, StoredTimestampEnum> timestamps,
            bool integerBooleans,
            IEnumerable<string> integerBooleanColumns,
            DbType timestampDbType,
            DbType zonedTimestampDbType)
        {
            if (String.IsNullOrEmpty(provider)) throw new ArgumentNullException(nameof(provider));
            Provider = provider;
            _DefaultTimestamp = defaultTimestamp;
            _Timestamps = new Dictionary<string, StoredTimestampEnum>(timestamps ?? throw new ArgumentNullException(nameof(timestamps)), StringComparer.OrdinalIgnoreCase);
            _IntegerBooleans = integerBooleans;
            _IntegerBooleanColumns = new HashSet<string>(integerBooleanColumns ?? throw new ArgumentNullException(nameof(integerBooleanColumns)), StringComparer.OrdinalIgnoreCase);
            _TimestampDbType = timestampDbType;
            _ZonedTimestampDbType = zonedTimestampDbType;
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Start binding the parameters of one command that writes or filters one table.
        /// </summary>
        /// <param name="command">Command to add parameters to.</param>
        /// <param name="table">Table the parameters address.</param>
        /// <returns>The parameter set.</returns>
        internal StoredParameters For(DbCommand command, string table)
        {
            return new StoredParameters(this, command, table);
        }

        /// <summary>
        /// How this provider stores a timestamp column.
        /// </summary>
        internal StoredTimestampEnum TimestampStorage(string table, string column)
        {
            return _Timestamps.TryGetValue(table + "." + column, out StoredTimestampEnum storage) ? storage : _DefaultTimestamp;
        }

        /// <summary>
        /// Whether this provider stores a boolean column as an integer where only 1 means true.
        /// </summary>
        internal bool StoresBooleanAsInteger(string table, string column)
        {
            return _IntegerBooleans || _IntegerBooleanColumns.Contains(table + "." + column);
        }

        /// <summary>
        /// The timestamp columns this provider names individually, keyed "table.column".
        /// </summary>
        internal IReadOnlyDictionary<string, StoredTimestampEnum> NamedTimestamps => _Timestamps;

        /// <summary>
        /// The boolean columns this provider stores as integers although its other booleans are native.
        /// </summary>
        internal IReadOnlyCollection<string> IntegerBooleanColumns => _IntegerBooleanColumns;

        /// <summary>
        /// Add a parameter holding a timestamp in the stored form of a column.
        /// </summary>
        internal void AddTimestamp(DbCommand command, string parameterName, string table, string column, DateTime? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            StoredTimestampEnum storage = TimestampStorage(table, column);
            DateTime? instant = value.HasValue ? UtcInstant(value.Value) : (DateTime?)null;
            switch (storage)
            {
                case StoredTimestampEnum.Iso8601Text:
                    parameter.DbType = DbType.String;
                    parameter.Value = instant.HasValue ? instant.Value.ToString(_Iso8601Format, CultureInfo.InvariantCulture) : DBNull.Value;
                    break;
                case StoredTimestampEnum.Timestamp:
                    parameter.DbType = _TimestampDbType;
                    parameter.Value = instant.HasValue ? DateTime.SpecifyKind(instant.Value, DateTimeKind.Unspecified) : DBNull.Value;
                    break;
                case StoredTimestampEnum.ServerRenderedText:
                case StoredTimestampEnum.TimestampWithZone:
                    parameter.DbType = _ZonedTimestampDbType;
                    parameter.Value = instant.HasValue ? ZonedValue(instant.Value) : DBNull.Value;
                    break;
                default:
                    throw new InvalidOperationException("Unknown timestamp storage " + storage + " for " + table + "." + column + " on " + Provider + ".");
            }

            command.Parameters.Add(parameter);
        }

        /// <summary>
        /// Add a parameter holding a boolean in the stored form of a column.
        /// </summary>
        internal void AddBoolean(DbCommand command, string parameterName, string table, string column, bool? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            if (StoresBooleanAsInteger(table, column))
            {
                parameter.DbType = DbType.Int32;
                parameter.Value = value.HasValue ? (value.Value ? 1 : 0) : DBNull.Value;
            }
            else
            {
                parameter.DbType = DbType.Boolean;
                parameter.Value = value.HasValue ? value.Value : DBNull.Value;
            }

            command.Parameters.Add(parameter);
        }

        /// <summary>
        /// Add a typed parameter; null binds as a database null.
        /// </summary>
        internal static void Add(DbCommand command, string parameterName, DbType type, object? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.DbType = type;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        /// <summary>
        /// The UTC instant a model timestamp names. A timestamp without a kind is a UTC instant, as a stored one
        /// without an offset reads, so the host time zone never changes the instant written.
        /// </summary>
        internal static DateTime UtcInstant(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        #endregion

        #region Private-Methods

        private object ZonedValue(DateTime instant)
        {
            // A zone-aware parameter carries the instant as UTC; a provider without a zone-aware type receives the
            // UTC wall-clock time.
            return _ZonedTimestampDbType == DbType.DateTimeOffset ? new DateTimeOffset(instant, TimeSpan.Zero) : (object)instant;
        }

        #endregion
    }
}
