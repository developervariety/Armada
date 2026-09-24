import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import CheckRuns from './CheckRuns';
import {
  getVesselReadiness,
  listCheckRuns,
  listVessels,
  listWorkflowProfiles,
  previewWorkflowProfileForVessel,
  runCheck,
} from '../api/client';

vi.mock('../api/client', async () => (await import('../test/clientMock')).withAllPages({
  listCheckRuns: vi.fn(),
  listVessels: vi.fn(),
  listWorkflowProfiles: vi.fn(),
  previewWorkflowProfileForVessel: vi.fn(),
  getVesselReadiness: vi.fn(),
  runCheck: vi.fn(),
}));

// One stable locale object, like the real provider.
vi.mock('../context/LocaleContext', () => {
  const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
    if (!params) return text;
    return Object.entries(params).reduce(
      (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
      text,
    );
  };
  const locale = {
    t: translate,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  };
  return { useLocale: () => locale };
});

const auth = vi.hoisted(() => ({ current: { isAdmin: true, isTenantAdmin: true } }));
vi.mock('../context/AuthContext', () => ({ useAuth: () => auth.current }));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({
    pushToast: vi.fn(),
  }),
}));

const nightlyBuild = {
  id: 'chk_123',
  tenantId: 'ten_123',
  userId: 'usr_123',
  workflowProfileId: 'wfp_123',
  vesselId: 'vsl_123',
  missionId: null,
  voyageId: null,
  deploymentId: null,
  label: 'Nightly Build',
  type: 'Build',
  source: 'Armada',
  status: 'Passed',
  providerName: null,
  externalId: null,
  externalUrl: null,
  environmentName: null,
  command: 'dotnet build',
  workingDirectory: 'C:/repo',
  branchName: 'main',
  commitHash: 'abc123',
  exitCode: 0,
  output: 'Build succeeded.',
  summary: 'Nightly build passed.',
  testSummary: null,
  coverageSummary: null,
  artifacts: [],
  durationMs: 1250,
  startedUtc: '2026-05-03T00:00:00Z',
  completedUtc: '2026-05-03T00:00:01Z',
  createdUtc: '2026-05-03T00:00:00Z',
  lastUpdateUtc: '2026-05-03T00:00:01Z',
};

// The server contract: the route caps a page at 500, filters by status, type, source and vessel,
// and reports totals for the whole filtered set. This server holds 730 Armada runs.
type CheckRunListParams = { pageNumber?: number; pageSize?: number; filters?: Record<string, string> };
const SERVER_TOTAL = 730;
function serverList(params?: CheckRunListParams) {
  const pageSize = Math.min(Math.max(params?.pageSize ?? 10, 1), 500);
  const filters = params?.filters ?? {};
  const matches = (!filters.source || filters.source === 'Armada')
    && (!filters.status || filters.status === 'Passed')
    && (!filters.type || filters.type === 'Build')
    && (!filters.vesselId || filters.vesselId === 'vsl_123');
  const total = matches ? SERVER_TOTAL : 0;
  return {
    success: true,
    pageNumber: params?.pageNumber ?? 1,
    pageSize,
    totalPages: Math.max(1, Math.ceil(total / pageSize)),
    totalRecords: total,
    totalMs: 1,
    objects: matches ? [nightlyBuild] : [],
  };
}

