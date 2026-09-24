namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Globalization;
    using System.Text.Json;

    /// <summary>
    /// One stored row read by column name, for a named entity, through a provider's value converter. Every read
    /// states what the column may hold: a required read throws a <see cref="StoredRowException"/> naming the
    /// entity and column when the column is missing, null where null is not allowed, or unconvertible. A column
    /// the schema allows to be absent is tested with <see cref="Has"/> first, so its absence is a stated rule
    /// rather than a swallowed exception.
    /// </summary>
    internal readonly struct StoredRow
    {
        #region Private-Members

        private readonly IDataRecord _Record;
        private readonly StoredValueConverter _Values;
        private readonly string _Entity;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="record">Reader positioned on the row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <param name="entity">Entity name used in messages.</param>
        internal StoredRow(IDataRecord record, StoredValueConverter values, string entity)
        {
            _Record = record ?? throw new ArgumentNullException(nameof(record));
            _Values = values ?? throw new ArgumentNullException(nameof(values));
            _Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Whether the result set carries the column. Used only for a column the schema allows to be absent;
        /// the caller then leaves the model's own default in place.
        /// </summary>
        /// <param name="column">Column name.</param>
        /// <returns>True when the column is present.</returns>
        internal bool Has(string column)
        {
            return OrdinalOf(column) >= 0;
        }

        /// <summary>
        /// Read a text column that is never absent. Null reads as an empty string and an empty string stays empty.
        /// </summary>
        internal string Text(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value) return String.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
        }

        /// <summary>
        /// Read an optional text column. Null and an empty string both read as null.
        /// </summary>
        internal string? NullableText(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value) return null;
            string? text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return String.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// Read an optional text column verbatim. Only null reads as null; an empty string stays empty.
        /// </summary>
        internal string? TextOrNull(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Read a required boolean.
        /// </summary>
        internal bool Bool(string column)
        {
            object value = Required(column);
            try { return _Values.ToBool(value); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a boolean", value, ex); }
        }

        /// <summary>
        /// Read an optional boolean; null reads as null.
        /// </summary>
        internal bool? NullableBool(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value) return null;
            try { return _Values.ToBool(value); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a boolean", value, ex); }
        }

        /// <summary>
        /// Read a required 32-bit integer.
        /// </summary>
        internal int Int(string column)
        {
            object value = Required(column);
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a 32-bit integer", value, ex); }
        }

        /// <summary>
        /// Read an optional 32-bit integer; null reads as null.
        /// </summary>
        internal int? NullableInt(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value) return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a 32-bit integer", value, ex); }
        }

        /// <summary>
        /// Read a required 64-bit integer.
        /// </summary>
        internal long Long(string column)
        {
            object value = Required(column);
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a 64-bit integer", value, ex); }
        }

        /// <summary>
        /// Read an optional 64-bit integer; null reads as null.
        /// </summary>
        internal long? NullableLong(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value) return null;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a 64-bit integer", value, ex); }
        }

        /// <summary>
        /// Read a required double-precision number.
        /// </summary>
        internal double Double(string column)
        {
            object value = Required(column);
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a number", value, ex); }
        }

        /// <summary>
        /// Read a required timestamp as a UTC instant.
        /// </summary>
        internal DateTime Utc(string column)
        {
            object value = Required(column);
            try { return _Values.ToUtc(value); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a timestamp", value, ex); }
        }

        /// <summary>
        /// Read an optional timestamp as a UTC instant. Null and empty text read as null.
        /// </summary>
        internal DateTime? NullableUtc(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value) return null;
            if (value is string text && String.IsNullOrWhiteSpace(text)) return null;
            try { return _Values.ToUtc(value); }
            catch (Exception ex) when (IsConversionFailure(ex)) { throw Unreadable(column, "a timestamp", value, ex); }
        }

        /// <summary>
        /// Read a required enum stored by its member name. The name must match exactly unless
        /// <paramref name="ignoreCase"/> is set, and it must name a defined member.
        /// </summary>
        internal TEnum Enum<TEnum>(string column, bool ignoreCase = false) where TEnum : struct, System.Enum
        {
            object value = Required(column);
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
            if (System.Enum.TryParse(text, ignoreCase, out TEnum parsed) && System.Enum.IsDefined(parsed)) return parsed;
            throw Unreadable(column, "a " + typeof(TEnum).Name + " member", value, null);
        }

        /// <summary>
        /// Read an optional enum stored by its exact member name; null reads as null. Any other text, including
        /// a number or a name in another case, is not a stored member name and throws.
        /// </summary>
        internal TEnum? NullableEnum<TEnum>(string column) where TEnum : struct, System.Enum
        {
            object value = Value(column);
            if (value == DBNull.Value) return null;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
            if (System.Enum.TryParse(text, false, out TEnum parsed) && System.Enum.IsDefined(parsed) && parsed.ToString() == text) return parsed;
            throw Unreadable(column, "a " + typeof(TEnum).Name + " member name", value, null);
        }

        /// <summary>
        /// Read an enum where the schema tolerates values outside the model: the stored name is matched without
        /// regard to case, and null, empty or unrecognised text reads as <paramref name="fallback"/>.
        /// </summary>
        internal TEnum EnumOrFallback<TEnum>(string column, TEnum fallback) where TEnum : struct, System.Enum
        {
            object value = Value(column);
            string text = value == DBNull.Value ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
            return System.Enum.TryParse(text, true, out TEnum parsed) ? parsed : fallback;
        }

        /// <summary>
        /// Read a JSON document column. Null or blank text reads as the type's default; text that is not valid
        /// JSON for the type throws, so a damaged document is never read, and then written back, as empty.
        /// </summary>
        internal T? Json<T>(string column, JsonSerializerOptions options)
        {
            object value = Value(column);
            if (value == DBNull.Value) return default;
            string? text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (String.IsNullOrWhiteSpace(text)) return default;
            try { return JsonSerializer.Deserialize<T>(text, options); }
            catch (JsonException ex) { throw Unreadable(column, "a JSON " + typeof(T).Name, null, ex); }
        }

        #endregion

        #region Private-Methods

        private object Value(string column)
        {
            int ordinal = OrdinalOf(column);
            if (ordinal < 0)
                throw new StoredRowException(_Entity, column, _Values.Provider, "is missing from the result set");
            return _Record.GetValue(ordinal) ?? DBNull.Value;
        }

        private object Required(string column)
        {
            object value = Value(column);
            if (value == DBNull.Value)
                throw new StoredRowException(_Entity, column, _Values.Provider, "is null but the model requires a value");
            return value;
        }

        private int OrdinalOf(string column)
        {
            for (int i = 0; i < _Record.FieldCount; i++)
            {
                if (String.Equals(_Record.GetName(i), column, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }

        private StoredRowException Unreadable(string column, string expected, object? value, Exception? inner)
        {
            string shown = value == null ? String.Empty : " ('" + Convert.ToString(value, CultureInfo.InvariantCulture) + "')";
            return new StoredRowException(_Entity, column, _Values.Provider, "holds a value" + shown + " that is not " + expected, inner);
        }

        private static bool IsConversionFailure(Exception ex)
        {
            return ex is FormatException || ex is InvalidCastException || ex is OverflowException;
        }

        #endregion
    }
}
