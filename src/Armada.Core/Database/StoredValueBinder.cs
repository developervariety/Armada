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
        /// The binder of a provider, for code shared across providers that is handed the provider type.
        /// </summary>
        internal static StoredValueBinder For(Armada.Core.Enums.DatabaseTypeEnum provider)
        {
            return provider switch
            {
                Armada.Core.Enums.DatabaseTypeEnum.Sqlite => Sqlite.SqliteDatabaseDriver.StoredBinder,
                Armada.Core.Enums.DatabaseTypeEnum.Postgresql => Postgresql.PostgresqlDatabaseDriver.StoredBinder,
                Armada.Core.Enums.DatabaseTypeEnum.Mysql => Mysql.MysqlDatabaseDriver.StoredBinder,
                Armada.Core.Enums.DatabaseTypeEnum.SqlServer => SqlServer.SqlServerDatabaseDriver.StoredBinder,
                _ => throw new NotSupportedException("No stored value binder for " + provider + ".")
            };
        }

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
            command.Parameters.Add(Timestamp(command.CreateParameter(), parameterName, table, column, value));
        }

        /// <summary>
        /// Configure a provider parameter to hold a timestamp in the stored form of a column, for a command whose
        /// parameters are collected before the command exists.
        /// </summary>
        internal TParameter Timestamp<TParameter>(TParameter parameter, string parameterName, string table, string column, DateTime? value) where TParameter : DbParameter
        {
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

            return parameter;
        }

        /// <summary>
        /// Add a parameter holding a boolean in the stored form of a column.
        /// </summary>
        internal void AddBoolean(DbCommand command, string parameterName, string table, string column, bool? value)
        {
            command.Parameters.Add(Boolean(command.CreateParameter(), parameterName, table, column, value));
        }

        /// <summary>
        /// Configure a provider parameter to hold a boolean in the stored form of a column, for a command whose
        /// parameters are collected before the command exists.
        /// </summary>
        internal TParameter Boolean<TParameter>(TParameter parameter, string parameterName, string table, string column, bool? value) where TParameter : DbParameter
        {
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

            return parameter;
        }

        /// <summary>
        /// Add a typed parameter; null binds as a database null.
        /// </summary>
        internal static void Add(DbCommand command, string parameterName, DbType type, object? value)
        {
            command.Parameters.Add(Configure(command.CreateParameter(), parameterName, type, value));
        }

        private static TParameter Configure<TParameter>(TParameter parameter, string parameterName, DbType type, object? value) where TParameter : DbParameter
        {
            parameter.ParameterName = parameterName;
            parameter.DbType = type;
            parameter.Value = value ?? DBNull.Value;
            return parameter;
        }

        /// <summary>Configure a provider parameter to hold text; null binds as a database null.</summary>
        internal static TParameter Parameter<TParameter>(TParameter parameter, string parameterName, string? value) where TParameter : DbParameter => Configure(parameter, parameterName, DbType.String, value);

        /// <summary>Configure a provider parameter to hold a 32-bit integer; null binds as a database null.</summary>
        internal static TParameter Parameter<TParameter>(TParameter parameter, string parameterName, int? value) where TParameter : DbParameter => Configure(parameter, parameterName, DbType.Int32, value);

        /// <summary>Configure a provider parameter to hold a 64-bit integer; null binds as a database null.</summary>
        internal static TParameter Parameter<TParameter>(TParameter parameter, string parameterName, long? value) where TParameter : DbParameter => Configure(parameter, parameterName, DbType.Int64, value);

        /// <summary>
        /// Add a filter value whose type is known only at run time: text or an integer. A timestamp or a boolean has
        /// a stored form that depends on its column, so it is refused here and is bound by column instead.
        /// </summary>
        internal static void ValueOf(DbCommand command, string parameterName, object? value)
        {
            switch (value)
            {
                case null: Add(command, parameterName, DbType.String, null); break;
                case string text: Add(command, parameterName, DbType.String, text); break;
                case int number: Add(command, parameterName, DbType.Int32, number); break;
                case long number: Add(command, parameterName, DbType.Int64, number); break;
                case double number: Add(command, parameterName, DbType.Double, number); break;
                default: throw new ArgumentException("A " + value.GetType().Name + " filter value is bound by its column, not by run-time type.", nameof(value));
            }
        }

        /// <summary>Add a text parameter; null binds as a database null.</summary>
        internal static void Value(DbCommand command, string parameterName, string? value) => Add(command, parameterName, DbType.String, value);

        /// <summary>Add a 32-bit integer parameter; null binds as a database null.</summary>
        internal static void Value(DbCommand command, string parameterName, int? value) => Add(command, parameterName, DbType.Int32, value);

        /// <summary>Add a 64-bit integer parameter; null binds as a database null.</summary>
        internal static void Value(DbCommand command, string parameterName, long? value) => Add(command, parameterName, DbType.Int64, value);

        /// <summary>
        /// Add a boolean flag a statement tests but does not store; a stored boolean is bound by its column instead.
        /// </summary>
        internal static void Value(DbCommand command, string parameterName, bool? value) => Add(command, parameterName, DbType.Boolean, value);

        /// <summary>Add a double-precision parameter; null binds as a database null.</summary>
        internal static void Value(DbCommand command, string parameterName, double? value) => Add(command, parameterName, DbType.Double, value);

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
