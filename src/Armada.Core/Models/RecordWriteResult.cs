namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Result of a create, update or delete of a configuration record through its shared service.
    /// </summary>
    /// <typeparam name="T">Record type.</typeparam>
    public class RecordWriteResult<T> where T : class
    {
        #region Public-Members

        /// <summary>
        /// Outcome of the write.
        /// </summary>
        public RecordWriteOutcomeEnum Outcome { get; set; } = RecordWriteOutcomeEnum.Succeeded;

        /// <summary>
        /// The stored record after a successful create or update, or the deleted record after a delete.
        /// Null for every refusal.
        /// </summary>
        public T? Record { get; set; } = null;

        /// <summary>
        /// Human-readable reason for a refusal. Null on success.
        /// </summary>
        public string? Message { get; set; } = null;

        /// <summary>
        /// Stable machine-readable code for a refusal that has one, such as a retired field. Null otherwise.
        /// </summary>
        public string? ErrorCode { get; set; } = null;

        /// <summary>
        /// Structured detail for a refusal, such as the validation result that refused it. Null otherwise.
        /// </summary>
        public object? Details { get; set; } = null;

        /// <summary>
        /// True when the write was applied.
        /// </summary>
        public bool Succeeded => Outcome == RecordWriteOutcomeEnum.Succeeded;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// A successful write.
        /// </summary>
        /// <param name="record">Stored record.</param>
        /// <returns>Result.</returns>
        public static RecordWriteResult<T> Success(T record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return new RecordWriteResult<T> { Outcome = RecordWriteOutcomeEnum.Succeeded, Record = record };
        }

        /// <summary>
        /// A refusal.
        /// </summary>
        /// <param name="outcome">Refusal outcome.</param>
        /// <param name="message">Reason.</param>
        /// <param name="errorCode">Optional stable code.</param>
        /// <returns>Result.</returns>
        public static RecordWriteResult<T> Refuse(RecordWriteOutcomeEnum outcome, string message, string? errorCode = null)
        {
            if (outcome == RecordWriteOutcomeEnum.Succeeded) throw new ArgumentException("A refusal needs a refusing outcome.", nameof(outcome));
            return new RecordWriteResult<T> { Outcome = outcome, Message = message, ErrorCode = errorCode };
        }

        /// <summary>
        /// A refusal for a missing or invalid field.
        /// </summary>
        /// <param name="message">Reason.</param>
        /// <param name="errorCode">Optional stable code.</param>
        /// <returns>Result.</returns>
        public static RecordWriteResult<T> Invalid(string message, string? errorCode = null)
        {
            return Refuse(RecordWriteOutcomeEnum.Invalid, message, errorCode);
        }

        /// <summary>
        /// A refusal for a record the caller cannot read or that does not exist.
        /// </summary>
        /// <param name="message">Reason.</param>
        /// <returns>Result.</returns>
        public static RecordWriteResult<T> NotFound(string message)
        {
            return Refuse(RecordWriteOutcomeEnum.NotFound, message);
        }

        /// <summary>
        /// A refusal for a record the caller may read but not change.
        /// </summary>
        /// <param name="message">Reason.</param>
        /// <returns>Result.</returns>
        public static RecordWriteResult<T> Forbidden(string message)
        {
            return Refuse(RecordWriteOutcomeEnum.Forbidden, message);
        }

        /// <summary>
        /// A refusal for a write that would duplicate a unique record.
        /// </summary>
        /// <param name="message">Reason.</param>
        /// <returns>Result.</returns>
        public static RecordWriteResult<T> Conflict(string message)
        {
            return Refuse(RecordWriteOutcomeEnum.Conflict, message);
        }

        #endregion
    }
}
