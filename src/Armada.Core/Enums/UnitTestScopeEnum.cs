namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Which of a workflow profile's unit-test commands to run.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum UnitTestScopeEnum
    {
        /// <summary>
        /// The full unit-test command (<c>UnitTestCommand</c>).
        /// </summary>
        Full = 0,

        /// <summary>
        /// The abbreviated unit-test command (<c>AbbreviatedUnitTestCommand</c>), falling back to the full one when unset.
        /// </summary>
        Abbreviated = 1
    }
}
