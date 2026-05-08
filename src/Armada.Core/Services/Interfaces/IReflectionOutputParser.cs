namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Memory;

    /// <summary>Pure parser for reflection consolidation captain AgentOutput (fenced reflections-candidate and reflections-diff).</summary>
    public interface IReflectionOutputParser
    {
        /// <summary>Parses AgentOutput markdown; ignores prose outside named fences.</summary>
        ReflectionOutputParseResult Parse(string agentOutput);
    }
}
