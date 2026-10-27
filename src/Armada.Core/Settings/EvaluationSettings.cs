namespace Armada.Core.Settings
{
    /// <summary>
    /// Budget and concurrency for evaluation runs (Evaluate), separate from production missions
    /// (docs/MODEL_HETEROGENEITY.md, Decision 9). Token figures are estimates (characters divided by four). All values
    /// apply live.
    /// </summary>
    public class EvaluationSettings
    {
        #region Public-Members

        /// <summary>
        /// Estimated tokens one run may spend. A probe that would exceed it is recorded as could-not-run. Default 250000,
        /// minimum 1000, maximum 100000000; out-of-range values are clamped.
        /// </summary>
        public long RunTokenBudget
        {
            get => _RunTokenBudget;
            set => _RunTokenBudget = Clamp(value, 1000, 100000000);
        }

        /// <summary>
        /// Estimated tokens all evaluation runs together may spend in any 24 hours; 0 means no limit. Default 2000000,
        /// maximum 10000000000; negative values become 0.
        /// </summary>
        public long DailyTokenBudget
        {
            get => _DailyTokenBudget;
            set => _DailyTokenBudget = Clamp(value, 0, 10000000000);
        }

        /// <summary>
        /// Estimated tokens the evaluation runs of one inference host may spend in any 24 hours; 0 means no limit.
        /// Default 1000000, maximum 10000000000; negative values become 0.
        /// </summary>
        public long DailyTokenBudgetPerHost
        {
            get => _DailyTokenBudgetPerHost;
            set => _DailyTokenBudgetPerHost = Clamp(value, 0, 10000000000);
        }

        /// <summary>
        /// Evaluation runs that may probe at once across all hosts. Default 2, minimum 1, maximum 32; out-of-range values
        /// are clamped.
        /// </summary>
        public int MaxConcurrentRuns
        {
            get => _MaxConcurrentRuns;
            set => _MaxConcurrentRuns = (int)Clamp(value, 1, 32);
        }

        /// <summary>
        /// Evaluation runs that may probe one inference host at once. Default 1, so models that share one machine (for
        /// example several Ollama models on one GB10) are evaluated one at a time. Minimum 1, maximum 32; out-of-range
        /// values are clamped.
        /// </summary>
        public int MaxConcurrentRunsPerHost
        {
            get => _MaxConcurrentRunsPerHost;
            set => _MaxConcurrentRunsPerHost = (int)Clamp(value, 1, 32);
        }

        /// <summary>
        /// Longest time one probe exchange (one model call through the runtime) may take, in seconds. Default 600,
        /// minimum 30, maximum 7200; out-of-range values are clamped.
        /// </summary>
        public int ProbeTimeoutSeconds
        {
            get => _ProbeTimeoutSeconds;
            set => _ProbeTimeoutSeconds = (int)Clamp(value, 30, 7200);
        }

        #endregion

        #region Private-Members

        private long _RunTokenBudget = 250000;
        private long _DailyTokenBudget = 2000000;
        private long _DailyTokenBudgetPerHost = 1000000;
        private int _MaxConcurrentRuns = 2;
        private int _MaxConcurrentRunsPerHost = 1;
        private int _ProbeTimeoutSeconds = 600;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public EvaluationSettings()
        {
        }

        #endregion

        #region Private-Methods

        private static long Clamp(long value, long min, long max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        #endregion
    }
}
