namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Reverse index over declared sibling repositories: given a producer vessel, finds the
    /// vessels that consume it.
    /// </summary>
    /// <remarks>
    /// A vessel declares the repositories it depends ON, via <see cref="Vessel.SiblingRepos"/>.
    /// That edge is the one docks need, because a dock provisions what the consumer must compile
    /// against. A gate needs the edge pointing the other way: when a producer changes a public
    /// API, the breakage lands in whoever consumes it, and the producer's own build cannot see it.
    /// Nothing else records that direction, so it is derived here by scanning every vessel's
    /// declarations for one that names the producer.
    /// <para>
    /// The declaration identifies its source by <see cref="SiblingRepo.VesselRef"/>, which the
    /// model defines as a vessel ID or a vessel name, so both are matched. A vessel never
    /// consumes itself: a self-referential declaration is ignored rather than treated as an edge,
    /// because building a vessel against itself proves nothing and would double every gate.
    /// </para>
    /// </remarks>
    public static class ConsumerVesselResolver
    {
        #region Public-Methods

        /// <summary>
        /// Find every vessel that declares the specified producer as a sibling repository.
        /// </summary>
        /// <param name="producerVesselId">Vessel ID of the producer whose consumers are wanted.</param>
        /// <param name="producerVesselName">
        /// Optional vessel name of the producer. Declarations may reference a vessel by name
        /// instead of ID, so both are matched when supplied.
        /// </param>
        /// <param name="allVessels">Every known vessel, including the producer itself.</param>
        /// <returns>
        /// One entry per consuming vessel, carrying the declaration that names the producer.
        /// Empty when nothing consumes the producer, which is the common single-repo case.
        /// </returns>
        public static IReadOnlyList<ConsumerDeclaration> Resolve(
            string producerVesselId,
            string? producerVesselName,
            IEnumerable<Vessel>? allVessels)
        {
            List<ConsumerDeclaration> consumers = new List<ConsumerDeclaration>();
            if (String.IsNullOrWhiteSpace(producerVesselId)) return consumers;
            if (allVessels == null) return consumers;

            foreach (Vessel candidate in allVessels)
            {
                if (candidate == null) continue;

                // A self-declaration is not a consumer edge.
                if (String.Equals(candidate.Id, producerVesselId, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (SiblingRepo sibling in candidate.GetSiblingRepos())
                {
                    if (sibling == null) continue;
                    if (String.IsNullOrWhiteSpace(sibling.RelativePath)) continue;
                    if (!ReferencesProducer(sibling.VesselRef, producerVesselId, producerVesselName)) continue;

                    consumers.Add(new ConsumerDeclaration(candidate, sibling));
                    break; // One edge per consumer is enough; a repeated declaration is still one build.
                }
            }

            return consumers;
        }

        #endregion

        #region Private-Methods

        private static bool ReferencesProducer(string? vesselRef, string producerVesselId, string? producerVesselName)
        {
            if (String.IsNullOrWhiteSpace(vesselRef)) return false;

            string trimmed = vesselRef.Trim();
            if (String.Equals(trimmed, producerVesselId, StringComparison.OrdinalIgnoreCase)) return true;

            if (!String.IsNullOrWhiteSpace(producerVesselName)
                && String.Equals(trimmed, producerVesselName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        #endregion
    }
}
