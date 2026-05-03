import type {
  AuthenticateRequest,
  AuthenticateResult,
  WhoAmIResult,
  TenantLookupResult,
  TenantMetadata,
  UserMaster,
  UserUpsertRequest,
  Credential,
  EnumerationResult,
  Fleet,
  Vessel,
  Captain,
  Mission,
  Voyage,
  ArmadaEvent,
  MergeEntry,
  Signal,
  Dock,
  DiffResult,
  LogResult,
  InstructionsResult,
  DispatchRequest,
  VoyageCreateRequest,
  PlanningSession,
  PlanningSessionCreateRequest,
  PlanningSessionDetail,
  PlanningSessionDispatchRequest,
  PlanningSessionMessageRequest,
  PlanningSessionSummaryRequest,
  PlanningSessionSummaryResponse,
  TransitionRequest,
  SendSignalRequest,
  SettingsData,
  BatchDeleteResult,
  DoctorCheck,
  StatusSnapshot,
  PromptTemplate,
  Persona,
  Pipeline,
  Playbook,
  WorkflowProfile,
  WorkflowProfileValidationResult,
  CheckRun,
  CheckRunRequest,
  MuxEndpointListResult,
  MuxEndpointShowResult,
  WorkspaceChangesResult,
  WorkspaceCreateDirectoryRequest,
  WorkspaceFileResponse,
  WorkspaceOperationResult,
  WorkspaceRenameRequest,
  WorkspaceSaveRequest,
  WorkspaceSaveResult,
  WorkspaceSearchResult,
  WorkspaceStatusResult,
  WorkspaceTreeResult,
  RequestHistoryEntry,
  RequestHistoryQuery,
  RequestHistoryRecord,
  RequestHistorySummaryResult,
} from '../types/models';

const BASE_URL = import.meta.env.VITE_ARMADA_SERVER_URL || '';

let authToken: string | null = null;
let onUnauthorized: (() => void) | null = null;

export function setAuthToken(token: string | null) {
  authToken = token;
}

export function setOnUnauthorized(cb: () => void) {
  onUnauthorized = cb;
}

/** Convert a PascalCase key to camelCase. */
function keyToCamel(key: string): string {
  return key.charAt(0).toLowerCase() + key.slice(1);
}

