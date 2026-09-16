namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Recorded answer to one dispatch-preflight question. The dispatch gate treats an unanswered
    /// question as unmet: an operator must record every answer before the objective is dispatchable.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectivePreflightAnswerEnum
    {
        /// <summary>No answer recorded. The question blocks dispatch until it is answered.</summary>
        Unanswered,

        /// <summary>The question is answered yes.</summary>
        Yes,

        /// <summary>The question is answered no.</summary>
        No
    }
}
