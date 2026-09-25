namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;

    /// <summary>
    /// The parameters of one command that addresses one table. A parameter is named "@" plus its column unless a
    /// name is given; every value is sent with an explicit type in the stored form of its column.
    /// </summary>
    internal sealed class StoredParameters
    {
        #region Private-Members

        private readonly StoredValueBinder _Binder;
        private readonly DbCommand _Command;
        private readonly string _Table;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="binder">Provider binder.</param>
        /// <param name="command">Command to add parameters to.</param>
        /// <param name="table">Table the parameters address.</param>
        internal StoredParameters(StoredValueBinder binder, DbCommand command, string table)
        {
            _Binder = binder ?? throw new ArgumentNullException(nameof(binder));
            _Command = command ?? throw new ArgumentNullException(nameof(command));
            _Table = table ?? throw new ArgumentNullException(nameof(table));
        }

        #endregion

        #region Internal-Methods

        /// <summary>Bind text; null binds as a database null.</summary>
        internal StoredParameters Text(string column, string? value) => Text("@" + column, column, value);

        /// <summary>Bind text under a parameter name that differs from its column.</summary>
        internal StoredParameters Text(string parameterName, string column, string? value)
        {
            StoredValueBinder.Add(_Command, parameterName, DbType.String, value);
            return this;
        }

        /// <summary>Bind a 32-bit integer; null binds as a database null.</summary>
        internal StoredParameters Int(string column, int? value) => Int("@" + column, column, value);

        /// <summary>Bind a 32-bit integer under a parameter name that differs from its column.</summary>
        internal StoredParameters Int(string parameterName, string column, int? value)
        {
            StoredValueBinder.Add(_Command, parameterName, DbType.Int32, value);
            return this;
        }

        /// <summary>Bind a 64-bit integer; null binds as a database null.</summary>
        internal StoredParameters Long(string column, long? value) => Long("@" + column, column, value);

        /// <summary>Bind a 64-bit integer under a parameter name that differs from its column.</summary>
        internal StoredParameters Long(string parameterName, string column, long? value)
        {
            StoredValueBinder.Add(_Command, parameterName, DbType.Int64, value);
            return this;
        }

        /// <summary>Bind a boolean in the column's stored form; null binds as a database null.</summary>
        internal StoredParameters Bool(string column, bool? value) => Bool("@" + column, column, value);

        /// <summary>Bind a boolean in the stored form of a column under a parameter name that differs from it.</summary>
        internal StoredParameters Bool(string parameterName, string column, bool? value)
        {
            _Binder.AddBoolean(_Command, parameterName, _Table, column, value);
            return this;
        }

        /// <summary>Bind a timestamp in the column's stored form; null binds as a database null.</summary>
        internal StoredParameters Utc(string column, DateTime? value) => Utc("@" + column, column, value);

        /// <summary>Bind a timestamp in the stored form of a column under a parameter name that differs from it.</summary>
        internal StoredParameters Utc(string parameterName, string column, DateTime? value)
        {
            _Binder.AddTimestamp(_Command, parameterName, _Table, column, value);
            return this;
        }

        #endregion
    }
}