/** Recursively convert all object keys from PascalCase to camelCase. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function camelizeKeys(obj: any): any {
  if (Array.isArray(obj)) return obj.map(camelizeKeys);
  if (obj !== null && typeof obj === 'object' && !(obj instanceof Date)) {
    return Object.fromEntries(
      Object.entries(obj).map(([k, v]) => [keyToCamel(k), camelizeKeys(v)])
    );
  }
  return obj;
}

interface RequestOptions {
  timeout?: number;
  rawText?: boolean;
}

const PLANNING_CREATE_TIMEOUT_MS = 5 * 60 * 1000;
const PLANNING_SUMMARIZE_TIMEOUT_MS = 3 * 60 * 1000;

async function request<T>(method: string, path: string, body?: unknown, opts?: RequestOptions): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (authToken) headers['X-Token'] = authToken;

  const controller = new AbortController();
  const timeoutMs = opts?.timeout ?? 30000;
  const timeoutId = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const res = await fetch(`${BASE_URL}${path}`, {
      method,
      headers,
      body: body ? JSON.stringify(body) : undefined,
      signal: controller.signal,
    });

    clearTimeout(timeoutId);

    if (res.status === 401) {
      onUnauthorized?.();
      throw new Error('Unauthorized');
    }

    if (!res.ok) {
      const text = await res.text();
      let msg = `${res.status}: ${text}`;
      try {
        const e = JSON.parse(text);
        msg = e.Message || e.message || e.Error || e.error || msg;
      } catch {
        // use raw text
      }
      throw new Error(msg);
    }

    if (res.status === 204) return undefined as T;

    if (opts?.rawText) {
      const text = await res.text();
      return text as unknown as T;
    }

    const json = await res.json();
    return camelizeKeys(json) as T;
  } catch (err) {
    clearTimeout(timeoutId);
    if (err instanceof DOMException && err.name === 'AbortError') {
      throw new Error('Request timed out');
    }
    throw err;
  }
}

function get<T>(path: string, opts?: RequestOptions) { return request<T>('GET', path, undefined, opts); }
function post<T>(path: string, body?: unknown, opts?: RequestOptions) { return request<T>('POST', path, body, opts); }
function put<T>(path: string, body?: unknown, opts?: RequestOptions) { return request<T>('PUT', path, body, opts); }
function del<T>(path: string, opts?: RequestOptions) { return request<T>('DELETE', path, undefined, opts); }

// Build query string from pagination/filter params
function buildQuery(params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }): string {
  if (!params) return '';
  const parts: string[] = [];
  if (params.pageNumber) parts.push(`pageNumber=${params.pageNumber}`);
  if (params.pageSize) parts.push(`pageSize=${params.pageSize}`);
  if (params.filters) {
    for (const [k, v] of Object.entries(params.filters)) {
      if (v) parts.push(`${encodeURIComponent(k)}=${encodeURIComponent(v)}`);
    }
  }
  return parts.length ? '?' + parts.join('&') : '';
}

function buildRequestHistoryQuery(params?: RequestHistoryQuery): string {
  if (!params) return '';

  const search = new URLSearchParams();
  const parts: string[] = [];
  if (params.pageNumber) search.set('pageNumber', String(params.pageNumber));
  if (params.pageSize) search.set('pageSize', String(params.pageSize));
  if (params.bucketMinutes) search.set('bucketMinutes', String(params.bucketMinutes));
  if (params.method) search.set('method', params.method);
  if (params.principal) search.set('principal', params.principal);
  if (params.tenantId) search.set('tenantId', params.tenantId);
  if (params.userId) search.set('userId', params.userId);
  if (params.credentialId) search.set('credentialId', params.credentialId);
  if (params.statusCode !== undefined && params.statusCode !== null) search.set('statusCode', String(params.statusCode));
  if (params.isSuccess !== undefined && params.isSuccess !== null) search.set('isSuccess', String(params.isSuccess));
  if (params.fromUtc) search.set('fromUtc', params.fromUtc);
  if (params.toUtc) search.set('toUtc', params.toUtc);

  const query = search.toString();
  if (query) parts.push(query);
  if (params.route) parts.push(`route=${encodeWorkspaceQueryPath(params.route)}`);
  return parts.length ? `?${parts.join('&')}` : '';
}

// ==================== Auth ====================
export const authenticate = (req: AuthenticateRequest) =>
  post<AuthenticateResult>('/api/v1/authenticate', req);

export const whoami = () => get<WhoAmIResult>('/api/v1/whoami');

export const lookupTenants = (email: string) =>
  post<TenantLookupResult>('/api/v1/tenants/lookup', { Email: email });

// ==================== Tenants (admin) ====================
export const listTenants = () => get<EnumerationResult<TenantMetadata>>('/api/v1/tenants');
export const createTenant = (data: Partial<TenantMetadata>) => post<TenantMetadata>('/api/v1/tenants', data);
export const updateTenant = (id: string, data: Partial<TenantMetadata>) => put<TenantMetadata>(`/api/v1/tenants/${id}`, data);
export const deleteTenant = (id: string) => del<void>(`/api/v1/tenants/${id}`);

// ==================== Users (admin) ====================
export const listUsers = () => get<EnumerationResult<UserMaster>>('/api/v1/users');
export const createUser = (data: UserUpsertRequest) => post<UserMaster>('/api/v1/users', data);
export const updateUser = (id: string, data: UserUpsertRequest) => put<UserMaster>(`/api/v1/users/${id}`, data);
export const deleteUser = (id: string) => del<void>(`/api/v1/users/${id}`);

// ==================== Credentials (admin) ====================
export const listCredentials = () => get<EnumerationResult<Credential>>('/api/v1/credentials');
export const createCredential = (data: Partial<Credential>) => post<Credential>('/api/v1/credentials', data);
export const updateCredential = (id: string, data: Partial<Credential>) => put<Credential>(`/api/v1/credentials/${id}`, data);
export const deleteCredential = (id: string) => del<void>(`/api/v1/credentials/${id}`);

// ==================== Fleets ====================
export const listFleets = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Fleet>>(`/api/v1/fleets${buildQuery(params)}`);
export const getFleet = (id: string) => get<Fleet>(`/api/v1/fleets/${id}`);
export const createFleet = (data: Partial<Fleet>) => post<Fleet>('/api/v1/fleets', data);
export const updateFleet = (id: string, data: Partial<Fleet>) => put<Fleet>(`/api/v1/fleets/${id}`, data);
export const deleteFleet = (id: string) => del<void>(`/api/v1/fleets/${id}`);
export const deleteFleetsBatch = (ids: string[]) => post<BatchDeleteResult>('/api/v1/fleets/delete/multiple', { Ids: ids });

// ==================== Vessels ====================
export const listVessels = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Vessel>>(`/api/v1/vessels${buildQuery(params)}`);
export const getVessel = (id: string) => get<Vessel>(`/api/v1/vessels/${id}`);
export const createVessel = (data: Partial<Vessel>) => post<Vessel>('/api/v1/vessels', data);
export const updateVessel = (id: string, data: Partial<Vessel>) => put<Vessel>(`/api/v1/vessels/${id}`, data);
export const deleteVessel = (id: string) => del<void>(`/api/v1/vessels/${id}`);
export const getVesselGitStatus = (id: string) => get<{ vesselId: string; commitsAhead: number | null; commitsBehind: number | null; error?: string }>(`/api/v1/vessels/${id}/git-status`);

// ==================== Workspace ====================
function encodeWorkspaceQueryPath(path: string) {
  return encodeURIComponent(path).replace(/%2F/g, '/');
}

export const getWorkspaceStatus = (vesselId: string) =>
  get<WorkspaceStatusResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/status`);
export const getWorkspaceTree = (vesselId: string, path?: string) =>
  get<WorkspaceTreeResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/tree${path ? `?path=${encodeWorkspaceQueryPath(path)}` : ''}`);
export const getWorkspaceFile = (vesselId: string, path: string) =>
  get<WorkspaceFileResponse>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/file?path=${encodeWorkspaceQueryPath(path)}`);
export const saveWorkspaceFile = (vesselId: string, data: WorkspaceSaveRequest) =>
  put<WorkspaceSaveResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/file`, data);
export const createWorkspaceDirectory = (vesselId: string, data: WorkspaceCreateDirectoryRequest) =>
  post<WorkspaceOperationResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/directory`, data);
export const renameWorkspaceEntry = (vesselId: string, data: WorkspaceRenameRequest) =>
  post<WorkspaceOperationResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/rename`, data);
export const deleteWorkspaceEntry = (vesselId: string, path: string) =>
  del<WorkspaceOperationResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/entry?path=${encodeWorkspaceQueryPath(path)}`);
export const searchWorkspace = (vesselId: string, query: string, maxResults = 200) =>
  get<WorkspaceSearchResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/search?q=${encodeURIComponent(query)}&maxResults=${maxResults}`);
export const getWorkspaceChanges = (vesselId: string) =>
  get<WorkspaceChangesResult>(`/api/v1/workspace/vessels/${encodeURIComponent(vesselId)}/changes`);

// ==================== Request History ====================
export const listRequestHistory = (params?: RequestHistoryQuery) =>
  get<EnumerationResult<RequestHistoryEntry>>(`/api/v1/request-history${buildRequestHistoryQuery(params)}`);
export const getRequestHistorySummary = (params?: RequestHistoryQuery) =>
  get<RequestHistorySummaryResult>(`/api/v1/request-history/summary${buildRequestHistoryQuery(params)}`);
export const getRequestHistoryEntry = (id: string) =>
  get<RequestHistoryRecord>(`/api/v1/request-history/${encodeURIComponent(id)}`);
export const deleteRequestHistoryEntry = (id: string) =>
  del<void>(`/api/v1/request-history/${encodeURIComponent(id)}`);
export const deleteRequestHistoryEntries = (ids: string[]) =>
  post<BatchDeleteResult>('/api/v1/request-history/delete/multiple', { Ids: ids });
export const deleteRequestHistoryByFilter = (query: RequestHistoryQuery) =>
  post<BatchDeleteResult>('/api/v1/request-history/delete/by-filter', query);

// ==================== Captains ====================
export const listCaptains = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Captain>>(`/api/v1/captains${buildQuery(params)}`);
export const getCaptain = (id: string) => get<Captain>(`/api/v1/captains/${id}`);
export const createCaptain = (data: Partial<Captain>) => post<Captain>('/api/v1/captains', data);
export const updateCaptain = (id: string, data: Partial<Captain>) => put<Captain>(`/api/v1/captains/${id}`, data);
export const deleteCaptain = (id: string) => del<void>(`/api/v1/captains/${id}`);
export const getCaptainLog = (id: string, lines = 500) => get<LogResult>(`/api/v1/captains/${id}/log?lines=${lines}`);
export const stopCaptain = (id: string) => post<void>(`/api/v1/captains/${id}/stop`);
export const recallCaptain = (id: string) => post<void>(`/api/v1/captains/${id}/recall`);
export const stopAllCaptains = () => post<void>('/api/v1/captains/stop-all');
export const listMuxEndpoints = (configDirectory?: string | null) =>
  get<MuxEndpointListResult>(`/api/v1/runtimes/mux/endpoints${configDirectory ? `?configDirectory=${encodeURIComponent(configDirectory)}` : ''}`);
export const getMuxEndpoint = (name: string, configDirectory?: string | null) =>
  get<MuxEndpointShowResult>(`/api/v1/runtimes/mux/endpoints/${encodeURIComponent(name)}${configDirectory ? `?configDirectory=${encodeURIComponent(configDirectory)}` : ''}`);

/** Restart a captain by deleting and recreating it with the same persisted configuration. */
export async function restartCaptain(id: string): Promise<Captain> {
  const captain = await getCaptain(id);
  await deleteCaptain(id);
  return createCaptain({
    name: captain.name,
    runtime: captain.runtime,
    systemInstructions: captain.systemInstructions,
    model: captain.model,
    allowedPersonas: captain.allowedPersonas,
    preferredPersona: captain.preferredPersona,
    runtimeOptionsJson: captain.runtimeOptionsJson,
  });
}

