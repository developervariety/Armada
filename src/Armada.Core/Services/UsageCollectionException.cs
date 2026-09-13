namespace Armada.Core.Services
{
    using System;
    using System.IO;

    /// <summary>A safe collector failure code and optional polling deadline; never contains response bodies or credentials.</summary>
    public sealed class UsageCollectionException : IOException
    {
        /// <summary>Machine-readable failure code.</summary>
        public string Code { get; }
        /// <summary>Earliest next collection attempt.</summary>
        public DateTime? RetryAfterUtc { get; }
        /// <summary>Create a collection failure.</summary>
        public UsageCollectionException(string code, DateTime? retryAfterUtc = null) : base(code)
        {
            Code = code;
            RetryAfterUtc = retryAfterUtc;
        }
    }
}