describe('CheckRuns', () => {
  beforeEach(() => {
    vi.mocked(listCheckRuns).mockImplementation(async (params?: CheckRunListParams) => serverList(params) as never);

    vi.mocked(listVessels).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 1000,
      totalPages: 1,
      totalRecords: 1,
      totalMs: 1,
      objects: [
        {
          id: 'vsl_123',
          name: 'Check Vessel',
        } as never,
      ],
    });

    vi.mocked(listWorkflowProfiles).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 1000,
      totalPages: 1,
      totalRecords: 1,
      totalMs: 1,
      objects: [
        {
          id: 'wfp_123',
          name: 'Default Workflow',
        } as never,
      ],
    });

    vi.mocked(previewWorkflowProfileForVessel).mockResolvedValue({
      resolvedProfile: {
        id: 'wfp_123',
        tenantId: 'ten_123',
        userId: 'usr_123',
        name: 'Default Workflow',
        description: null,
        scope: 'Vessel',
        fleetId: null,
        vesselId: 'vsl_123',
        isDefault: true,
        active: true,
        languageHints: [],
        lintCommand: null,
        buildCommand: 'dotnet build',
        unitTestCommand: 'dotnet test',
        integrationTestCommand: null,
        e2eTestCommand: null,
        migrationCommand: null,
        securityScanCommand: null,
        performanceCommand: null,
        packageCommand: null,
        deploymentVerificationCommand: null,
        rollbackVerificationCommand: null,
        publishArtifactCommand: null,
        releaseVersioningCommand: null,
        changelogGenerationCommand: null,
        requiredSecrets: [],
        requiredInputs: [],
        expectedArtifacts: [],
        environments: [],
        createdUtc: '2026-05-03T00:00:00Z',
        lastUpdateUtc: '2026-05-03T00:00:00Z',
      },
      resolutionMode: 'Vessel',
      availableCheckTypes: ['Build', 'UnitTest'],
      commandPreviews: [
        {
          checkType: 'Build',
          environmentName: null,
          command: 'dotnet build',
        },
      ],
    });

    vi.mocked(getVesselReadiness).mockResolvedValue({
      vesselId: 'vsl_123',
      workflowProfileId: 'wfp_123',
      checkType: 'Build',
      environmentName: null,
      isReady: true,
      errorCount: 0,
      warningCount: 0,
      issues: [],
      setupChecklist: [],
      toolchainProbes: [],
      deploymentMetadata: null,
    } as never);

    vi.mocked(runCheck).mockResolvedValue({
      id: 'chk_456',
      vesselId: 'vsl_123',
      workflowProfileId: 'wfp_123',
      missionId: null,
      voyageId: null,
      label: 'Manual Build',
      type: 'Build',
      source: 'Armada',
      status: 'Passed',
      command: 'dotnet build',
      workingDirectory: 'C:/repo',
      artifacts: [],
      createdUtc: '2026-05-03T00:00:00Z',
      lastUpdateUtc: '2026-05-03T00:00:00Z',
    } as never);
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  function renderPage() {
    render(
      <MemoryRouter initialEntries={['/checks']}>
        <Routes>
          <Route path="/checks" element={<CheckRuns />} />
          <Route path="/checks/:id" element={<div>Check Detail Route</div>} />
          <Route path="/releases/new" element={<div>Create Release Route</div>} />
        </Routes>
      </MemoryRouter>,
    );
  }

  function cardValue(label: string) {
    const card = Array.from(document.querySelectorAll('.playbook-overview-card'))
      .find((element) => element.querySelector('span')?.textContent === label);
    return card?.querySelector('strong')?.textContent;
  }

  it('renders check runs, filters them, and opens the run modal', async () => {
    renderPage();

    expect(screen.getByRole('heading', { name: 'Checks' })).toBeInTheDocument();
    expect(await screen.findByText('Nightly Build')).toBeInTheDocument();
    expect(screen.getAllByText('Check Vessel').length).toBeGreaterThan(0);

    fireEvent.change(screen.getByDisplayValue('All sources'), {
      target: { value: 'External' },
    });
    expect(await screen.findByText('No check runs match the current filters.')).toBeInTheDocument();

    fireEvent.change(screen.getByDisplayValue('External'), {
      target: { value: 'all' },
    });
    expect(await screen.findByText('Nightly Build')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Run Check/ }));
    expect(screen.getByRole('heading', { name: 'Run Check' })).toBeInTheDocument();
  });

  it('requests a page the server can return and shows the server total', async () => {
    renderPage();
    expect(await screen.findByText('Nightly Build')).toBeInTheDocument();
    for (const call of vi.mocked(listCheckRuns).mock.calls) {
      expect(call[0]?.pageSize ?? 0).toBeLessThanOrEqual(500);
    }
    await waitFor(() => expect(cardValue('Total Runs')).toBe(String(SERVER_TOTAL)));
  });

  it('sends the status, type and source filters to the server', async () => {
    renderPage();
    expect(await screen.findByText('Nightly Build')).toBeInTheDocument();
    fireEvent.change(screen.getByDisplayValue('All statuses'), { target: { value: 'Failed' } });
    await waitFor(() => expect(listCheckRuns).toHaveBeenLastCalledWith(
      expect.objectContaining({ filters: expect.objectContaining({ status: 'Failed' }) }),
    ));
    fireEvent.change(screen.getByDisplayValue('All check types'), { target: { value: 'UnitTest' } });
    await waitFor(() => expect(listCheckRuns).toHaveBeenLastCalledWith(
      expect.objectContaining({ filters: expect.objectContaining({ status: 'Failed', type: 'UnitTest' }) }),
    ));
  });

  it('shows the command override only to a global administrator, because the server refuses it for anyone else', async () => {
    auth.current = { isAdmin: false, isTenantAdmin: true };
    const { unmount } = render(<MemoryRouter><CheckRuns /></MemoryRouter>);
    expect(await screen.findByText('Nightly Build')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Run Check/ }));
    expect(screen.queryByText('Command Override')).not.toBeInTheDocument();
    unmount();

    auth.current = { isAdmin: true, isTenantAdmin: true };
    render(<MemoryRouter><CheckRuns /></MemoryRouter>);
    expect(await screen.findByText('Nightly Build')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Run Check/ }));
    expect(screen.getByText('Command Override')).toBeInTheDocument();
  });

  it('a refresh reloads the runs but not the vessel and workflow profile names', async () => {
    renderPage();
    expect(await screen.findByText('Nightly Build')).toBeInTheDocument();
    await waitFor(() => expect(listVessels).toHaveBeenCalled());
    const vesselsBefore = vi.mocked(listVessels).mock.calls.length;
    const runsBefore = vi.mocked(listCheckRuns).mock.calls.length;

    fireEvent.click(screen.getByTitle('Refresh check runs'));

    await waitFor(() => expect(vi.mocked(listCheckRuns).mock.calls.length).toBeGreaterThan(runsBefore));
    expect(vi.mocked(listVessels).mock.calls.length).toBe(vesselsBefore);
  });
});
