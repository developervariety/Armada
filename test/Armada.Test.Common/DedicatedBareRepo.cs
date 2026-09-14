namespace Armada.Test.Common
{
    using System;

    /// <summary>
    /// A bare repository created for one test, with the commit at its main branch.
    /// </summary>
    public sealed class DedicatedBareRepo
    {
        /// <summary>
        /// Absolute path to the bare repository.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Full commit identifier at refs/heads/main.
        /// </summary>
        public string HeadCommit { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="path">Absolute path to the bare repository.</param>
        /// <param name="headCommit">Full commit identifier at refs/heads/main.</param>
        public DedicatedBareRepo(string path, string headCommit)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            HeadCommit = headCommit ?? throw new ArgumentNullException(nameof(headCommit));
        }

        /// <summary>
        /// file:// URL for the bare repository, suitable for a vessel RepoUrl.
        /// </summary>
        public string Url => "file:///" + Path.Replace("\\", "/");
    }
}
