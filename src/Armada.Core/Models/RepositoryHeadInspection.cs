namespace Armada.Core.Models
{
    /// <summary>Verified read-only repository HEAD inspection.</summary>
    public class RepositoryHeadInspection
    {
        /// <summary>True when HEAD is a verified detached commit.</summary>
        public bool IsDetached { get; set; }

        /// <summary>Symbolic HEAD ref when one exists.</summary>
        public string? HeadRef { get; set; }
    }
}
