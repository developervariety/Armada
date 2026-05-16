namespace Armada.Test.Common
{
    using Armada.Core.Models;

    public class TenantPairResult
    {
        public string TenantA { get; set; } = string.Empty;

        public string TenantB { get; set; } = string.Empty;
    }

    public class TenantUserResult
    {
        public string TenantId { get; set; } = string.Empty;

        public string UserId { get; set; } = string.Empty;
    }

    public class OperationalGraphResult
    {
        public TenantMetadata Tenant { get; set; } = null!;

        public UserMaster User { get; set; } = null!;

        public Credential Credential { get; set; } = null!;

        public Fleet Fleet { get; set; } = null!;

        public Vessel Vessel { get; set; } = null!;

        public Captain Captain { get; set; } = null!;

        public Voyage Voyage { get; set; } = null!;

        public Mission Mission { get; set; } = null!;

        public Dock Dock { get; set; } = null!;

        public Signal Signal { get; set; } = null!;

        public ArmadaEvent Event { get; set; } = null!;

        public MergeEntry MergeEntry { get; set; } = null!;
    }
}
