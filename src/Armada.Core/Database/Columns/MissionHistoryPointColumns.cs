namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The mission history-point projection (created time, status and vessel of a mission row), shared by every
    /// provider.
    /// </summary>
    internal static class MissionHistoryPointColumns
    {
        /// <summary>
        /// Read a mission history-point row.
        /// </summary>
        /// <param name="record">Reader positioned on a row selecting created_utc, status and vessel_id.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The history point.</returns>
        internal static MissionHistoryPoint Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "MissionHistoryPoint");
            return new MissionHistoryPoint
            {
                CreatedUtc = row.Utc("created_utc"),
                Status = row.Enum<MissionStatusEnum>("status"),
                VesselId = row.NullableText("vessel_id")
            };
        }
    }
}
