import { render, screen, within } from '@testing-library/react';
import DockGitAnchorPanel from './DockGitAnchorPanel';
import type { DockGitAnchorSnapshot } from '../../types/models';

vi.mock('../../context/LocaleContext', () => {
  const locale = {
    t: (text: string, params?: Record<string, string | number | null | undefined>) =>
      params
        ? Object.entries(params).reduce((current, [key, value]) => current.split(`{{${key}}}`).join(String(value ?? '')), text)
        : text,
    formatDateTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

const provisioned = '0123456789abcdef0123456789abcdef01234567';

function snapshot(overrides: Partial<DockGitAnchorSnapshot>): DockGitAnchorSnapshot {
  return {
    version: 1,
    dockId: 'dck_example',
    missionId: 'msn_example',
    vesselId: 'vsl_example',
    provisionedCommit: provisioned,
    provisionedUtc: '2026-09-13T10:00:00Z',
    resolvedUtc: '2026-09-13T10:00:05Z',
    state: 'Complete',
    truncated: false,
    errorCode: null,
    anchors: {
      targetBranch: 'main',
      baseCommit: provisioned,
      targetTip: 'fedcba9876543210fedcba9876543210fedcba98',
      resolutionError: null,
      hasContent: true,
      files: [
        {
          path: 'src/Service.cs',
          requestedPath: 'Service.cs',
          existsOnRevision: true,
          isExternalSourceTree: false,
          commits: [{ sha: 'abc1234def', subject: 'Harden the service gate', dateUtc: '2026-09-01' }],
        },
        { path: 'src/NewThing.cs', requestedPath: '', existsOnRevision: false, isExternalSourceTree: false, commits: [] },
        { path: 'decompiled-tree/Vendor.cs', requestedPath: '', existsOnRevision: false, isExternalSourceTree: true, commits: [] },
      ],
      priorArt: [
        { term: 'ServiceGate', found: true, matchingFileCount: 3, sampleLocations: ['src/Service.cs:42'] },
        { term: 'NewThing', found: false, matchingFileCount: 0, sampleLocations: [] },
      ],
    },
    ...overrides,
  };
}

describe('DockGitAnchorPanel', () => {
  it('states that no provisioning evidence is recorded when the dock has no snapshot', () => {
    render(<DockGitAnchorPanel snapshot={null} />);

    expect(screen.getByRole('heading', { name: 'Starting Point' })).toBeInTheDocument();
    expect(screen.getByText('No provisioning evidence is recorded for this dock.')).toBeInTheDocument();
  });

  it('shows the provisioned commit, branch, tip, path history and prior art of a complete snapshot', () => {
    render(<DockGitAnchorPanel snapshot={snapshot({})} />);

    expect(screen.getByText(provisioned)).toBeInTheDocument();
    expect(screen.getByText('main')).toBeInTheDocument();
    expect(screen.getByText('fedcba9876543210fedcba9876543210fedcba98')).toBeInTheDocument();
    expect(screen.getByText('Complete')).toBeInTheDocument();

    const service = screen.getByText('src/Service.cs').closest('[data-anchor-path]') as HTMLElement;
    expect(within(service).getByText(/Harden the service gate/)).toBeInTheDocument();
    expect(within(service).getByText('requested as Service.cs')).toBeInTheDocument();

    const created = screen.getByText('src/NewThing.cs').closest('[data-anchor-path]') as HTMLElement;
    expect(within(created).getByText('not on the provisioned revision')).toBeInTheDocument();

    const external = screen.getByText('decompiled-tree/Vendor.cs').closest('[data-anchor-path]') as HTMLElement;
    expect(within(external).getByText('external source tree')).toBeInTheDocument();
    expect(within(external).queryByText('not on the provisioned revision')).not.toBeInTheDocument();

    const found = screen.getByText('ServiceGate').closest('[data-prior-art]') as HTMLElement;
    expect(within(found).getByText('found in 3 files')).toBeInTheDocument();
    const absent = screen.getByText('NewThing').closest('[data-prior-art]') as HTMLElement;
    expect(within(absent).getByText('not found')).toBeInTheDocument();
  });

  it('marks an incomplete snapshot with its error code, truncation and resolution error', () => {
    render(
      <DockGitAnchorPanel
        snapshot={snapshot({
          state: 'Incomplete',
          truncated: true,
          errorCode: 'history_query_failed',
          anchors: { ...snapshot({}).anchors, resolutionError: 'target branch not found' },
        })}
      />,
    );

    expect(screen.getByText('Incomplete')).toBeInTheDocument();
    expect(screen.getByText('history_query_failed')).toBeInTheDocument();
    expect(screen.getByText('Some evidence was omitted to stay within storage bounds.')).toBeInTheDocument();
    expect(screen.getByText('target branch not found')).toBeInTheDocument();
  });

  it('says enrichment is pending for a seeded snapshot', () => {
    render(<DockGitAnchorPanel snapshot={snapshot({ state: 'Seeded', resolvedUtc: null })} />);

    expect(screen.getByText('Seeded')).toBeInTheDocument();
    expect(screen.getByText('Anchor enrichment has not run yet; only the provisioned commit is recorded.')).toBeInTheDocument();
  });
});
