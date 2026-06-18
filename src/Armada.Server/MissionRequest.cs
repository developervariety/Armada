namespace Armada.Server
{
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using SyslogLogging;
    using Voltaic;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server.Mcp;
    using Armada.Server.WebSocket;

    /// <summary>
    /// Request model for a mission within a voyage request.
    /// </summary>
    public class MissionRequest
    {
        /// <summary>
        /// Mission title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Mission description.
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Optional file-copy operations performed before captain launch.
        /// </summary>
        public List<PrestagedFile>? PrestagedFiles { get; set; } = null;

        /// <summary>
        /// Optional per-mission code context mode.
        /// </summary>
        public string? CodeContextMode { get; set; } = null;

        /// <summary>
        /// Optional per-mission code context query.
        /// </summary>
        public string? CodeContextQuery { get; set; } = null;

        /// <summary>
        /// Optional preferred model or complexity tier.
        /// </summary>
        public string? PreferredModel { get; set; } = null;

        /// <summary>
        /// Optional concrete mission dependency id.
        /// </summary>
        public string? DependsOnMissionId { get; set; } = null;

        /// <summary>
        /// Optional logical alias for this mission within the dispatch batch.
        /// </summary>
        public string? Alias { get; set; } = null;

        /// <summary>
        /// Optional logical alias dependency within the dispatch batch.
        /// </summary>
        public string? DependsOnMissionAlias { get; set; } = null;

        /// <summary>
        /// Optional per-mission playbook selections.
        /// </summary>
        public List<SelectedPlaybook>? SelectedPlaybooks { get; set; } = null;
    }
}
