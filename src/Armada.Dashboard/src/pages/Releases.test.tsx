import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import Releases from './Releases';
import {
  deleteRelease,
  listReleases,
  listVessels,
  listWorkflowProfiles,
} from '../api/client';

const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
  if (!params) return text;
  return Object.entries(params).reduce(
    (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
    text,
  );
};

vi.mock('../api/client', () => ({
  listReleases: vi.fn(),
  listVessels: vi.fn(),
  listWorkflowProfiles: vi.fn(),
  deleteRelease: vi.fn(),
}));

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    isAdmin: true,
    isTenantAdmin: true,
  }),
}));

vi.mock('../context/LocaleContext', () => ({
  useLocale: () => ({
    t: translate,
    formatDateTime: (value: string | null | undefined) => value ?? '',
    formatRelativeTime: (value: string | null | undefined) => value ?? '',
  }),
}));

vi.mock('../context/NotificationContext', () => ({
  useNotifications: () => ({
    pushToast: vi.fn(),
  }),
}));

describe('Releases', () => {
  beforeEach(() => {
    vi.mocked(listReleases).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 9999,
      totalPages: 1,
      totalRecords: 1,
      totalMs: 1,
      objects: [
        {
          id: 'rel_123',
          tenantId: 'ten_123',
          userId: 'usr_123',
          vesselId: 'vsl_123',
          workflowProfileId: 'wfp_123',
          title: 'Release Candidate',
          version: '1.2.3',
          tagName: 'v1.2.3',
          summary: 'Candidate summary',
          notes: 'Candidate notes',
          status: 'Candidate',
          voyageIds: ['vyg_123'],
          missionIds: ['msn_123'],
          checkRunIds: ['chk_123'],
          artifacts: [],
          createdUtc: '2026-05-03T00:00:00Z',
          lastUpdateUtc: '2026-05-03T01:00:00Z',
          publishedUtc: null,
        },
      ],
    });

    vi.mocked(listVessels).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 9999,
      totalPages: 1,
      totalRecords: 1,
      totalMs: 1,
      objects: [
        {
          id: 'vsl_123',
          name: 'Release Vessel',
        } as never,
      ],
    });

    vi.mocked(listWorkflowProfiles).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 9999,
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

    vi.mocked(deleteRelease).mockResolvedValue();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('renders releases and filters them by search text', async () => {
    render(
      <MemoryRouter initialEntries={['/releases']}>
        <Routes>
          <Route path="/releases" element={<Releases />} />
          <Route path="/releases/new" element={<div>Create Release Route</div>} />
          <Route path="/releases/:id" element={<div>Release Detail Route</div>} />
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByRole('heading', { name: 'Releases' })).toBeInTheDocument();
    expect(await screen.findByText('Release Candidate')).toBeInTheDocument();
    expect(screen.getAllByText('Release Vessel').length).toBeGreaterThan(0);
    expect(screen.getByText('Default Workflow')).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('Search by title, version, tag, summary, or ID...'), {
      target: { value: 'missing' },
    });
    expect(await screen.findByText('No releases match the current filters.')).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('Search by title, version, tag, summary, or ID...'), {
      target: { value: 'candidate' },
    });
    expect(await screen.findByText('Release Candidate')).toBeInTheDocument();
  });
});