// ==================== Missions ====================
export const listMissions = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Mission>>(`/api/v1/missions${buildQuery(params)}`);
export const getMission = (id: string) => get<Mission>(`/api/v1/missions/${id}`);
export const createMission = (data: Partial<Mission>) => post<Mission>('/api/v1/missions', data);
export const updateMission = (id: string, data: Partial<Mission>) => put<Mission>(`/api/v1/missions/${id}`, data);
export const deleteMission = (id: string) => del<void>(`/api/v1/missions/${id}`);
export const purgeMission = (id: string) => del<void>(`/api/v1/missions/${id}/purge`);
export const dispatchMission = (data: DispatchRequest) => post<Mission>('/api/v1/missions', data);
export const restartMission = (id: string) => post<Mission>(`/api/v1/missions/${id}/restart`);
export const retryMissionLanding = (id: string) => post<any>(`/api/v1/missions/${id}/retry-landing`, {});
export const transitionMission = (id: string, data: TransitionRequest) => put<Mission>(`/api/v1/missions/${id}/status`, data);
export const approveMissionReview = (id: string, comment?: string) => post<Mission>(`/api/v1/missions/${id}/review/approve`, comment ? { comment } : {});
export const denyMissionReview = (id: string, comment?: string) => post<Mission>(`/api/v1/missions/${id}/review/deny`, comment ? { comment } : {});
export const getMissionDiff = (id: string) => get<DiffResult>(`/api/v1/missions/${id}/diff`, { timeout: 30000 });
export const getMissionLog = (id: string, lines = 500) => get<LogResult>(`/api/v1/missions/${id}/log?lines=${lines}`);
export const getMissionInstructions = (id: string) => get<InstructionsResult>(`/api/v1/missions/${id}/instructions`);

