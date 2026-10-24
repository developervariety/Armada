namespace Armada.Runtimes.Harbor
{
    /// <summary>Captured process stream limited to its configured byte budget.</summary>
    internal sealed class HarborWorkspaceStreamResult
    {
        /// <summary>Bytes retained within this stream's configured limit.</summary>
        public byte[] Bytes { get; set; } = System.Array.Empty<byte>();
        /// <summary>Total bytes observed from the process stream.</summary>
        public long TotalBytesRead { get; set; }
        /// <summary>Bytes observed but not retained after the limit was reached.</summary>
        public long OmittedBytes { get; set; }
        /// <summary>Decoded text view of the retained bytes.</summary>
        public string Text { get; set; } = System.String.Empty;
        /// <summary>Whether the stream exceeded its retained byte limit.</summary>
        public bool Truncated { get; set; }
    }
}
