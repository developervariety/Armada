namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>One read-only repository health criterion.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RepositoryHealthCriterionEnum
    {
        /// <summary>A configured bare repository or working checkout is a Git repository.</summary>
        RepositoryReachable,

        /// <summary>The vessel default branch resolves to a commit.</summary>
        DefaultBranch,

        /// <summary>The working checkout is present, clean, and not behind the remote default branch.</summary>
        WorkingCheckout,

        /// <summary>The bare repository and origin have not diverged.</summary>
        BareOrigin,

        /// <summary>An active workflow profile resolves for the vessel.</summary>
        WorkflowProfile,

        /// <summary>Recent check results.</summary>
        RecentChecks
    }
}
