namespace Armada.LoadGenerator
{
    /// <summary>Minimal retention settings contract, containing no secrets.</summary>
    public class LoadGeneratorRetention
    {
        /// <summary>Mission/event/signal retention; zero disables expiry.</summary>
        public int? DataRetentionDays { get; set; }
        /// <summary>Request retention; zero disables expiry.</summary>
        public int? RequestHistoryRetentionDays { get; set; }
    }
}
