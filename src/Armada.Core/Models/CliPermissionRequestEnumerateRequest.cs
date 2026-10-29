namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Enumeration query for CLI permission requests (POST /api/v1/cli-permissions/requests/enumerate, MCP enumerate
    /// with entityType cli_permission_requests): paging plus Status (Pending, Allowed, Denied, Expired, or Cancelled),
    /// MissionId, CaptainId, VesselId, and ThreadId.
    /// </summary>
    public class CliPermissionRequestEnumerateRequest : EnumerationQuery
    {
        #region Public-Members

        /// <summary>
        /// Ask thread filter (ath_ prefix).
        /// </summary>
        public string? ThreadId { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CliPermissionRequestEnumerateRequest()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The request filters as a <see cref="CliPermissionRequestQuery"/>.
        /// </summary>
        /// <returns>The filters.</returns>
        /// <exception cref="ArgumentException">Status is not a CliPermissionRequestStatusEnum name.</exception>
        public CliPermissionRequestQuery ToFilter()
        {
            CliPermissionRequestQuery filter = new CliPermissionRequestQuery
            {
                MissionId = Blank(MissionId),
                CaptainId = Blank(CaptainId),
                VesselId = Blank(VesselId),
                ThreadId = Blank(ThreadId)
            };

            string? status = Blank(Status);
            if (status != null)
            {
                if (Int32.TryParse(status, out int _) || !Enum.TryParse<CliPermissionRequestStatusEnum>(status, true, out CliPermissionRequestStatusEnum parsed))
                    throw new ArgumentException("Invalid status: " + status);
                filter.Status = parsed;
            }

            return filter;
        }

        #endregion

        #region Private-Methods

        private static string? Blank(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        #endregion
    }
}
