namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;

    /// <summary>One point in a Harbor host series. <see cref="Missing"/> and a null field are gaps, not zeros.</summary>
    public sealed class HarborHostMetricPoint
    {
        /// <summary>UTC time of the sample, or of the gap marker.</summary>
        public DateTime AtUtc { get; set; }

        /// <summary>True when this point is a gap. Every measurement on a gap is null.</summary>
        public bool Missing { get; set; }

        /// <summary>CPU percent, or null when that measurement is absent.</summary>
        public double? CpuPercent { get; set; }

        /// <summary>Memory in use, in bytes, or null.</summary>
        public long? MemoryUsedBytes { get; set; }

        /// <summary>Memory size, in bytes, or null.</summary>
        public long? MemoryTotalBytes { get; set; }

        /// <summary>Disk bytes in use, or null.</summary>
        public long? DiskUsedBytes { get; set; }

        /// <summary>Disk size, in bytes, or null.</summary>
        public long? DiskTotalBytes { get; set; }

        /// <summary>Live jobs, or null.</summary>
        public int? ActiveJobs { get; set; }

        /// <summary>Live captain processes, or null.</summary>
        public int? ActiveCaptains { get; set; }
    }

    /// <summary>
    /// In-memory host series for one runner. <see cref="State"/> is <see cref="StateNoData"/>, <see cref="StateCurrent"/>,
    /// or <see cref="StateStale"/>. A stale series ends in a gap and names <see cref="StaleSinceUtc"/>.
    /// </summary>
    public sealed class HarborHostMetricsSnapshot
    {
        /// <summary>No stored sample. This is not an error and it is not a zero reading.</summary>
        public const string StateNoData = "no-data";

        /// <summary>The newest sample is inside the fresh window.</summary>
        public const string StateCurrent = "current";

        /// <summary>The newest sample is older than the fresh window.</summary>
        public const string StateStale = "stale";

        /// <summary>Runner the series belongs to.</summary>
        public string RunnerId { get; set; } = String.Empty;

        /// <summary><see cref="StateNoData"/>, <see cref="StateCurrent"/>, or <see cref="StateStale"/>.</summary>
        public string State { get; set; } = StateNoData;

        /// <summary>UTC time of the last real sample when <see cref="State"/> is <see cref="StateStale"/>.</summary>
        public DateTime? StaleSinceUtc { get; set; }

        /// <summary>UTC time of the newest real sample, when one is stored.</summary>
        public DateTime? NewestSampleUtc { get; set; }

        /// <summary>Real samples and gap markers, oldest first.</summary>
        public List<HarborHostMetricPoint> Points { get; set; } = new List<HarborHostMetricPoint>();
    }

    /// <summary>
    /// Fixed ring of recent host samples for each Harbor runner. The ring keeps at most
    /// <see cref="DefaultMaximumSamples"/> samples and drops a sample older than <see cref="DefaultMaximumAge"/>.
    /// A null or empty reading is not stored. A stored zero is a measurement.
    /// </summary>
    public sealed class HarborHostMetricsBuffer
    {
        /// <summary>Samples retained for one runner.</summary>
        public const int DefaultMaximumSamples = 120;

        /// <summary>Oldest sample age the ring retains.</summary>
        public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromHours(24);

        /// <summary>Age after which the newest sample is stale and the series ends in a gap.</summary>
        public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(90);

        private readonly object _Gate = new object();
        private readonly Dictionary<string, List<Sample>> _Rings = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
        private readonly Func<DateTime> _Clock;
        private readonly int _MaximumSamples;
        private readonly TimeSpan _MaximumAge;
        private readonly TimeSpan _StaleAfter;

        /// <summary>Create a ring with the production limits.</summary>
        /// <param name="clock">UTC clock. Null uses <see cref="DateTime.UtcNow"/>.</param>
        /// <param name="maximumSamples">Samples kept for one runner. Null uses <see cref="DefaultMaximumSamples"/>.</param>
        /// <param name="maximumAge">Age cap. Null uses <see cref="DefaultMaximumAge"/>.</param>
        /// <param name="staleAfter">Fresh window. Null uses <see cref="DefaultStaleAfter"/>.</param>
        public HarborHostMetricsBuffer(Func<DateTime>? clock = null, int? maximumSamples = null, TimeSpan? maximumAge = null, TimeSpan? staleAfter = null)
        {
            _Clock = clock ?? (() => DateTime.UtcNow);
            _MaximumSamples = maximumSamples ?? DefaultMaximumSamples;
            _MaximumAge = maximumAge ?? DefaultMaximumAge;
            _StaleAfter = staleAfter ?? DefaultStaleAfter;
            if (_MaximumSamples < 1) throw new ArgumentOutOfRangeException(nameof(maximumSamples));
            if (_MaximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
            if (_StaleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }

        /// <summary>
        /// Store one reading for a runner. A null reading, or a reading with no measured field, changes nothing.
        /// </summary>
        /// <param name="runnerId">Authenticated runner identifier.</param>
        /// <param name="sample">Host reading from that runner, or null when the heartbeat omitted it.</param>
        /// <returns>True when a sample was stored.</returns>
        public bool Record(string runnerId, HarborHostSample? sample)
        {
            if (String.IsNullOrWhiteSpace(runnerId) || sample == null) return false;
            Sample stored = Sanitize(sample, Utc(_Clock()));
            if (!stored.HasMeasurement) return false;
            string id = runnerId.Trim();
            lock (_Gate)
            {
                if (!_Rings.TryGetValue(id, out List<Sample>? ring))
                {
                    ring = new List<Sample>();
                    _Rings[id] = ring;
                }
                Purge(ring, stored.AtUtc);
                ring.Add(stored);
                while (ring.Count > _MaximumSamples) ring.RemoveAt(0);
            }
            return true;
        }

        /// <summary>Read the series for a runner. An unknown or expired runner is <see cref="HarborHostMetricsSnapshot.StateNoData"/>.</summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <returns>Series with gaps where a sample is late. Gap fields are null.</returns>
        public HarborHostMetricsSnapshot Read(string runnerId)
        {
            HarborHostMetricsSnapshot snapshot = new HarborHostMetricsSnapshot { RunnerId = runnerId ?? String.Empty };
            if (String.IsNullOrWhiteSpace(runnerId)) return snapshot;
            string id = runnerId.Trim();
            DateTime now = Utc(_Clock());
            List<Sample> kept;
            lock (_Gate)
            {
                if (!_Rings.TryGetValue(id, out List<Sample>? ring)) return snapshot;
                Purge(ring, now);
                if (ring.Count == 0)
                {
                    _Rings.Remove(id);
                    return snapshot;
                }
                kept = new List<Sample>(ring);
            }

            List<HarborHostMetricPoint> points = new List<HarborHostMetricPoint> { ToPoint(kept[0]) };
            for (int index = 1; index < kept.Count; index++)
            {
                if (kept[index].AtUtc - kept[index - 1].AtUtc > _StaleAfter)
                    points.Add(Gap(kept[index - 1].AtUtc + _StaleAfter));
                points.Add(ToPoint(kept[index]));
            }

            Sample newest = kept[kept.Count - 1];
            snapshot.NewestSampleUtc = newest.AtUtc;
            snapshot.Points = points;
            if (now - newest.AtUtc > _StaleAfter)
            {
                snapshot.State = HarborHostMetricsSnapshot.StateStale;
                snapshot.StaleSinceUtc = newest.AtUtc;
                points.Add(Gap(now));
            }
            else
            {
                snapshot.State = HarborHostMetricsSnapshot.StateCurrent;
            }
            return snapshot;
        }

        private void Purge(List<Sample> ring, DateTime now)
        {
            DateTime oldest = now - _MaximumAge;
            ring.RemoveAll(sample => sample.AtUtc < oldest);
        }

        private static Sample Sanitize(HarborHostSample sample, DateTime atUtc)
        {
            return new Sample
            {
                AtUtc = atUtc,
                CpuPercent = Cpu(sample.CpuPercent),
                MemoryUsedBytes = Bytes(sample.MemoryUsedBytes),
                MemoryTotalBytes = Bytes(sample.MemoryTotalBytes),
                DiskUsedBytes = Bytes(sample.DiskUsedBytes),
                DiskTotalBytes = Bytes(sample.DiskTotalBytes),
                ActiveJobs = Count(sample.ActiveJobs),
                ActiveCaptains = Count(sample.ActiveCaptains)
            };
        }

        private static double? Cpu(double? value)
        {
            if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value)) return null;
            if (value.Value < 0d || value.Value > 100d) return null;
            return value.Value;
        }

        private static long? Bytes(long? value)
        {
            if (!value.HasValue || value.Value < 0) return null;
            return value.Value;
        }

        private static int? Count(int? value)
        {
            if (!value.HasValue || value.Value < 0) return null;
            return value.Value;
        }

        private static HarborHostMetricPoint ToPoint(Sample sample)
        {
            return new HarborHostMetricPoint
            {
                AtUtc = sample.AtUtc,
                Missing = false,
                CpuPercent = sample.CpuPercent,
                MemoryUsedBytes = sample.MemoryUsedBytes,
                MemoryTotalBytes = sample.MemoryTotalBytes,
                DiskUsedBytes = sample.DiskUsedBytes,
                DiskTotalBytes = sample.DiskTotalBytes,
                ActiveJobs = sample.ActiveJobs,
                ActiveCaptains = sample.ActiveCaptains
            };
        }

        private static HarborHostMetricPoint Gap(DateTime atUtc)
        {
            return new HarborHostMetricPoint { AtUtc = atUtc, Missing = true };
        }

        private static DateTime Utc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            if (value.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value;
        }

        private sealed class Sample
        {
            public DateTime AtUtc;
            public double? CpuPercent;
            public long? MemoryUsedBytes;
            public long? MemoryTotalBytes;
            public long? DiskUsedBytes;
            public long? DiskTotalBytes;
            public int? ActiveJobs;
            public int? ActiveCaptains;

            public bool HasMeasurement
            {
                get
                {
                    return CpuPercent.HasValue || MemoryUsedBytes.HasValue || MemoryTotalBytes.HasValue
                        || DiskUsedBytes.HasValue || DiskTotalBytes.HasValue || ActiveJobs.HasValue || ActiveCaptains.HasValue;
                }
            }
        }
    }
}
