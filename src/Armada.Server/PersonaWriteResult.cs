namespace Armada.Server
{
    using Armada.Core.Models;

    /// <summary>
    /// Result of a persona create, update, or delete.
    /// </summary>
    public sealed class PersonaWriteResult
    {
        #region Public-Members

        /// <summary>
        /// What happened.
        /// </summary>
        public PersonaWriteStatusEnum Status { get; set; } = PersonaWriteStatusEnum.Ok;

        /// <summary>
        /// The created or updated persona (the deleted one for a delete), or null on failure.
        /// </summary>
        public Persona? Persona { get; set; } = null;

        /// <summary>
        /// Error message when the status is not Ok.
        /// </summary>
        public string Message { get; set; } = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// A successful result.
        /// </summary>
        /// <param name="persona">The persona.</param>
        /// <returns>The result.</returns>
        public static PersonaWriteResult Success(Persona persona)
        {
            return new PersonaWriteResult { Status = PersonaWriteStatusEnum.Ok, Persona = persona };
        }

        /// <summary>
        /// A failed result.
        /// </summary>
        /// <param name="status">Failure status.</param>
        /// <param name="message">Error message.</param>
        /// <returns>The result.</returns>
        public static PersonaWriteResult Fail(PersonaWriteStatusEnum status, string message)
        {
            return new PersonaWriteResult { Status = status, Message = message };
        }

        #endregion
    }
}
