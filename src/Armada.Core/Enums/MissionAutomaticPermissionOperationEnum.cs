namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Operator changes to the optional automatic-work barrier.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MissionAutomaticPermissionOperationEnum
    {
        /// <summary>Opt in with both actions held; preserve any existing grants.</summary>
        Enable,

        /// <summary>Grant exactly one action for a bounded current launch and source.</summary>
        Grant,

        /// <summary>Remove exactly one grant without affecting another action.</summary>
        Revoke
    }
}
