namespace Armada.Core.Models
{
    using System;

    /// <summary>Compact typed evidence for a provider request failure.</summary>
    public sealed record RuntimeProviderError
    {
        private string? _ErrorType;

        /// <summary>HTTP status code reported by the provider or runtime transport.</summary>
        public int? HttpStatusCode { get; init; }

        /// <summary>Provider error type or code, bounded to 128 characters.</summary>
        public string? ErrorType
        {
            get => _ErrorType;
            init => _ErrorType = String.IsNullOrEmpty(value) || value.Length <= 128
                ? value
                : value.Substring(0, 128);
        }
    }
}
