namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>Pure parser over an Architect captain's AgentOutput markdown.</summary>
    public interface IArchitectOutputParser
    {
        /// <summary>Parses the Architect captain's AgentOutput. Returns a structured result with a verdict.</summary>
        ArchitectParseResult Parse(string agentOutput);
    }
}
