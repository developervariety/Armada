namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments for reading one page of persisted mission output.
    /// </summary>
    public sealed class MissionOutputArgs
    {
        /// <summary>Mission ID.</summary>
        public string MissionId { get; set; } = String.Empty;
        /// <summary>Zero-based UTF-16 character offset.</summary>
        public int Offset { get; set; }
        /// <summary>Requested UTF-16 character count.</summary>
        public int Length { get; set; } = 16000;
    }
}