// ==================== Voyages ====================
export const listVoyages = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Voyage>>(`/api/v1/voyages${buildQuery(params)}`);
export const getVoyage = (id: string) => get<Voyage>(`/api/v1/voyages/${id}`);
export const getVoyageStatus = (id: string) => get<Record<string, unknown>>(`/api/v1/voyages/${id}/status`);
export const createVoyage = (data: VoyageCreateRequest) => post<Voyage>('/api/v1/voyages', data);
export const cancelVoyage = (id: string) => del<void>(`/api/v1/voyages/${id}`);
export const purgeVoyage = (id: string) => del<void>(`/api/v1/voyages/${id}/purge`);

// ==================== Planning Sessions ====================
export const listPlanningSessions = () => get<PlanningSession[]>('/api/v1/planning-sessions');
export const getPlanningSession = (id: string) => get<PlanningSessionDetail>(`/api/v1/planning-sessions/${id}`);
export const createPlanningSession = (data: PlanningSessionCreateRequest) =>
  post<PlanningSessionDetail>('/api/v1/planning-sessions', data, { timeout: PLANNING_CREATE_TIMEOUT_MS });
export const sendPlanningSessionMessage = (id: string, data: PlanningSessionMessageRequest) => post<PlanningSessionDetail>(`/api/v1/planning-sessions/${id}/messages`, data);
export const summarizePlanningSession = (id: string, data: PlanningSessionSummaryRequest) =>
  post<PlanningSessionSummaryResponse>(`/api/v1/planning-sessions/${id}/summarize`, data, { timeout: PLANNING_SUMMARIZE_TIMEOUT_MS });
