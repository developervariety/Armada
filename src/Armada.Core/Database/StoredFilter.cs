namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;

    /// <summary>
    /// The conditions of one statement over one table and the parameters they bind. A condition is added only when
    /// its filter value is present, in the order the method set adds them, so the generated text is stable. Every
    /// value binds in the stored form of the column it tests.
    /// </summary>
    internal sealed class StoredFilter
    {
        private readonly List<string> _Conditions = new List<string>();
        private readonly List<Action<StoredParameters>> _Binds = new List<Action<StoredParameters>>();

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="table">Table the conditions test.</param>
        internal StoredFilter(string table)
        {
            if (String.IsNullOrWhiteSpace(table)) throw new ArgumentNullException(nameof(table));
            Table = table;
        }

        /// <summary>
        /// Table the conditions test.
        /// </summary>
        internal string Table { get; }

        /// <summary>
        /// Conditions in the order they were added.
        /// </summary>
        internal IReadOnlyList<string> Conditions => _Conditions;

        /// <summary>
        /// The conditions joined with AND, without the WHERE keyword.
        /// </summary>
        internal string Conjunction => String.Join(" AND ", _Conditions);

        /// <summary>
        /// A WHERE clause with a leading space, or an empty string when there are no conditions.
        /// </summary>
        internal string Where => _Conditions.Count > 0 ? " WHERE " + Conjunction : String.Empty;

        /// <summary>
        /// Require a text column to equal a value; skipped when the value is null or blank.
        /// </summary>
        internal StoredFilter Text(string column, string? value) => Text(column, "@" + column, value);

        /// <summary>
        /// Require a text column to equal a value bound under a named parameter; skipped when the value is null or blank.
        /// </summary>
        internal StoredFilter Text(string column, string parameterName, string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return this;
            return Condition(column + " = " + parameterName, p => p.Text(parameterName, column, value));
        }

        /// <summary>
        /// Require a column that stores an enumeration name to equal a value; skipped when the value is absent.
        /// </summary>
        internal StoredFilter Name<TEnum>(string column, TEnum? value) where TEnum : struct, Enum
        {
            if (!value.HasValue) return this;
            string name = value.Value.ToString();
            return Condition(column + " = @" + column, p => p.Text("@" + column, column, name));
        }

        /// <summary>
        /// Require an integer column to equal a value; skipped when the value is absent.
        /// </summary>
        internal StoredFilter Int(string column, int? value)
        {
            if (!value.HasValue) return this;
            int number = value.Value;
            return Condition(column + " = @" + column, p => p.Int("@" + column, column, number));
        }

        /// <summary>
        /// Require a boolean column to equal a value bound under a named parameter; skipped when the value is absent.
        /// </summary>
        internal StoredFilter Bool(string column, string parameterName, bool? value)
        {
            if (!value.HasValue) return this;
            bool flag = value.Value;
            return Condition(column + " = " + parameterName, p => p.Bool(parameterName, column, flag));
        }

        /// <summary>
        /// Require a timestamp column to compare with an instant; skipped when the instant is absent.
        /// </summary>
        /// <param name="column">Timestamp column.</param>
        /// <param name="comparison">Comparison operator, such as &gt;= or &lt;.</param>
        /// <param name="parameterName">Parameter the instant binds under.</param>
        /// <param name="value">Instant.</param>
        internal StoredFilter Time(string column, string comparison, string parameterName, DateTime? value)
        {
            if (!value.HasValue) return this;
            DateTime instant = value.Value;
            return Condition(column + " " + comparison + " " + parameterName, p => p.Utc(parameterName, column, instant));
        }

        /// <summary>
        /// Add a condition whose text and parameters the method set writes itself.
        /// </summary>
        /// <param name="condition">Condition text.</param>
        /// <param name="bind">Binds the parameters the condition names; null when it names none.</param>
        internal StoredFilter Condition(string condition, Action<StoredParameters>? bind)
        {
            if (String.IsNullOrWhiteSpace(condition)) throw new ArgumentNullException(nameof(condition));
            _Conditions.Add(condition);
            if (bind != null) _Binds.Add(bind);
            return this;
        }

        /// <summary>
        /// Bind every parameter the conditions name to a command.
        /// </summary>
        internal void Bind(DbCommand command, StoredValueBinder binder)
        {
            StoredParameters parameters = binder.For(command, Table);
            foreach (Action<StoredParameters> bind in _Binds) bind(parameters);
        }
    }
}
