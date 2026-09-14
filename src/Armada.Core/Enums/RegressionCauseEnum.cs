namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Classified cause of a post-land regression record.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RegressionCauseEnum
    {
        /// <summary>The cause has not been classified.</summary>
        Unclassified = 0,

        /// <summary>The landed change named by the record caused the regression.</summary>
        LandedChange = 1,

        /// <summary>The failure existed before the landed change.</summary>
        PreExisting = 2,

        /// <summary>The failure came from the environment, not from the landed change.</summary>
        Environment = 3,

        /// <summary>The record was investigated and is not a regression.</summary>
        NotRegression = 4
    }
}
