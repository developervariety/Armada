namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The home an operator gave one Judge follow-up item.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum JudgeFollowUpDispositionKindEnum
    {
        /// <summary>A fix objective owns the item; the disposition is the objective id.</summary>
        Objective,

        /// <summary>The owner decided the item; the disposition states the decision.</summary>
        Decision,

        /// <summary>A recorded note closes the item; the disposition is the note.</summary>
        Note
    }
}
