namespace Armada.Runtimes.Harbor
{
    using System;
    using Armada.Core.Harbor;

    /// <summary>One in-memory supervisor launch envelope. Callers must not persist or log this value.</summary>
    public sealed class HarborSupervisorStart
    {
        /// <summary>Create an empty envelope for typed JSON deserialization.</summary>
        public HarborSupervisorStart()
        {
            Options = new HarborSupervisorOptions();
            Request = new HarborLaunchRequest();
        }

        /// <summary>Create the options and fully resolved launch request for one supervisor process.</summary>
        /// <param name="options">Local supervisor policy.</param>
        /// <param name="request">Resolved launch request, including its private prompt.</param>
        public HarborSupervisorStart(HarborSupervisorOptions options, HarborLaunchRequest request)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }

        /// <summary>Local supervisor policy.</summary>
        public HarborSupervisorOptions Options { get; set; }

        /// <summary>Resolved launch request. Do not write it to durable storage or logs.</summary>
        public HarborLaunchRequest Request { get; set; }
    }
}
