namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The voyage_playbooks selection projection, shared by every provider. A delivery mode outside the model reads
    /// as inline full content.
    /// </summary>
    internal static class VoyagePlaybookColumns
    {
        /// <summary>
        /// Read a voyage_playbooks row selecting playbook_id and delivery_mode.
        /// </summary>
        /// <param name="record">Reader positioned on a voyage_playbooks row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The selection.</returns>
        internal static SelectedPlaybook Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "VoyagePlaybook");
            return new SelectedPlaybook
            {
                PlaybookId = row.Text("playbook_id"),
                DeliveryMode = row.EnumOrFallback("delivery_mode", PlaybookDeliveryModeEnum.InlineFullContent)
            };
        }
    }
}
