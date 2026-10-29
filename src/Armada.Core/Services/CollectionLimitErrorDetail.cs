namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Machine-readable body of a collection-limit refusal.
    /// </summary>
    public sealed class CollectionLimitErrorDetail
    {
        #region Public-Members

        /// <summary>
        /// Always <see cref="CollectionLimitExceededException.ErrorCode"/>.
        /// </summary>
        public string Code { get; set; } = CollectionLimitExceededException.ErrorCode;

        /// <summary>
        /// Name of the field that exceeded its limit.
        /// </summary>
        public string Field { get; set; } = String.Empty;

        /// <summary>
        /// The most items that field accepts.
        /// </summary>
        public int Limit { get; set; }

        /// <summary>
        /// How many items the request asked for.
        /// </summary>
        public long Actual { get; set; }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy the fields of a limit exception.
        /// </summary>
        /// <param name="exception">The refusal.</param>
        /// <returns>The detail a transport returns beside the message.</returns>
        public static CollectionLimitErrorDetail FromException(CollectionLimitExceededException exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            return new CollectionLimitErrorDetail
            {
                Code = CollectionLimitExceededException.ErrorCode,
                Field = exception.Field,
                Limit = exception.Limit,
                Actual = exception.Actual
            };
        }

        #endregion
    }
}
