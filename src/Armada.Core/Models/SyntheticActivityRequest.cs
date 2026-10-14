namespace Armada.Core.Models
{
    using System;

    /// <summary>Bounded screenshot activity generation settings. Use identical settings to resume a batch.</summary>
    public class SyntheticActivityRequest
    {
        /// <summary>Operator-provided batch slug, 1-64 letters, digits, underscores or hyphens.</summary>
        public string BatchId { get; set; } = "";
        /// <summary>Historical days, default 42; allowed 1-90.</summary>
        public int Days { get; set; } = 42;
        /// <summary>Mean record density multiplier, default 1; allowed 0.1-5.</summary>
        public double Density { get; set; } = 1;
        /// <summary>Random variation strength, default 1; allowed 0-2. Zero gives uniform counts.</summary>
        public double Entropy { get; set; } = 1;
        /// <summary>Random seed. Identical seed and window reproduce the same records.</summary>
        public int Seed { get; set; }
        /// <summary>Fixed UTC window end, required; no future values. Preserve for retries.</summary>
        public DateTime ToUtc { get; set; }
    }
}
