namespace Armada.Core.Memory
{
    /// <summary>Single error emitted when parsing reflection AgentOutput violates the contract.</summary>
    /// <param name="Type">Machine-readable error category (for example duplicate_fence or missing_fence).</param>
    /// <param name="Message">Human-readable detail.</param>
    public sealed record ReflectionOutputParseError(string Type, string Message);
}