export const dispatchPlanningSession = (id: string, data: PlanningSessionDispatchRequest) => post<Voyage>(`/api/v1/planning-sessions/${id}/dispatch`, data);
export const stopPlanningSession = (id: string) => post<PlanningSessionDetail>(`/api/v1/planning-sessions/${id}/stop`);
export const deletePlanningSession = (id: string) => del<void>(`/api/v1/planning-sessions/${id}`);

// ==================== Events ====================
export const listEvents = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<ArmadaEvent>>(`/api/v1/events${buildQuery(params)}`);
export const getEvent = (id: string) => get<ArmadaEvent>(`/api/v1/events/${id}`);
export const deleteEventsBatch = (ids: string[]) => post<BatchDeleteResult>('/api/v1/events/delete/multiple', { Ids: ids });

// ==================== Merge Queue ====================
export const listMergeQueue = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<MergeEntry>>(`/api/v1/merge-queue${buildQuery(params)}`);
export const getMergeEntry = (id: string) => get<MergeEntry>(`/api/v1/merge-queue/${id}`);
export const enqueueMerge = (data: Partial<MergeEntry>) => post<MergeEntry>('/api/v1/merge-queue', data);
export const deleteMergeEntry = (id: string) => del<void>(`/api/v1/merge-queue/${id}`);
export const processMergeEntry = (id: string) => post<void>(`/api/v1/merge-queue/${id}/process`);
export const processAllMergeQueue = () => post<void>('/api/v1/merge-queue/process-all');
export const cancelMergeEntry = (id: string) => post<void>(`/api/v1/merge-queue/${id}/cancel`);

// ==================== Prompt Templates ====================
export const listPromptTemplates = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<PromptTemplate>>(`/api/v1/prompt-templates${buildQuery(params)}`);
export const getPromptTemplate = (name: string) => get<PromptTemplate>(`/api/v1/prompt-templates/${encodeURIComponent(name)}`);
export const updatePromptTemplate = (name: string, data: { content: string; description?: string }) => put<PromptTemplate>(`/api/v1/prompt-templates/${encodeURIComponent(name)}`, data);
export const resetPromptTemplate = (name: string) => post<PromptTemplate>(`/api/v1/prompt-templates/${encodeURIComponent(name)}/reset`);

