namespace Armada.Test.Common
{
    using System;
    using System.Globalization;

    /// <summary>
    /// One slice of a test executable's suites, written on the command line as <c>index/count</c> with a
    /// 1-based index.
    /// </summary>
    public class TestShard
    {
        #region Public-Members

        /// <summary>1-based shard index, at least 1 and at most <see cref="Count"/>.</summary>
        public int Index { get; }

        /// <summary>Number of shards the suites are split across, at least 1.</summary>
        public int Count { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a shard; the index is 1-based.</summary>
        public TestShard(int index, int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "Shard count must be at least 1.");
            if (index < 1 || index > count) throw new ArgumentOutOfRangeException(nameof(index), "Shard index must be between 1 and the shard count.");
            Index = index;
            Count = count;
        }

        /// <summary>Parse <c>index/count</c>; malformed or out-of-range values are an <see cref="ArgumentException"/>.</summary>
        public static TestShard Parse(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException("--shard requires a value of the form <index>/<count>");
            string[] parts = value.Trim().Split('/');
            if (parts.Length != 2
                || !Int32.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                || count < 1 || index < 1 || index > count)
            {
                throw new ArgumentException("--shard must be <index>/<count> with 1 <= index <= count, got: " + value);
            }

            return new TestShard(index, count);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public override string ToString()
        {
            return Index.ToString(CultureInfo.InvariantCulture) + "/" + Count.ToString(CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
