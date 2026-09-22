namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>Outcome of reloading live settings from the settings file they are bound to.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SettingsReloadOutcomeEnum
    {
        /// <summary>The candidate passed validation and its runtime-tunable values were applied.</summary>
        [EnumMember(Value = "Applied")]
        Applied,

        /// <summary>The file content equals the content last applied, so nothing was applied again.</summary>
        [EnumMember(Value = "Unchanged")]
        Unchanged,

        /// <summary>The bound settings file does not exist; the current settings were kept.</summary>
        [EnumMember(Value = "FileMissing")]
        FileMissing,

        /// <summary>The candidate could not be read or failed validation; the current settings were kept.</summary>
        [EnumMember(Value = "Invalid")]
        Invalid
    }
}