// ==================== Playbooks ====================
export const listPlaybooks = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Playbook>>(`/api/v1/playbooks${buildQuery(params)}`);
export const getPlaybook = (id: string) => get<Playbook>(`/api/v1/playbooks/${id}`);
export const createPlaybook = (data: Partial<Playbook>) => post<Playbook>('/api/v1/playbooks', data);
export const updatePlaybook = (id: string, data: Partial<Playbook>) => put<Playbook>(`/api/v1/playbooks/${id}`, data);
export const deletePlaybook = (id: string) => del<void>(`/api/v1/playbooks/${id}`);

// ==================== Workflow Profiles ====================
export const listWorkflowProfiles = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<WorkflowProfile>>(`/api/v1/workflow-profiles${buildQuery(params)}`);
export const getWorkflowProfile = (id: string) => get<WorkflowProfile>(`/api/v1/workflow-profiles/${encodeURIComponent(id)}`);
export const createWorkflowProfile = (data: Partial<WorkflowProfile>) => post<WorkflowProfile>('/api/v1/workflow-profiles', data);
export const updateWorkflowProfile = (id: string, data: Partial<WorkflowProfile>) => put<WorkflowProfile>(`/api/v1/workflow-profiles/${encodeURIComponent(id)}`, data);
export const deleteWorkflowProfile = (id: string) => del<void>(`/api/v1/workflow-profiles/${encodeURIComponent(id)}`);
export const validateWorkflowProfile = (data: Partial<WorkflowProfile>) => post<WorkflowProfileValidationResult>('/api/v1/workflow-profiles/validate', data);
export const resolveWorkflowProfile = (vesselId: string, workflowProfileId?: string | null) =>
  get<WorkflowProfile>(`/api/v1/workflow-profiles/resolve/vessels/${encodeURIComponent(vesselId)}${workflowProfileId ? `?workflowProfileId=${encodeURIComponent(workflowProfileId)}` : ''}`);

// ==================== Check Runs ====================
export const listCheckRuns = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<CheckRun>>(`/api/v1/check-runs${buildQuery(params)}`);
export const getCheckRun = (id: string) => get<CheckRun>(`/api/v1/check-runs/${encodeURIComponent(id)}`);
export const runCheck = (data: CheckRunRequest) => post<CheckRun>('/api/v1/check-runs', data, { timeout: 35 * 60 * 1000 });
export const retryCheckRun = (id: string) => post<CheckRun>(`/api/v1/check-runs/${encodeURIComponent(id)}/retry`);
export const deleteCheckRun = (id: string) => del<void>(`/api/v1/check-runs/${encodeURIComponent(id)}`);

// ==================== Personas ====================
export const listPersonas = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Persona>>(`/api/v1/personas${buildQuery(params)}`);
export const getPersona = (name: string) => get<Persona>(`/api/v1/personas/${encodeURIComponent(name)}`);
export const createPersona = (data: Partial<Persona>) => post<Persona>('/api/v1/personas', data);
export const updatePersona = (name: string, data: Partial<Persona>) => put<Persona>(`/api/v1/personas/${encodeURIComponent(name)}`, data);
export const deletePersona = (name: string) => del<void>(`/api/v1/personas/${encodeURIComponent(name)}`);

// ==================== Pipelines ====================
export const listPipelines = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Pipeline>>(`/api/v1/pipelines${buildQuery(params)}`);
export const getPipeline = (name: string) => get<Pipeline>(`/api/v1/pipelines/${encodeURIComponent(name)}`);
export const createPipeline = (data: Partial<Pipeline>) => post<Pipeline>('/api/v1/pipelines', data);
export const updatePipeline = (name: string, data: Partial<Pipeline>) => put<Pipeline>(`/api/v1/pipelines/${encodeURIComponent(name)}`, data);
export const deletePipeline = (name: string) => del<void>(`/api/v1/pipelines/${encodeURIComponent(name)}`);

