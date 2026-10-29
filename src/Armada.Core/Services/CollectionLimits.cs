namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The size limits a single request must stay inside. A refusal names the field, the limit and the
    /// count the request asked for. These limits do not change the shape of a successful response.
    /// </summary>
    public static class CollectionLimits
    {
        #region Public-Members

        /// <summary>The smallest page a caller may ask for.</summary>
        public const int MinPageSize = 1;

        /// <summary>The largest page a caller may ask for.</summary>
        public const int MaxPageSize = 1000;

        /// <summary>The page size used when a caller does not set one.</summary>
        public const int DefaultPageSize = 10;

        /// <summary>
        /// The most identifiers, missions or chat messages one request may carry.
        /// </summary>
        public const int MaxRequestItems = 1000;

        /// <summary>
        /// The most entries one configuration list may carry. Dispatch playbook selections and an
        /// endpoint's allowed-tool list use this limit.
        /// </summary>
        public const int MaxConfigItems = 256;

        /// <summary>The most linked identifiers one request may carry.</summary>
        public const int MaxLinkedIds = 1000;

        /// <summary>
        /// The most time buckets one summary window may ask a response to fill.
        /// </summary>
        public const int MaxSummaryBuckets = 1000;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Refuse when a list is longer than its limit. A null list is within every limit.
        /// </summary>
        /// <typeparam name="T">Item type.</typeparam>
        /// <param name="items">The list.</param>
        /// <param name="field">Field name reported on the refusal.</param>
        /// <param name="limit">The most items allowed.</param>
        public static void Require<T>(ICollection<T>? items, string field, int limit)
        {
            if (items == null) return;
            RequireCount(items.Count, field, limit);
        }

        /// <summary>
        /// Refuse when a count is over its limit.
        /// </summary>
        /// <param name="count">How many items the request asked for.</param>
        /// <param name="field">Field name reported on the refusal.</param>
        /// <param name="limit">The most items allowed.</param>
        public static void RequireCount(long count, string field, int limit)
        {
            if (count > limit)
                throw new CollectionLimitExceededException(field, limit, count);
        }

        /// <summary>
        /// Refuse when an identifier list is longer than <see cref="MaxRequestItems"/>.
        /// A sequence that is not a collection is counted without being stored again. A single-pass
        /// sequence is consumed by that count, so callers pass a list.
        /// </summary>
        /// <param name="ids">Identifiers the request asked to delete.</param>
        /// <param name="field">Field name reported on the refusal.</param>
        public static void RequireRequestItems(IEnumerable<string?>? ids, string field)
        {
            if (ids == null) return;
            if (ids is ICollection<string?> nullable)
            {
                RequireCount(nullable.Count, field, MaxRequestItems);
                return;
            }
            if (ids is ICollection<string> values)
            {
                RequireCount(values.Count, field, MaxRequestItems);
                return;
            }

            long count = 0;
            foreach (string? _ in ids)
            {
                count++;
                if (count > MaxRequestItems)
                    throw new CollectionLimitExceededException(field, MaxRequestItems, count);
            }
        }

        /// <summary>
        /// Refuse a dispatch whose mission list or playbook selections are over their limits.
        /// Each playbook list is judged on its own: the voyage list, then each mission list.
        /// </summary>
        /// <param name="missions">Missions the dispatch would create.</param>
        /// <param name="selectedPlaybooks">Playbooks selected for the voyage.</param>
        public static void RequireDispatch(ICollection<MissionDescription>? missions, ICollection<SelectedPlaybook>? selectedPlaybooks)
        {
            Require(missions, "Missions", MaxRequestItems);
            Require(selectedPlaybooks, "SelectedPlaybooks", MaxConfigItems);
            if (missions == null) return;
            foreach (MissionDescription mission in missions)
            {
                if (mission?.SelectedPlaybooks == null) continue;
                Require(mission.SelectedPlaybooks, "SelectedPlaybooks", MaxConfigItems);
            }
        }

        /// <summary>
        /// Refuse a summary window whose inclusive bucket fill would pass <see cref="MaxSummaryBuckets"/>.
        /// The count uses the same epoch grid and bucket width as the summary builders. A window whose
        /// end is before its floored start asks for no buckets.
        /// </summary>
        /// <param name="fromUtc">Window start.</param>
        /// <param name="toUtc">Window end, inclusive of the bucket that contains it.</param>
        /// <param name="bucketMinutes">Bucket width in minutes. A non-positive width is one minute.</param>
        public static void RequireInclusiveBuckets(DateTime fromUtc, DateTime toUtc, double bucketMinutes)
        {
            double minutes = bucketMinutes > 0 ? bucketMinutes : 1;
            DateTime cursor = TimeBucketGrid.FloorUtc(fromUtc, minutes);
            DateTime end = toUtc.ToUniversalTime();
            if (end < cursor) return;

            long bucketTicks = (long)(minutes * TimeSpan.TicksPerMinute);
            if (bucketTicks < 1) bucketTicks = 1;
            long count = ((end.Ticks - cursor.Ticks) / bucketTicks) + 1;
            RequireCount(count, "Buckets", MaxSummaryBuckets);
        }

        /// <summary>
        /// Refuse a mission-history window whose exclusive bucket fill would pass
        /// <see cref="MaxSummaryBuckets"/>. The count uses the same tick step as the mission-history
        /// summary: the bucket width, and never less than one minute.
        /// </summary>
        /// <param name="fromUtc">Window start.</param>
        /// <param name="toUtc">Window end. A bucket that starts at this instant is not included.</param>
        /// <param name="bucketMinutes">Bucket width in minutes. A non-positive width is 60 minutes.</param>
        public static void RequireMissionHistoryBuckets(DateTime fromUtc, DateTime toUtc, int bucketMinutes)
        {
            int minutes = bucketMinutes > 0 ? bucketMinutes : 60;
            long bucketTicks = Math.Max(TimeSpan.FromMinutes(minutes).Ticks, TimeSpan.FromMinutes(1).Ticks);
            DateTime from = fromUtc.Kind == DateTimeKind.Utc ? fromUtc : fromUtc.ToUniversalTime();
            DateTime to = toUtc.Kind == DateTimeKind.Utc ? toUtc : toUtc.ToUniversalTime();
            long startTicks = (from.Ticks / bucketTicks) * bucketTicks;
            long span = to.Ticks - startTicks;
            if (span < 0) span = 0;
            RequireCount(span / bucketTicks, "Buckets", MaxSummaryBuckets);
        }

        #endregion
    }
}
