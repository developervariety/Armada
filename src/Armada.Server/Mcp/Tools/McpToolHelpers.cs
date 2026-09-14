namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>
    /// Shared helper methods used by MCP tool registration classes.
    /// </summary>
    public static class McpToolHelpers
    {
        /// <summary>
        /// Checks whether a mission status transition is valid.
        /// </summary>
        /// <param name="current">Current mission status.</param>
        /// <param name="target">Target mission status.</param>
        /// <returns>True if the transition is allowed; otherwise, false.</returns>
        public static bool IsValidTransition(MissionStatusEnum current, MissionStatusEnum target)
        {
            // Delegated to the single authoritative table so the MCP transition tool and the
            // services agree. A local copy here omitted every PullRequestOpen transition, so the
            // operator tool rejected the PR-fallback flow as an invalid transition.
            return MissionStateMachine.IsValidTransition(current, target);
        }


        /// <summary>
        /// Read a text file safely, allowing concurrent writes from other processes.
        /// </summary>
        public static async Task<string> ReadTextFileSafeAsync(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new StreamReader(fs);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Read a log file safely as lines, allowing concurrent writes from other processes.
        /// </summary>
        public static async Task<string[]> ReadLogFileSafeAsync(string path)
        {
            string content = await ReadTextFileSafeAsync(path).ConfigureAwait(false);
            return content.Split('\n');
        }


    }
}
