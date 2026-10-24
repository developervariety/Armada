namespace Armada.Core.Harbor
{
    using System.Collections.Generic;

    /// <summary>Runner proof for jobs retained across a link or Admiral restart.</summary>
    public sealed class HarborResumeRequest : HarborMessage
    {
        /// <summary>Job proofs retained by this runner.</summary>
        public List<HarborResumeJobInfo> Jobs { get; set; } = new List<HarborResumeJobInfo>();
    }
}
