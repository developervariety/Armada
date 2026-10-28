namespace Armada.Core.Models
{
    /// <summary>Private immutable copy of one retained prior launch instruction file.</summary>
    public sealed class ResearchReplayInstruction
    {
        /// <summary>Original mission identity.</summary>
        public string MissionId { get; set; } = System.String.Empty;
        /// <summary>Filename under the configured instructions directory; never an arbitrary path.</summary>
        public string FileName { get; set; } = System.String.Empty;
        /// <summary>Exact UTF-8 instruction text retained privately inside the database archive.</summary>
        public string Content { get; set; } = System.String.Empty;
        /// <summary>Digest of the original file bytes.</summary>
        public string Sha256 { get; set; } = System.String.Empty;
        /// <summary>Original byte count.</summary>
        public int Utf8Bytes { get; set; }
    }
}
