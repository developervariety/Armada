namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One consumer edge: a vessel that declares some producer vessel as a sibling repository,
    /// paired with the declaration that names it.
    /// </summary>
    /// <remarks>
    /// The declaration is carried alongside the vessel because verifying the consumer needs both:
    /// the vessel supplies the repository and the workflow profile to build, and the declaration
    /// supplies the relative path the consumer's build probes expect the producer to occupy.
    /// </remarks>
    public class ConsumerDeclaration
    {
        #region Public-Members

        /// <summary>
        /// The vessel that consumes the producer.
        /// </summary>
        public Vessel Consumer { get; }

        /// <summary>
        /// The sibling declaration on <see cref="Consumer"/> that names the producer, giving the
        /// relative path at which the producer must be materialized.
        /// </summary>
        public SiblingRepo Declaration { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate a consumer edge.
        /// </summary>
        /// <param name="consumer">The consuming vessel.</param>
        /// <param name="declaration">The declaration on that vessel naming the producer.</param>
        public ConsumerDeclaration(Vessel consumer, SiblingRepo declaration)
        {
            Consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
            Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        }

        #endregion
    }
}
