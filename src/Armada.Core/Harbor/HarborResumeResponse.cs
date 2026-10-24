namespace Armada.Core.Harbor
{
    using System.Collections.Generic;

    /// <summary>Server response to a typed runner resume request.</summary>
    public sealed class HarborResumeResponse : HarborMessage
    {
        /// <summary>Per-job resume decisions.</summary>
        public List<HarborResumeJobResult> Jobs { get; set; } = new List<HarborResumeJobResult>();
    }
}
