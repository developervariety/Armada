namespace Armada.Server
{
    /// <summary>The typed-decision provider key to store. The value is written to the key file and never returned.</summary>
    public sealed class TypedDecisionKeyRequest
    {
        /// <summary>The provider API key.</summary>
        public string? ApiKey { get; set; }
    }
}
