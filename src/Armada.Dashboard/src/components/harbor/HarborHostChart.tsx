import type { HarborHostMetricPoint, HarborHostMetrics } from '../../types/models';

type Reading = (point: HarborHostMetricPoint) => number | null;

/** Split a series on a missing point or a null field. A gap is not inserted as zero. */
export function seriesSegments(points: HarborHostMetricPoint[], value: Reading): number[][] {
  const segments: number[][] = [];
  let current: number[] = [];
  for (const point of points) {
    const reading = point.missing ? null : value(point);
    if (reading == null || !Number.isFinite(reading)) {
      if (current.length > 0) segments.push(current);
      current = [];
      continue;
    }
    current.push(reading);
  }
  if (current.length > 0) segments.push(current);
  return segments;
}

function percentUsed(used: number | null, total: number | null): number | null {
  if (used == null || total == null || total <= 0) return null;
  return (used / total) * 100;
}

const series: { name: string; value: Reading }[] = [
  { name: 'CPU percent', value: (point) => point.cpuPercent },
  { name: 'Memory percent', value: (point) => percentUsed(point.memoryUsedBytes, point.memoryTotalBytes) },
  { name: 'Disk percent', value: (point) => percentUsed(point.diskUsedBytes, point.diskTotalBytes) },
  { name: 'Active jobs', value: (point) => point.activeJobs },
  { name: 'Active captains', value: (point) => point.activeCaptains },
];

export default function HarborHostChart({
  reading,
  formatDateTime = (value: string) => value,
}: {
  reading: HarborHostMetrics;
  formatDateTime?: (value: string) => string;
}) {
  if (reading.state === 'no-data' || reading.points.length === 0) {
    return <p role="status">No host metrics for this Harbor. A Harbor that sends none leaves this chart empty.</p>;
  }

  return (
    <div>
      {reading.state === 'stale' && reading.staleSinceUtc && (
        <p role="status">
          Stale since <time dateTime={reading.staleSinceUtc}>{formatDateTime(reading.staleSinceUtc)}</time>
        </p>
      )}
      {series.map((item) => (
        <HostSeries key={item.name} name={item.name} points={reading.points} value={item.value} />
      ))}
    </div>
  );
}

function HostSeries({ name, points, value }: { name: string; points: HarborHostMetricPoint[]; value: Reading }) {
  const width = 320;
  const height = 72;
  const values = points.map((point) => (point.missing ? null : value(point))).filter((item): item is number => item != null && Number.isFinite(item));
  const peak = Math.max(1, ...values, 0);
  const slot = points.length <= 1 ? width - 20 : (width - 20) / (points.length - 1);

  return (
    <figure style={{ margin: '0.5rem 0' }}>
      <figcaption>{name}</figcaption>
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={name} style={{ width: '100%', height }}>
        {points.map((point, index) => {
          const reading = point.missing ? null : value(point);
          const x = 10 + index * slot;
          if (reading == null || !Number.isFinite(reading)) {
            return <rect key={index} x={x - 1} y={8} width={2} height={height - 16} fill="currentColor" opacity={0.35} data-series={name} data-gap="true" />;
          }
          const y = 8 + (1 - reading / peak) * (height - 24);
          return <circle key={index} cx={x} cy={y} r={3} fill="currentColor" data-series={name} data-value={String(reading)} />;
        })}
      </svg>
    </figure>
  );
}
