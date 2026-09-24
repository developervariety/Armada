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
    }
}