// ==================== Docks ====================
export const listDocks = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Dock>>(`/api/v1/docks${buildQuery(params)}`);
export const getDock = (id: string) => get<Dock>(`/api/v1/docks/${id}`);
export const deleteDock = (id: string) => del<void>(`/api/v1/docks/${id}`);

// ==================== Signals ====================
export const listSignals = (params?: { pageNumber?: number; pageSize?: number; filters?: Record<string, string> }) =>
  get<EnumerationResult<Signal>>(`/api/v1/signals${buildQuery(params)}`);
export const getSignal = (id: string) => get<Signal>(`/api/v1/signals/${id}`);
export const sendSignal = (data: SendSignalRequest) => post<Signal>('/api/v1/signals', data);
export const markSignalRead = (id: string) => put<void>(`/api/v1/signals/${id}/read`);
export const deleteSignalsBatch = (ids: string[]) => post<BatchDeleteResult>('/api/v1/signals/delete/multiple', { Ids: ids });

// ==================== Status / Health ====================
export const getStatus = () => get<StatusSnapshot>('/api/v1/status');
export const getHealth = () => get<Record<string, unknown>>('/api/v1/status/health');
export const getDoctor = () => get<DoctorCheck[]>('/api/v1/doctor');

// ==================== Settings ====================
export const getSettings = () => get<SettingsData>('/api/v1/settings');
export const updateSettings = (data: SettingsData) => put<SettingsData>('/api/v1/settings', data);

// ==================== Server ====================
export const stopServer = () => post<void>('/api/v1/server/stop');
export const resetServer = () => post<void>('/api/v1/server/reset');

// ==================== Backup / Restore ====================
/** Download backup as a ZIP file blob. The server endpoint is GET and returns binary. */
export async function downloadBackup(): Promise<void> {
  const headers: Record<string, string> = {};
  if (authToken) headers['X-Token'] = authToken;
  const res = await fetch(`${BASE_URL}/api/v1/backup`, { method: 'GET', headers });
  if (res.status === 401) { onUnauthorized?.(); throw new Error('Unauthorized'); }
  if (!res.ok) throw new Error(`Backup failed: ${res.status}`);
  const blob = await res.blob();
  const disposition = res.headers.get('Content-Disposition') || '';
  const match = disposition.match(/filename="?([^"]+)"?/);
  const filename = match ? match[1] : `armada-backup-${new Date().toISOString().slice(0, 10)}.zip`;
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

/** Upload a backup ZIP file to restore. Sends raw bytes with filename header. */
export async function restoreBackup(file: File): Promise<Record<string, unknown>> {
  const headers: Record<string, string> = { 'Content-Type': 'application/octet-stream' };
  if (authToken) headers['X-Token'] = authToken;
  headers['X-Original-Filename'] = file.name;
  const bytes = await file.arrayBuffer();
  const res = await fetch(`${BASE_URL}/api/v1/restore`, { method: 'POST', headers, body: bytes });
  if (res.status === 401) { onUnauthorized?.(); throw new Error('Unauthorized'); }
  if (!res.ok) { const text = await res.text(); throw new Error(text || `Restore failed: ${res.status}`); }
  const json = await res.json();
  return camelizeKeys(json) as Record<string, unknown>;
}

// ==================== Generic entity lookup ====================
export function getEntity(type: string, id: string): Promise<unknown> {
  const typeMap: Record<string, string> = {
    fleets: 'fleets',
    vessels: 'vessels',
    captains: 'captains',
    missions: 'missions',
    voyages: 'voyages',
    signals: 'signals',
    events: 'events',
    docks: 'docks',
    'merge-queue': 'merge-queue',
    playbooks: 'playbooks',
  };
  const endpoint = typeMap[type];
  if (!endpoint) throw new Error(`Unknown entity type: ${type}`);
  return get<unknown>(`/api/v1/${endpoint}/${id}`);
}
