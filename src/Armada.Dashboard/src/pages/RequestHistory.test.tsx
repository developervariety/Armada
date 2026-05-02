import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import RequestHistory from './RequestHistory';
import {
  deleteRequestHistoryByFilter,
  deleteRequestHistoryEntries,
  deleteRequestHistoryEntry,
  getRequestHistoryEntry,
  getRequestHistorySummary,
  listRequestHistory,
} from '../api/client';

const translate = (text: string, params?: Record<string, string | number | null | undefined>) => {
  if (!params) return text;
  return Object.entries(params).reduce(
    (current, [key, value]) => current.split(`{{${key}}}`).join(value == null ? '' : String(value)),
    text,
  );
};

vi.mock('../api/client', () => ({
  listRequestHistory: vi.fn(),
  getRequestHistorySummary: vi.fn(),
  getRequestHistoryEntry: vi.fn(),
  deleteRequestHistoryEntry: vi.fn(),
  deleteRequestHistoryEntries: vi.fn(),
  deleteRequestHistoryByFilter: vi.fn(),
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

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    isAdmin: false,
    isTenantAdmin: true,
  }),
}));

describe('RequestHistory', () => {
  beforeEach(() => {
    vi.mocked(listRequestHistory).mockResolvedValue({
      success: true,
      pageNumber: 1,
      pageSize: 25,
      totalPages: 1,
      totalRecords: 1,
      totalMs: 1,
      objects: [
        {
          id: 'req_123',
          tenantId: 'ten_123',
          userId: 'usr_123',
          credentialId: 'crd_123',
          principalDisplay: 'captain@armada',
          authMethod: 'Bearer',
          method: 'POST',
          route: '/api/v1/missions',
          routeTemplate: '/api/v1/missions',
          queryString: null,
          statusCode: 202,
          durationMs: 18.25,
          requestSizeBytes: 128,
          responseSizeBytes: 512,
          requestContentType: 'application/json',
          responseContentType: 'application/json',
          isSuccess: true,
          clientIp: '127.0.0.1',
          correlationId: 'corr_123',
          createdUtc: '2026-05-01T12:00:00Z',
        },
      ],
    });

    vi.mocked(getRequestHistorySummary).mockResolvedValue({
      totalCount: 1,
      successCount: 1,
      failureCount: 0,
      successRate: 100,
      averageDurationMs: 18.25,
      fromUtc: '2026-05-01T11:00:00Z',
      toUtc: '2026-05-01T12:00:00Z',
      bucketMinutes: 15,
      buckets: [
        {
          bucketStartUtc: '2026-05-01T11:45:00Z',
          bucketEndUtc: '2026-05-01T12:00:00Z',
          totalCount: 1,
          successCount: 1,
          failureCount: 0,
          averageDurationMs: 18.25,
        },
      ],
    });

    vi.mocked(getRequestHistoryEntry).mockResolvedValue({
      entry: {
        id: 'req_123',
        tenantId: 'ten_123',
        userId: 'usr_123',
        credentialId: 'crd_123',
        principalDisplay: 'captain@armada',
        authMethod: 'Bearer',
        method: 'POST',
        route: '/api/v1/missions',
        routeTemplate: '/api/v1/missions',
        queryString: null,
        statusCode: 202,
        durationMs: 18.25,
        requestSizeBytes: 128,
        responseSizeBytes: 512,
        requestContentType: 'application/json',
        responseContentType: 'application/json',
        isSuccess: true,
        clientIp: '127.0.0.1',
        correlationId: 'corr_123',
        createdUtc: '2026-05-01T12:00:00Z',
      },
      detail: null,
    });

    vi.mocked(deleteRequestHistoryEntry).mockResolvedValue();
    vi.mocked(deleteRequestHistoryEntries).mockResolvedValue({ deleted: 0, skipped: [] });
    vi.mocked(deleteRequestHistoryByFilter).mockResolvedValue({ deleted: 0, skipped: [] });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('renders summary cards and paginated request rows', async () => {
    render(
      <MemoryRouter initialEntries={['/requests']}>
        <Routes>
          <Route path="/requests" element={<RequestHistory />} />
          <Route path="/requests/:id" element={<RequestHistory />} />
          <Route path="/api-explorer" element={<div>API Explorer Route</div>} />
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByRole('heading', { name: 'Requests' })).toBeInTheDocument();
    expect(await screen.findByText('/api/v1/missions')).toBeInTheDocument();
    expect(screen.getByText('captain@armada')).toBeInTheDocument();
    expect(screen.getByText('100.0%')).toBeInTheDocument();
  });

  it('renders collapsible request detail sections for headers and bodies', async () => {
    vi.mocked(getRequestHistoryEntry).mockResolvedValueOnce({
      entry: {
        id: 'req_123',
        tenantId: 'ten_123',
        userId: 'usr_123',
        credentialId: 'crd_123',
        principalDisplay: 'captain@armada',
        authMethod: 'Bearer',
        method: 'POST',
        route: '/api/v1/missions',
        routeTemplate: '/api/v1/missions',
        queryString: null,
        statusCode: 202,
        durationMs: 18.25,
        requestSizeBytes: 128,
        responseSizeBytes: 512,
        requestContentType: 'application/json',
        responseContentType: 'application/json',
        isSuccess: true,
        clientIp: '127.0.0.1',
        correlationId: 'corr_123',
        createdUtc: '2026-05-01T12:00:00Z',
      },
      detail: {
        requestHistoryId: 'req_123',
        pathParamsJson: '{}',
        queryParamsJson: '{}',
        requestHeadersJson: '{"x-request-header":"header-value"}',
        responseHeadersJson: '{"x-response-header":"response-value"}',
        requestBodyText: 'request-body-value',
        responseBodyText: 'response-body-value',
        requestBodyTruncated: false,
        responseBodyTruncated: false,
      },
    });

    render(
      <MemoryRouter initialEntries={['/requests/req_123']}>
        <Routes>
          <Route path="/requests" element={<RequestHistory />} />
          <Route path="/requests/:id" element={<RequestHistory />} />
          <Route path="/api-explorer" element={<div>API Explorer Route</div>} />
        </Routes>
      </MemoryRouter>,
    );

    expect(await screen.findByText('request-body-value')).toBeInTheDocument();
    expect(screen.getByText(/x-request-header/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Request Headers/i }));
    expect(screen.queryByText(/x-request-header/i)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Request Body/i }));
    expect(screen.queryByText('request-body-value')).not.toBeInTheDocument();
  });
});
