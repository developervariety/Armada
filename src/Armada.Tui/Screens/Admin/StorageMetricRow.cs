namespace Armada.Tui.Screens.Admin
{
    /// <summary>One human-readable storage measurement in the terminal inventory.</summary>
    public class StorageMetricRow
    {
        /// <summary>Measurement group.</summary>
        public string Group { get; set; } = "";
        /// <summary>Host or provider.</summary>
        public string Host { get; set; } = "";
        /// <summary>Path, table or policy name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Formatted amount, with units or accuracy.</summary>
        public string Amount { get; set; } = "";
        /// <summary>Measurement state.</summary>
        public string Status { get; set; } = "";
        /// <summary>Detailed explanation shown on activation.</summary>
        public string Detail { get; set; } = "";
    }
}
