namespace Armada.Core.Services
{
    /// <summary>A candidate includes developer commits that are not on the remote target.</summary>
    public sealed class GitUnpublishedCommitsException : InvalidOperationException
    {
        /// <summary>Describe why publication is unsafe.</summary>
        public GitUnpublishedCommitsException(string message) : base(message) { }
    }
}
