namespace Armada.Core.Enums
{
    /// <summary>
    /// The kind of response produced by the Ask Armada assistant.
    /// </summary>
    public enum AskResponseKindEnum
    {
        /// <summary>
        /// A direct answer to a recognized question.
        /// </summary>
        Answer = 0,

        /// <summary>
        /// A help/capabilities listing.
        /// </summary>
        Help = 1,

        /// <summary>
        /// The question was not understood.
        /// </summary>
        Unknown = 2
    }
}
