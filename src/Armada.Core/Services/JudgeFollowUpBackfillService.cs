namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>Repairs missed durable Judge follow-up captures over a bounded time range.</summary>
    public sealed class JudgeFollowUpBackfillService
    {
        private readonly DatabaseDriver _Database;
        private readonly JudgeFollowUpService _FollowUps;

        /// <summary>Result of one bounded backfill pass.</summary>
        public sealed class Result
        {
            /// <summary>Number of mission pages read.</summary>
            public int PagesScanned { get; set; }
            /// <summary>Number of mission rows read.</summary>
            public int MissionsScanned { get; set; }
            /// <summary>Terminal Judge outputs that contain the required section.</summary>
            public int JudgeOutputsWithSection { get; set; }
            /// <summary>Sections that contain actionable text.</summary>
            public int Actionable { get; set; }
            /// <summary>Sections that contain the explicit no-work sentinel.</summary>
            public int ExplicitNone { get; set; }
            /// <summary>Canonical rows that existed before the pass.</summary>
            public int AlreadyPresent { get; set; }
            /// <summary>Rows a dry run would create.</summary>
            public int WouldCreate { get; set; }
            /// <summary>Rows created by a write pass.</summary>
            public int Created { get; set; }
            /// <summary>Per-mission capture failures.</summary>
            public int Errors { get; set; }
            /// <summary>True when the page bound stopped the scan before its end.</summary>
            public bool Incomplete { get; set; }
            /// <summary>Judge mission identifiers created by the pass.</summary>
            public List<string> CreatedMissionIds { get; set; } = new List<string>();
            /// <summary>Judge mission identifiers that failed capture.</summary>
            public List<string> ErrorMissionIds { get; set; } = new List<string>();
        }

        /// <summary>Instantiate.</summary>
        public JudgeFollowUpBackfillService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _FollowUps = new JudgeFollowUpService(database, logging ?? throw new ArgumentNullException(nameof(logging)));
        }

        /// <summary>
        /// Scan terminal missions in ascending creation order. The maximum page count prevents an
        /// accidental unbounded operator request; <see cref="Result.Incomplete"/> is true if data remains.
        /// </summary>
        public async Task<Result> RunAsync(
            DateTime fromUtc,
            DateTime toUtc,
            bool dryRun,
            int maxPages,
            CancellationToken token = default)
        {
            if (toUtc <= fromUtc) throw new ArgumentException("toUtc must be later than fromUtc");
            maxPages = Math.Clamp(maxPages, 1, 1000);
            Result result = new Result();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            for (int pageNumber = 1; pageNumber <= maxPages; pageNumber++)
            {
                token.ThrowIfCancellationRequested();
                EnumerationResult<Mission> page = await _Database.Missions.EnumerateAsync(new EnumerationQuery
                {
                    PageNumber = pageNumber,
                    PageSize = 500,
                    Order = EnumerationOrderEnum.CreatedAscending,
                    CreatedAfter = fromUtc,
                    CreatedBefore = toUtc
                }, token).ConfigureAwait(false);
                result.PagesScanned++;
                result.MissionsScanned += page.Objects.Count;

                foreach (Mission mission in page.Objects)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(mission.Id)
                        || !String.Equals(PersonaCatalog.NormalizeName(mission.Persona), PersonaCatalog.Judge, StringComparison.Ordinal)
                        || !IsTerminal(mission.Status)) continue;

                    JudgeOutputParser.FollowUpSection section = JudgeOutputParser.ParseSuggestedFollowUps(mission.AgentOutput);
                    if (!section.Present) continue;
                    result.JudgeOutputsWithSection++;
                    if (section.ExplicitNone) result.ExplicitNone++;
                    else if (!String.IsNullOrWhiteSpace(section.Body)) result.Actionable++;

                    try
                    {
                        JudgeFollowUp? existing = await _Database.JudgeFollowUps
                            .ReadByJudgeMissionAsync(mission.Id, token).ConfigureAwait(false);
                        if (existing != null)
                        {
                            result.AlreadyPresent++;
                            continue;
                        }

                        if (dryRun)
                        {
                            result.WouldCreate++;
                        }
                        else
                        {
                            await _FollowUps.CaptureAsync(
                                mission,
                                JudgeOutputParser.ParseVerdictLabel(mission),
                                section.Body,
                                token).ConfigureAwait(false);
                            result.Created++;
                            result.CreatedMissionIds.Add(mission.Id);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        result.Errors++;
                        result.ErrorMissionIds.Add(mission.Id);
                    }
                }

                if (pageNumber >= page.TotalPages || page.Objects.Count == 0) return result;
            }

            result.Incomplete = true;
            return result;
        }

        private static bool IsTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.LandingFailed
                || status == MissionStatusEnum.Cancelled;
        }
    }
}
