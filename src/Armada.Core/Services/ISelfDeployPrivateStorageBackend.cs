namespace Armada.Core.Services
{
    using System.IO;

    /// <summary>
    /// Platform rule for owner-only private storage. Every operation either leaves the path verified as
    /// owner-only or throws <see cref="SelfDeployPrivateStorageException"/> with a stable reason.
    /// </summary>
    internal interface ISelfDeployPrivateStorageBackend
    {
        /// <summary>Stable backend name.</summary>
        string Name { get; }

        /// <summary>Create one missing directory level with owner-only access.</summary>
        /// <param name="path">Directory to create; its parent exists.</param>
        void CreatePrivateDirectory(string path);

        /// <summary>Verify an existing directory is owner-only without changing it.</summary>
        /// <param name="path">Directory to verify.</param>
        void VerifyPrivateDirectory(string path);

        /// <summary>Create a new file with owner-only access and return it open for writing.</summary>
        /// <param name="path">File to create; must not exist.</param>
        /// <returns>Writable stream.</returns>
        FileStream CreatePrivateFile(string path);

        /// <summary>Apply and verify owner-only access on an existing file.</summary>
        /// <param name="path">File to restrict.</param>
        void RestrictFile(string path);
    }
}
