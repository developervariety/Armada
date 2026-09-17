namespace Armada.Test.Common
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    /// <summary>
    /// Deterministic assignment of a test executable's suites to shards.
    /// </summary>
    /// <remarks>
    /// Every serial suite goes to shard 1, so suites that share state beyond their own process never run at
    /// the same time as each other. The remaining suites are placed heaviest first on the shard with the
    /// least expected time, ties broken by suite name and then by the lowest shard index. The same registered
    /// suites, weights and serial list therefore always produce the same split, and every suite lands on
    /// exactly one shard.
    /// </remarks>
    public class SuiteShardPlan
    {
        #region Public-Members

        /// <summary>Expected seconds for a suite the weight table does not name.</summary>
        public double DefaultSeconds { get; }

        /// <summary>Suite name to expected seconds.</summary>
        public IReadOnlyDictionary<string, double> Weights => _Weights;

        /// <summary>Suite name to the reason it is pinned to shard 1.</summary>
        public IReadOnlyDictionary<string, string> SerialSuites => _SerialSuites;

        #endregion

        #region Private-Members

        private Dictionary<string, double> _Weights;
        private Dictionary<string, string> _SerialSuites;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a plan from a weight table and a serial list.</summary>
        public SuiteShardPlan(ShardWeightsFile weights, SerialSuitesFile serial)
        {
            ArgumentNullException.ThrowIfNull(weights);
            ArgumentNullException.ThrowIfNull(serial);
            DefaultSeconds = IsUsableWeight(weights.DefaultSeconds) ? weights.DefaultSeconds : 1.0;
            _Weights = new Dictionary<string, double>(weights.Suites ?? new Dictionary<string, double>(), StringComparer.Ordinal);
            _SerialSuites = new Dictionary<string, string>(serial.Suites ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in _SerialSuites)
            {
                if (String.IsNullOrWhiteSpace(entry.Value))
                    throw new InvalidOperationException("Serial suite '" + entry.Key + "' has no recorded reason.");
            }
        }

        /// <summary>
        /// Load the weight table and serial list. A missing file is an error: a shard run without them would
        /// balance blindly and could separate serial suites.
        /// </summary>
        public static SuiteShardPlan Load(string weightsPath, string serialPath)
        {
            ShardWeightsFile weights = ReadJson<ShardWeightsFile>(weightsPath);
            SerialSuitesFile serial = ReadJson<SerialSuitesFile>(serialPath);
            return new SuiteShardPlan(weights, serial);
        }

        #endregion

        #region Public-Methods

        /// <summary>Expected seconds for one suite.</summary>
        public double WeightOf(string suiteName)
        {
            if (_Weights.TryGetValue(suiteName, out double seconds) && IsUsableWeight(seconds)) return seconds;
            return DefaultSeconds;
        }

        /// <summary>
        /// Split the registered suite names across <paramref name="shardCount"/> shards. Element 0 is shard 1.
        /// Each shard keeps the registration order of its suites. Throws when a name is duplicated or when the
        /// serial list names a suite that is not registered, so a rename cannot silently unpin a serial suite.
        /// </summary>
        public List<List<string>> Assign(IReadOnlyList<string> suiteNames, int shardCount)
        {
            ArgumentNullException.ThrowIfNull(suiteNames);
            if (shardCount < 1) throw new ArgumentOutOfRangeException(nameof(shardCount), "Shard count must be at least 1.");

            HashSet<string> registered = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in suiteNames)
            {
                if (!registered.Add(name))
                    throw new InvalidOperationException("Duplicate suite name cannot be sharded: " + name);
            }

            List<string> unknownSerial = _SerialSuites.Keys.Where(name => !registered.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
            if (unknownSerial.Count > 0)
                throw new InvalidOperationException("Serial suite list names suites that are not registered: " + String.Join(", ", unknownSerial));

            Dictionary<string, int> assignment = new Dictionary<string, int>(StringComparer.Ordinal);
            double[] loads = new double[shardCount];
            foreach (string name in suiteNames)
            {
                if (!_SerialSuites.ContainsKey(name)) continue;
                assignment[name] = 0;
                loads[0] += WeightOf(name);
            }

            List<string> balanced = suiteNames
                .Where(name => !_SerialSuites.ContainsKey(name))
                .OrderByDescending(name => WeightOf(name))
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToList();
            foreach (string name in balanced)
            {
                int lightest = 0;
                for (int i = 1; i < shardCount; i++)
                {
                    if (loads[i] < loads[lightest]) lightest = i;
                }

                assignment[name] = lightest;
                loads[lightest] += WeightOf(name);
            }

            List<List<string>> shards = new List<List<string>>();
            for (int i = 0; i < shardCount; i++) shards.Add(new List<string>());
            foreach (string name in suiteNames) shards[assignment[name]].Add(name);
            return shards;
        }

        #endregion

        #region Private-Methods

        private static bool IsUsableWeight(double seconds)
        {
            return !Double.IsNaN(seconds) && !Double.IsInfinity(seconds) && seconds >= 0;
        }

        private static T ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Shard plan file is missing: " + path, path);
            T? value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            if (value == null) throw new InvalidOperationException("Shard plan file is empty: " + path);
            return value;
        }

        #endregion
    }
}
