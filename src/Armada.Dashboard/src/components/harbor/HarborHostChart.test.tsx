import { render, screen } from '@testing-library/react';
import { expect, test } from 'vitest';
import HarborHostChart, { seriesSegments } from './HarborHostChart';
import type { HarborHostMetricPoint, HarborHostMetrics } from '../../types/models';

function point(partial: Partial<HarborHostMetricPoint> & Pick<HarborHostMetricPoint, 'atUtc' | 'missing'>): HarborHostMetricPoint {
  return {
    cpuPercent: null,
    memoryUsedBytes: null,
    memoryTotalBytes: null,
    diskUsedBytes: null,
    diskTotalBytes: null,
    activeJobs: null,
    activeCaptains: null,
    ...partial,
  };
}

const reading: HarborHostMetrics = {
  runnerId: 'host-a',
  state: 'stale',
  staleSinceUtc: '2026-03-01T00:00:00Z',
  newestSampleUtc: '2026-03-01T00:00:00Z',
  points: [
    point({ atUtc: '2026-03-01T00:00:00Z', missing: false, cpuPercent: 40, activeJobs: 2 }),
    point({ atUtc: '2026-03-01T00:02:00Z', missing: true }),
    point({ atUtc: '2026-03-01T00:03:00Z', missing: false, cpuPercent: 0, activeJobs: 1 }),
  ],
};

test('a missing sample is a gap and a measured zero stays zero', () => {
  expect(seriesSegments(reading.points, (item) => item.cpuPercent)).toEqual([[40], [0]]);
  const { container } = render(<HarborHostChart reading={reading} />);
  expect(screen.getByRole('status')).toHaveTextContent('Stale since');
  expect(screen.getByRole('status').querySelector('time')).toHaveAttribute('dateTime', '2026-03-01T00:00:00Z');
  expect(container.querySelectorAll('[data-series="CPU percent"][data-gap="true"]')).toHaveLength(1);
  expect([...container.querySelectorAll('[data-series="CPU percent"][data-value]')].map((node) => node.getAttribute('data-value'))).toEqual(['40', '0']);
});

test('no host sample renders an empty chart', () => {
  const { container } = render(<HarborHostChart reading={{ ...reading, state: 'no-data', staleSinceUtc: null, points: [] }} />);
  expect(screen.getByRole('status')).toHaveTextContent('No host metrics for this Harbor.');
  expect(container.querySelector('[data-value]')).toBeNull();
  expect(container.querySelector('[data-gap="true"]')).toBeNull();
});
