namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// A request asked for more items than the limit for that field allows.
    /// </summary>
    public sealed class CollectionLimitExceededException : ArgumentException
    {
        #region Public-Members

        /// <summary>
        /// Machine-readable code for this refusal.
        /// </summary>
        public const string ErrorCode = "CollectionLimitExceeded";

        /// <summary>
        /// Name of the field that exceeded its limit.
        /// </summary>
        public string Field { get; }

        /// <summary>
        /// The most items that field accepts.
        /// </summary>
        public int Limit { get; }

        /// <summary>
        /// How many items the request asked for.
        /// </summary>
        public long Actual { get; }

        /// <summary>
        /// The refusal text, without the parameter suffix an <see cref="ArgumentException"/> adds.
        /// </summary>
        public override string Message => _Message;

        #endregion

        #region Private-Members

        private readonly string _Message;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="field">Field name.</param>
        /// <param name="limit">Allowed maximum.</param>
        /// <param name="actual">Count the request asked for.</param>
        public CollectionLimitExceededException(string field, int limit, long actual)
            : base(Describe(field, limit, actual), field)
        {
            Field = field ?? String.Empty;
            Limit = limit;
            Actual = actual;
            _Message = Describe(field, limit, actual);
        }

        #endregion

        #region Private-Methods

        private static string Describe(string? field, int limit, long actual)
        {
            return (field ?? "collection") + " has " + actual + " items; the most allowed is " + limit + ".";
        }

        #endregion
    }
}
