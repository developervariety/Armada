namespace Armada.Core.Services.TypedDecisions
{
    using System;

    /// <summary>Result of a bounded event-scoped retained-sample lookup.</summary>
    public sealed class TypedDecisionSampleLookupResult
    {
        /// <summary>Create an unavailable result until a factory selects an outcome.</summary>
        private TypedDecisionSampleLookupResult()
        {
        }

        /// <summary>Lookup state: available, not_found, scan_incomplete, or unavailable.</summary>
        public string Availability { get; private set; } = "not_found";

        /// <summary>The validated retained sample when availability is available.</summary>
        public TypedDecisionSample? Sample { get; private set; }

        /// <summary>Create a successful result with a validated sample.</summary>
        /// <param name="sample">The validated retained sample.</param>
        /// <returns>A result with availability set to available.</returns>
        public static TypedDecisionSampleLookupResult Found(TypedDecisionSample sample)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            return new TypedDecisionSampleLookupResult { Availability = "available", Sample = sample };
        }

        /// <summary>Create a complete lookup result with no matching sample.</summary>
        /// <returns>A result with availability set to not_found.</returns>
        public static TypedDecisionSampleLookupResult NotFound()
        {
            return new TypedDecisionSampleLookupResult { Availability = "not_found" };
        }

        /// <summary>Create a result when a bounded scan could not establish availability.</summary>
        /// <returns>A result with availability set to scan_incomplete.</returns>
        public static TypedDecisionSampleLookupResult ScanIncomplete()
        {
            return new TypedDecisionSampleLookupResult { Availability = "scan_incomplete" };
        }

        /// <summary>Create a result when the matching sample fails validation.</summary>
        /// <returns>A result with availability set to unavailable.</returns>
        public static TypedDecisionSampleLookupResult UnsafeSample()
        {
            return new TypedDecisionSampleLookupResult { Availability = "unavailable" };
        }
    }
}
