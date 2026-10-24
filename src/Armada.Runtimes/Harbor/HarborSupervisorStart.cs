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
            RepositoryGrant = null;
        }

        /// <summary>Create the options and fully resolved launch request for one supervisor process.</summary>
        /// <param name="options">Local supervisor policy.</param>
        /// <param name="request">Resolved launch request, including its private prompt.</param>
        /// <param name="repositoryGrant">Selected checkout grant, or null for a mapped launch.</param>
        public HarborSupervisorStart(HarborSupervisorOptions options, HarborLaunchRequest request, HarborRepositoryGrant? repositoryGrant = null)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Request = request ?? throw new ArgumentNullException(nameof(request));
            RepositoryGrant = repositoryGrant;
        }

        /// <summary>Local supervisor policy.</summary>
        public HarborSupervisorOptions Options { get; set; }

        /// <summary>Resolved launch request. Do not write it to durable storage or logs.</summary>
        public HarborLaunchRequest Request { get; set; }

        /// <summary>The one configured grant selected by the pinned checkout binding, or null for mapped launches.</summary>
        public HarborRepositoryGrant? RepositoryGrant { get; set; }
    }
}
