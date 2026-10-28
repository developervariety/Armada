namespace Armada.Core.Harbor
{
    using System.Text.Json.Serialization;

    /// <summary>Bounded operations on a configured runner repository or an owned checkout.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborWorkspaceOperationEnum
    {
        /// <summary>InspectRepository operation.</summary>
        InspectRepository,
        /// <summary>ProvisionCheckout operation.</summary>
        ProvisionCheckout,
        /// <summary>InspectCheckout operation.</summary>
        InspectCheckout,
        /// <summary>Git operation.</summary>
        Git,
        /// <summary>Stat operation.</summary>
        Stat,
        /// <summary>ReadText operation.</summary>
        ReadText,
        /// <summary>WriteText operation.</summary>
        WriteText,
        /// <summary>ReadBytes operation.</summary>
        ReadBytes,
        /// <summary>WriteBytes operation.</summary>
        WriteBytes,
        /// <summary>EnumerateFiles operation.</summary>
        EnumerateFiles,
        /// <summary>DeleteFile operation.</summary>
        DeleteFile,
        /// <summary>RemoveCheckout operation.</summary>
        RemoveCheckout,
        /// <summary>RunVerification operation.</summary>
        RunVerification,
        /// <summary>AddGitExclude operation.</summary>
        AddGitExclude,
        /// <summary>Read one bounded chunk from a completed verification output spool.</summary>
        ReadVerificationOutput,
        /// <summary>Run one allowlisted Codex inventory command on the runner host.</summary>
        ProbeCodexInventory,
    }
}
