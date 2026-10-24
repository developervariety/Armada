namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Where Armada stores and operates on a mission checkout.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborCheckoutModeEnum
    {
        /// <summary>Use the existing Admiral-owned local Git worktree.</summary>
        ProcessOnly = 0,

        /// <summary>Use a checkout stored and operated by the named Harbor runner.</summary>
        HarborOwned = 1
    }
}
