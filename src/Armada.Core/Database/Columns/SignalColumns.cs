namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The signals row-to-model contract, shared by every provider.
    /// </summary>
    internal static class SignalColumns
    {
        /// <summary>
        /// Read a signals row.
        /// </summary>
        /// <param name="record">Reader positioned on a signals row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The signal.</returns>
        internal static Signal Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Signal");
            Signal signal = new Signal();
            signal.Id = row.Text("id");
            signal.TenantId = row.NullableText("tenant_id");
            signal.UserId = row.NullableText("user_id");
            signal.FromCaptainId = row.NullableText("from_captain_id");
            signal.ToCaptainId = row.NullableText("to_captain_id");
            signal.Type = row.Enum<SignalTypeEnum>("type");
            signal.Payload = row.NullableText("payload");
            signal.Read = row.Bool("read");
            signal.CreatedUtc = row.Utc("created_utc");
            return signal;
        }

        /// <summary>
        /// Bind every stored signals column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the signals table.</param>
        /// <param name="signal">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Signal signal)
        {
            parameters
                .Text("id", signal.Id)
                .Text("tenant_id", signal.TenantId)
                .Text("user_id", signal.UserId)
                .Text("from_captain_id", signal.FromCaptainId)
                .Text("to_captain_id", signal.ToCaptainId)
                .Text("type", signal.Type.ToString())
                .Text("payload", signal.Payload)
                .Bool("read", signal.Read)
                .Utc("created_utc", signal.CreatedUtc);
        }
    }
}
