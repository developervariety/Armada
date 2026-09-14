namespace Armada.Core.Harbor
{
    using System.Text.Json.Serialization;

    /// <summary>Lifecycle state of a job the Admiral launched on a Harbor runner.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborJobStateEnum
    {
        /// <summary>Launch sent; the runner has not reported a started process.</summary>
        Pending,

        /// <summary>The owning runner reported a started process.</summary>
        Running,

        /// <summary>A stop was sent to the owning runner.</summary>
        Stopping,

        /// <summary>The owning runner reported a process exit.</summary>
        Exited,

        /// <summary>The job failed before a normal exit.</summary>
        Failed,

        /// <summary>The owning runner can no longer report this job.</summary>
        Lost
    }
}
