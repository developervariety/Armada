const DEPLOYMENT_STORAGE_KEY = 'armada_proxy_instance_id';
const PROXY_SESSION_STORAGE_KEY = 'armada_proxy_session_token';
const THEME_STORAGE_KEY = 'armada_proxy_theme';
const PROXY_SESSION_HEADER = 'X-Armada-Proxy-Session';
const API_EXPLORER_PRESETS = [
  { key: 'summary', label: 'Summary', method: 'GET', path: '/summary' },
  { key: 'activity', label: 'Recent Activity', method: 'GET', path: '/activity' },
  { key: 'missions', label: 'Missions', method: 'GET', path: '/missions' },
  { key: 'voyages', label: 'Voyages', method: 'GET', path: '/voyages' },
  { key: 'playbooks', label: 'Playbooks', method: 'GET', path: '/playbooks' },
  { key: 'backlog', label: 'Backlog', method: 'GET', path: '/backlog' },
  { key: 'planning', label: 'Planning Sessions', method: 'GET', path: '/planning-sessions' },
  { key: 'workflow-profiles', label: 'Workflow Profiles', method: 'GET', path: '/workflow-profiles' },
  { key: 'check-runs', label: 'Check Runs', method: 'GET', path: '/check-runs' },
  { key: 'environments', label: 'Environments', method: 'GET', path: '/environments' },
  { key: 'releases', label: 'Releases', method: 'GET', path: '/releases' },
  { key: 'deployments', label: 'Deployments', method: 'GET', path: '/deployments' },
  { key: 'incidents', label: 'Incidents', method: 'GET', path: '/incidents' },
  { key: 'runbooks', label: 'Runbooks', method: 'GET', path: '/runbooks' },
  { key: 'request-history', label: 'Request History', method: 'GET', path: '/request-history' },
  { key: 'personas', label: 'Personas', method: 'GET', path: '/personas' },
  { key: 'prompt-templates', label: 'Prompt Templates', method: 'GET', path: '/prompt-templates' },
];
const state = {
  instances: [],
  selectedInstanceId: null,
  pendingInstanceId: null,
  sessionToken: null,
  isAuthenticated: false,
  loginStep: 'proxy-password',
  sidebarOpen: false,
  summary: null,
  fleets: [],
  vessels: [],
  pipelines: [],
  playbooks: [],
  objectives: [],
  planningSessions: [],
  workflowProfiles: [],
  checkRuns: [],
  environments: [],
  releases: [],
  deployments: [],
  incidents: [],
  runbooks: [],
  runbookExecutions: [],
  requestHistory: [],
  requestHistorySummary: null,
  personas: [],
  promptTemplates: [],
  captainToolAccess: {},
  workspaceSnapshot: null,
  theme: 'light',
  selectedFleetId: null,
  selectedVesselId: null,
  selectedMissionId: null,
  selectedPlaybookId: null,
  selectedObjectiveId: null,
  selectedPlanningSessionId: null,
  selectedPlanningMessageId: '',
  editingObjective: null,
  editingFleet: null,
  editingVessel: null,
  editingMission: null,
  editingPlaybook: null,
  planningDetail: null,
  planningComposer: '',
  planningDraftTitle: '',
  planningDraftDescription: '',
  planningWorkspaceLoading: false,
  planningWorkspaceStatus: { message: '', kind: null },
  planningSending: false,
  planningSummarizing: false,
  planningDispatching: false,
  planningStopping: false,
  planningDeleting: false,
  dispatchSelectedPlaybooks: [],
  detailModal: null,
  resourceEditor: null,
};

const elements = {
  loginView: document.getElementById('loginView'),
  loginLogo: document.getElementById('loginLogo'),
  loginCopy: document.getElementById('loginCopy'),
  loginStepProxy: document.getElementById('loginStepProxy'),
  loginStepDeployment: document.getElementById('loginStepDeployment'),
  loginStepAccess: document.getElementById('loginStepAccess'),
  proxyPasswordStep: document.getElementById('proxyPasswordStep'),
  proxyUnlockForm: document.getElementById('proxyUnlockForm'),
  proxyUnlockButton: document.getElementById('proxyUnlockButton'),
  deploymentSelectionStep: document.getElementById('deploymentSelectionStep'),
  loginPassword: document.getElementById('loginPassword'),
  loginStatus: document.getElementById('loginStatus'),
  loginRefreshButton: document.getElementById('loginRefreshButton'),
  proxyRelockButton: document.getElementById('proxyRelockButton'),
  loginThemeToggleButton: document.getElementById('loginThemeToggleButton'),
  instanceCount: document.getElementById('instanceCount'),
  instanceList: document.getElementById('instanceList'),
  deploymentPasswordStep: document.getElementById('deploymentPasswordStep'),
  selectedInstanceId: document.getElementById('selectedInstanceId'),
  selectedInstanceMeta: document.getElementById('selectedInstanceMeta'),
  deploymentPasswordForm: document.getElementById('deploymentPasswordForm'),
  deploymentPassword: document.getElementById('deploymentPassword'),
  deploymentOpenButton: document.getElementById('deploymentOpenButton'),
  deploymentBackButton: document.getElementById('deploymentBackButton'),
  appView: document.getElementById('appView'),
  sidebar: document.getElementById('sidebar'),
  sidebarOverlay: document.getElementById('sidebarOverlay'),
  sidebarDeploymentId: document.getElementById('sidebarDeploymentId'),
  sidebarDeploymentState: document.getElementById('sidebarDeploymentState'),
  switchDeploymentButton: document.getElementById('switchDeploymentButton'),
  sidebarSwitchDeploymentButton: document.getElementById('sidebarSwitchDeploymentButton'),
  mobileMenuButton: document.getElementById('mobileMenuButton'),
  currentDeploymentLabel: document.getElementById('currentDeploymentLabel'),
  currentDeploymentState: document.getElementById('currentDeploymentState'),
  refreshButton: document.getElementById('refreshButton'),
  themeToggleButton: document.getElementById('themeToggleButton'),
  openDispatchButton: document.getElementById('openDispatchButton'),
  emptyState: document.getElementById('emptyState'),
  instanceWorkspace: document.getElementById('instanceWorkspace'),
  summaryTitle: document.getElementById('summaryTitle'),
  summarySubtitle: document.getElementById('summarySubtitle'),
  summaryCards: document.getElementById('summaryCards'),
  activityFeed: document.getElementById('activityFeed'),
  missionList: document.getElementById('missionList'),
  voyageList: document.getElementById('voyageList'),
  captainList: document.getElementById('captainList'),
  fleetList: document.getElementById('fleetList'),
  vesselList: document.getElementById('vesselList'),
  playbookList: document.getElementById('playbookList'),
  backlogList: document.getElementById('backlogList'),
  planningList: document.getElementById('planningList'),
  planningWorkspace: document.getElementById('planningWorkspace'),
  workflowProfileList: document.getElementById('workflowProfileList'),
  checkRunList: document.getElementById('checkRunList'),
  environmentList: document.getElementById('environmentList'),
  releaseList: document.getElementById('releaseList'),
  deploymentList: document.getElementById('deploymentList'),
  incidentList: document.getElementById('incidentList'),
  runbookList: document.getElementById('runbookList'),
  runbookExecutionList: document.getElementById('runbookExecutionList'),
  requestHistoryList: document.getElementById('requestHistoryList'),
  requestHistorySummary: document.getElementById('requestHistorySummary'),
  captainToolsCaptainId: document.getElementById('captainToolsCaptainId'),
  captainToolsLoadButton: document.getElementById('captainToolsLoadButton'),
  captainToolsSummary: document.getElementById('captainToolsSummary'),
  apiExplorerForm: document.getElementById('apiExplorerForm'),
  apiExplorerPreset: document.getElementById('apiExplorerPreset'),
  apiExplorerUsePresetButton: document.getElementById('apiExplorerUsePresetButton'),
  apiExplorerMethod: document.getElementById('apiExplorerMethod'),
  apiExplorerPath: document.getElementById('apiExplorerPath'),
  apiExplorerBody: document.getElementById('apiExplorerBody'),
  apiExplorerSendButton: document.getElementById('apiExplorerSendButton'),
  apiExplorerStatusText: document.getElementById('apiExplorerStatusText'),
  apiExplorerResponse: document.getElementById('apiExplorerResponse'),
  workspaceForm: document.getElementById('workspaceForm'),
  workspaceVesselId: document.getElementById('workspaceVesselId'),
  workspacePath: document.getElementById('workspacePath'),
  workspaceSearchQuery: document.getElementById('workspaceSearchQuery'),
  workspaceSearchLimit: document.getElementById('workspaceSearchLimit'),
  workspaceLoadStatusButton: document.getElementById('workspaceLoadStatusButton'),
  workspaceLoadTreeButton: document.getElementById('workspaceLoadTreeButton'),
  workspaceSearchButton: document.getElementById('workspaceSearchButton'),
  workspaceLoadChangesButton: document.getElementById('workspaceLoadChangesButton'),
  workspaceStatusText: document.getElementById('workspaceStatusText'),
  workspaceStatusPanel: document.getElementById('workspaceStatusPanel'),
  workspaceResultList: document.getElementById('workspaceResultList'),
  pipelineReferenceList: document.getElementById('pipelineReferenceList'),
  personaReferenceList: document.getElementById('personaReferenceList'),
  promptTemplateReferenceList: document.getElementById('promptTemplateReferenceList'),
  missionBrowseForm: document.getElementById('missionBrowseForm'),
  missionBrowseStatus: document.getElementById('missionBrowseStatus'),
  missionBrowseLimit: document.getElementById('missionBrowseLimit'),
  missionBrowseVoyageId: document.getElementById('missionBrowseVoyageId'),
  missionBrowseVesselId: document.getElementById('missionBrowseVesselId'),
  missionBrowseRecentButton: document.getElementById('missionBrowseRecentButton'),
  missionBrowseStatusText: document.getElementById('missionBrowseStatusText'),
  voyageBrowseForm: document.getElementById('voyageBrowseForm'),
  voyageBrowseStatus: document.getElementById('voyageBrowseStatus'),
  voyageBrowseLimit: document.getElementById('voyageBrowseLimit'),
  voyageBrowseRecentButton: document.getElementById('voyageBrowseRecentButton'),
  voyageBrowseStatusText: document.getElementById('voyageBrowseStatusText'),
  backlogBrowseForm: document.getElementById('backlogBrowseForm'),
  backlogBrowseStatus: document.getElementById('backlogBrowseStatus'),
  backlogBrowseBacklogState: document.getElementById('backlogBrowseBacklogState'),
  backlogBrowseLimit: document.getElementById('backlogBrowseLimit'),
  backlogBrowseSearch: document.getElementById('backlogBrowseSearch'),
  backlogBrowseOwner: document.getElementById('backlogBrowseOwner'),
  backlogBrowseVesselId: document.getElementById('backlogBrowseVesselId'),
  backlogBrowseTag: document.getElementById('backlogBrowseTag'),
  backlogBrowseRecentButton: document.getElementById('backlogBrowseRecentButton'),
  backlogBrowseStatusText: document.getElementById('backlogBrowseStatusText'),
  planningBrowseForm: document.getElementById('planningBrowseForm'),
  planningBrowseCaptainId: document.getElementById('planningBrowseCaptainId'),
  planningBrowseVesselId: document.getElementById('planningBrowseVesselId'),
  planningBrowseStatus: document.getElementById('planningBrowseStatus'),
  planningBrowseObjectiveId: document.getElementById('planningBrowseObjectiveId'),
  planningBrowseLimit: document.getElementById('planningBrowseLimit'),
  planningBrowseRecentButton: document.getElementById('planningBrowseRecentButton'),
  planningBrowseStatusText: document.getElementById('planningBrowseStatusText'),
  requestHistoryForm: document.getElementById('requestHistoryForm'),
  requestHistoryMethod: document.getElementById('requestHistoryMethod'),
  requestHistoryRoute: document.getElementById('requestHistoryRoute'),
  requestHistoryStatusCode: document.getElementById('requestHistoryStatusCode'),
  requestHistoryLimit: document.getElementById('requestHistoryLimit'),
  requestHistoryRecentButton: document.getElementById('requestHistoryRecentButton'),
  requestHistoryStatusText: document.getElementById('requestHistoryStatusText'),
  openFleetModalButton: document.getElementById('openFleetModalButton'),
  openVesselModalButton: document.getElementById('openVesselModalButton'),
  openPlaybookModalButton: document.getElementById('openPlaybookModalButton'),
  openBacklogModalButton: document.getElementById('openBacklogModalButton'),
  openPlanningModalButton: document.getElementById('openPlanningModalButton'),
  refreshBacklogButton: document.getElementById('refreshBacklogButton'),
  refreshPlanningButton: document.getElementById('refreshPlanningButton'),
  refreshDeliveryButton: document.getElementById('refreshDeliveryButton'),
  refreshReferenceButton: document.getElementById('refreshReferenceButton'),
  entityCardTemplate: document.getElementById('entityCardTemplate'),
  detailModal: document.getElementById('detailModal'),
  detailModalTitle: document.getElementById('detailModalTitle'),
  detailModalSubtitle: document.getElementById('detailModalSubtitle'),
  detailModalBody: document.getElementById('detailModalBody'),
  dispatchModal: document.getElementById('dispatchModal'),
  dispatchForm: document.getElementById('dispatchForm'),
  dispatchVesselId: document.getElementById('dispatchVesselId'),
  dispatchPipelineId: document.getElementById('dispatchPipelineId'),
  dispatchPriority: document.getElementById('dispatchPriority'),
  dispatchTitle: document.getElementById('dispatchTitle'),
  dispatchDescription: document.getElementById('dispatchDescription'),
  dispatchPlaybookList: document.getElementById('dispatchPlaybookList'),
  dispatchMissions: document.getElementById('dispatchMissions'),
  dispatchSubmitButton: document.getElementById('dispatchSubmitButton'),
  dispatchFormStatus: document.getElementById('dispatchFormStatus'),
  fleetModal: document.getElementById('fleetModal'),
  fleetModalTitle: document.getElementById('fleetModalTitle'),
  fleetModalSubtitle: document.getElementById('fleetModalSubtitle'),
  fleetForm: document.getElementById('fleetForm'),
  fleetName: document.getElementById('fleetName'),
  fleetDescription: document.getElementById('fleetDescription'),
  fleetDefaultPipelineId: document.getElementById('fleetDefaultPipelineId'),
  fleetActive: document.getElementById('fleetActive'),
  fleetResetButton: document.getElementById('fleetResetButton'),
  fleetFormStatus: document.getElementById('fleetFormStatus'),
  vesselModal: document.getElementById('vesselModal'),
  vesselModalTitle: document.getElementById('vesselModalTitle'),
  vesselModalSubtitle: document.getElementById('vesselModalSubtitle'),
  vesselForm: document.getElementById('vesselForm'),
  vesselFleetId: document.getElementById('vesselFleetId'),
  vesselName: document.getElementById('vesselName'),
  vesselRepoUrl: document.getElementById('vesselRepoUrl'),
  vesselWorkingDirectory: document.getElementById('vesselWorkingDirectory'),
  vesselDefaultBranch: document.getElementById('vesselDefaultBranch'),
  vesselDefaultPipelineId: document.getElementById('vesselDefaultPipelineId'),
  vesselAllowConcurrentMissions: document.getElementById('vesselAllowConcurrentMissions'),
  vesselActive: document.getElementById('vesselActive'),
  vesselResetButton: document.getElementById('vesselResetButton'),
  vesselFormStatus: document.getElementById('vesselFormStatus'),
  missionModal: document.getElementById('missionModal'),
  missionModalTitle: document.getElementById('missionModalTitle'),
  missionModalSubtitle: document.getElementById('missionModalSubtitle'),
  missionForm: document.getElementById('missionForm'),
  missionTitle: document.getElementById('missionTitle'),
  missionDescription: document.getElementById('missionDescription'),
  missionVesselId: document.getElementById('missionVesselId'),
  missionVoyageId: document.getElementById('missionVoyageId'),
  missionPersona: document.getElementById('missionPersona'),
  missionPriority: document.getElementById('missionPriority'),
  missionResetButton: document.getElementById('missionResetButton'),
  missionFormStatus: document.getElementById('missionFormStatus'),
  playbookModal: document.getElementById('playbookModal'),
  playbookModalTitle: document.getElementById('playbookModalTitle'),
  playbookModalSubtitle: document.getElementById('playbookModalSubtitle'),
  playbookForm: document.getElementById('playbookForm'),
  playbookFileName: document.getElementById('playbookFileName'),
  playbookDescription: document.getElementById('playbookDescription'),
  playbookContent: document.getElementById('playbookContent'),
  playbookActive: document.getElementById('playbookActive'),
  playbookResetButton: document.getElementById('playbookResetButton'),
  playbookDeleteButton: document.getElementById('playbookDeleteButton'),
  playbookFormStatus: document.getElementById('playbookFormStatus'),
  backlogModal: document.getElementById('backlogModal'),
  backlogModalTitle: document.getElementById('backlogModalTitle'),
  backlogModalSubtitle: document.getElementById('backlogModalSubtitle'),
  backlogForm: document.getElementById('backlogForm'),
  backlogTitle: document.getElementById('backlogTitle'),
  backlogDescription: document.getElementById('backlogDescription'),
  backlogStatus: document.getElementById('backlogStatus'),
  backlogBacklogState: document.getElementById('backlogBacklogState'),
  backlogKind: document.getElementById('backlogKind'),
  backlogPriority: document.getElementById('backlogPriority'),
  backlogEffort: document.getElementById('backlogEffort'),
  backlogOwner: document.getElementById('backlogOwner'),
  backlogVesselId: document.getElementById('backlogVesselId'),
  backlogTargetVersion: document.getElementById('backlogTargetVersion'),
  backlogDueUtc: document.getElementById('backlogDueUtc'),
  backlogParentObjectiveId: document.getElementById('backlogParentObjectiveId'),
  backlogBlockedByObjectiveIds: document.getElementById('backlogBlockedByObjectiveIds'),
  backlogTags: document.getElementById('backlogTags'),
  backlogAcceptanceCriteria: document.getElementById('backlogAcceptanceCriteria'),
  backlogNonGoals: document.getElementById('backlogNonGoals'),
  backlogRolloutConstraints: document.getElementById('backlogRolloutConstraints'),
  backlogEvidenceLinks: document.getElementById('backlogEvidenceLinks'),
  backlogSaveButton: document.getElementById('backlogSaveButton'),
  backlogResetButton: document.getElementById('backlogResetButton'),
  backlogDeleteButton: document.getElementById('backlogDeleteButton'),
  backlogFormStatus: document.getElementById('backlogFormStatus'),
  planningModal: document.getElementById('planningModal'),
  planningModalTitle: document.getElementById('planningModalTitle'),
  planningModalSubtitle: document.getElementById('planningModalSubtitle'),
  planningForm: document.getElementById('planningForm'),
  planningTitle: document.getElementById('planningTitle'),
  planningCaptainId: document.getElementById('planningCaptainId'),
  planningVesselId: document.getElementById('planningVesselId'),
  planningPipelineId: document.getElementById('planningPipelineId'),
  planningObjectiveId: document.getElementById('planningObjectiveId'),
  planningInitialMessage: document.getElementById('planningInitialMessage'),
  planningSaveButton: document.getElementById('planningSaveButton'),
  planningResetButton: document.getElementById('planningResetButton'),
  planningFormStatus: document.getElementById('planningFormStatus'),
  resourceEditorModal: document.getElementById('resourceEditorModal'),
  resourceEditorTitle: document.getElementById('resourceEditorTitle'),
  resourceEditorSubtitle: document.getElementById('resourceEditorSubtitle'),
  resourceEditorForm: document.getElementById('resourceEditorForm'),
  resourceEditorHelp: document.getElementById('resourceEditorHelp'),
  resourceEditorPayload: document.getElementById('resourceEditorPayload'),
  resourceEditorSaveButton: document.getElementById('resourceEditorSaveButton'),
  resourceEditorResetButton: document.getElementById('resourceEditorResetButton'),
  resourceEditorDeleteButton: document.getElementById('resourceEditorDeleteButton'),
  resourceEditorStatus: document.getElementById('resourceEditorStatus'),
};

const PLAYBOOK_DELIVERY_MODES = [
  {
    value: 'InlineFullContent',
    label: 'Inline full content',
    description: 'Inject the complete markdown into every dispatched mission instruction.',
  },
  {
    value: 'InstructionWithReference',
    label: 'Instruction with reference',
    description: 'Tell the model to read the materialized playbook outside the worktree.',
  },
  {
    value: 'AttachIntoWorktree',
    label: 'Attach into worktree',
    description: 'Materialize the playbook in the worktree and instruct the model to use it there.',
  },
];

const DEFAULT_RESOURCE_LIMIT = 8;

const RESOURCE_DEFINITIONS = {
  workflowProfile: {
    label: 'Workflow Profile',
    kind: 'workflow-profile',
    listElementKey: 'workflowProfileList',
    endpoint: 'workflow-profiles',
    detailPath: (id) => `/workflow-profiles/${encodeURIComponent(id)}`,
    enumeratePath: '/workflow-profiles/enumerate',
    createTemplate: () => ({
      name: 'New Workflow Profile',
      description: '',
      scope: 'Global',
      isDefault: false,
      active: true,
      languageHints: [],
      lintCommand: '',
      buildCommand: '',
      unitTestCommand: '',
      integrationTestCommand: '',
      e2eTestCommand: '',
      deploymentVerificationCommand: '',
      environments: [],
    }),
  },
  checkRun: {
    label: 'Check Run',
    kind: 'check-run',
    listElementKey: 'checkRunList',
    endpoint: 'check-runs',
    detailPath: (id) => `/check-runs/${encodeURIComponent(id)}`,
    enumeratePath: '/check-runs/enumerate',
    updateSupported: false,
    createTemplate: () => ({
      vesselId: state.vessels?.[0]?.id || '',
      workflowProfileId: '',
      type: 'Build',
      label: '',
      environmentName: '',
    }),
  },
  environment: {
    label: 'Environment',
    kind: 'environment',
    listElementKey: 'environmentList',
    endpoint: 'environments',
    detailPath: (id) => `/environments/${encodeURIComponent(id)}`,
    enumeratePath: '/environments/enumerate',
    createTemplate: () => ({
      vesselId: state.vessels?.[0]?.id || '',
      name: 'Environment',
      description: '',
      kind: 'Development',
      baseUrl: '',
      healthEndpoint: '',
      requiresApproval: false,
      isDefault: false,
      active: true,
      verificationDefinitions: [],
    }),
  },
  release: {
    label: 'Release',
    kind: 'release',
    listElementKey: 'releaseList',
    endpoint: 'releases',
    detailPath: (id) => `/releases/${encodeURIComponent(id)}`,
    enumeratePath: '/releases/enumerate',
    createTemplate: () => ({
      vesselId: state.vessels?.[0]?.id || '',
      title: 'Draft Release',
      version: '',
      tagName: '',
      summary: '',
      notes: '',
      status: 'Draft',
      voyageIds: [],
      missionIds: [],
      checkRunIds: [],
      objectiveIds: [],
    }),
  },
  deployment: {
    label: 'Deployment',
    kind: 'deployment',
    listElementKey: 'deploymentList',
    endpoint: 'deployments',
    detailPath: (id) => `/deployments/${encodeURIComponent(id)}`,
    enumeratePath: '/deployments/enumerate',
    createTemplate: () => ({
      vesselId: state.vessels?.[0]?.id || '',
      environmentId: '',
      environmentName: '',
      releaseId: '',
      title: 'Deployment',
      sourceRef: '',
      summary: '',
      notes: '',
      autoExecute: false,
      objectiveIds: [],
    }),
  },
  incident: {
    label: 'Incident',
    kind: 'incident',
    listElementKey: 'incidentList',
    endpoint: 'incidents',
    detailPath: (id) => `/incidents/${encodeURIComponent(id)}`,
    enumeratePath: '/incidents/enumerate',
    createTemplate: () => ({
      title: 'Incident',
      summary: '',
      status: 'Open',
      severity: 'High',
      vesselId: state.vessels?.[0]?.id || '',
      environmentId: '',
      environmentName: '',
      deploymentId: '',
      releaseId: '',
      impact: '',
      rootCause: '',
      recoveryNotes: '',
      postmortem: '',
      objectiveIds: [],
    }),
  },
  runbook: {
    label: 'Runbook',
    kind: 'runbook',
    listElementKey: 'runbookList',
    endpoint: 'runbooks',
    detailPath: (id) => `/runbooks/${encodeURIComponent(id)}`,
    enumeratePath: '/runbooks/enumerate',
    createTemplate: () => ({
      fileName: 'runbook.md',
      title: 'Runbook',
      description: '',
      workflowProfileId: '',
      environmentId: '',
      environmentName: '',
      defaultCheckType: 'DeploymentVerification',
      parameters: [],
      steps: [],
      overviewMarkdown: '# Runbook',
      active: true,
    }),
  },
  runbookExecution: {
    label: 'Runbook Execution',
    kind: 'runbook-execution',
    listElementKey: 'runbookExecutionList',
    endpoint: 'runbook-executions',
    detailPath: (id) => `/runbook-executions/${encodeURIComponent(id)}`,
    enumeratePath: '/runbook-executions/enumerate',
    createSupported: false,
  },
  pipeline: {
    label: 'Pipeline',
    kind: 'pipeline',
    listElementKey: 'pipelineReferenceList',
    endpoint: 'pipelines',
    detailPath: (id) => `/pipelines/${encodeURIComponent(id)}`,
    enumeratePath: '/pipelines',
    createSupported: false,
    readOnly: true,
  },
  persona: {
    label: 'Persona',
    kind: 'persona',
    listElementKey: 'personaReferenceList',
    endpoint: 'personas',
    detailPath: (id) => `/personas/${encodeURIComponent(id)}`,
    enumeratePath: '/personas/enumerate',
    createSupported: false,
    readOnly: true,
  },
  promptTemplate: {
    label: 'Prompt Template',
    kind: 'prompt-template',
    listElementKey: 'promptTemplateReferenceList',
    endpoint: 'prompt-templates',
    detailPath: (id) => `/prompt-templates/${encodeURIComponent(id)}`,
    enumeratePath: '/prompt-templates/enumerate',
    createSupported: false,
    readOnly: true,
  },
};

function escapeHtml(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

const SHA256_CONSTANTS = [
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5,
  0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
  0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
  0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
  0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
  0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
  0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
  0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
  0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

function rotateRight(value, bits) {
  return (value >>> bits) | (value << (32 - bits));
}

// Fallback SHA-256 for non-secure HTTP contexts where SubtleCrypto is unavailable.
function sha256HexFallback(message) {
  const bytes = Array.from(new TextEncoder().encode(String(message)));
  const bitLength = bytes.length * 8;
  const highBits = Math.floor(bitLength / 0x100000000);
  const lowBits = bitLength >>> 0;

  bytes.push(0x80);
  while ((bytes.length % 64) !== 56) bytes.push(0);

  bytes.push((highBits >>> 24) & 0xff);
  bytes.push((highBits >>> 16) & 0xff);
  bytes.push((highBits >>> 8) & 0xff);
  bytes.push(highBits & 0xff);
  bytes.push((lowBits >>> 24) & 0xff);
  bytes.push((lowBits >>> 16) & 0xff);
  bytes.push((lowBits >>> 8) & 0xff);
  bytes.push(lowBits & 0xff);

  let h0 = 0x6a09e667;
  let h1 = 0xbb67ae85;
  let h2 = 0x3c6ef372;
  let h3 = 0xa54ff53a;
  let h4 = 0x510e527f;
  let h5 = 0x9b05688c;
  let h6 = 0x1f83d9ab;
  let h7 = 0x5be0cd19;

  for (let offset = 0; offset < bytes.length; offset += 64) {
    const schedule = new Array(64).fill(0);
    for (let index = 0; index < 16; index += 1) {
      const base = offset + (index * 4);
      schedule[index] = (
        (bytes[base] << 24) |
        (bytes[base + 1] << 16) |
        (bytes[base + 2] << 8) |
        bytes[base + 3]
      ) | 0;
    }

    for (let index = 16; index < 64; index += 1) {
      const sigma0 = rotateRight(schedule[index - 15], 7) ^ rotateRight(schedule[index - 15], 18) ^ (schedule[index - 15] >>> 3);
      const sigma1 = rotateRight(schedule[index - 2], 17) ^ rotateRight(schedule[index - 2], 19) ^ (schedule[index - 2] >>> 10);
      schedule[index] = (schedule[index - 16] + sigma0 + schedule[index - 7] + sigma1) | 0;
    }

    let a = h0;
    let b = h1;
    let c = h2;
    let d = h3;
    let e = h4;
    let f = h5;
    let g = h6;
    let h = h7;

    for (let index = 0; index < 64; index += 1) {
      const sigma1 = rotateRight(e, 6) ^ rotateRight(e, 11) ^ rotateRight(e, 25);
      const choice = (e & f) ^ (~e & g);
      const temp1 = (h + sigma1 + choice + SHA256_CONSTANTS[index] + schedule[index]) | 0;
      const sigma0 = rotateRight(a, 2) ^ rotateRight(a, 13) ^ rotateRight(a, 22);
      const majority = (a & b) ^ (a & c) ^ (b & c);
      const temp2 = (sigma0 + majority) | 0;

      h = g;
      g = f;
      f = e;
      e = (d + temp1) | 0;
      d = c;
      c = b;
      b = a;
      a = (temp1 + temp2) | 0;
    }

    h0 = (h0 + a) | 0;
    h1 = (h1 + b) | 0;
    h2 = (h2 + c) | 0;
    h3 = (h3 + d) | 0;
    h4 = (h4 + e) | 0;
    h5 = (h5 + f) | 0;
    h6 = (h6 + g) | 0;
    h7 = (h7 + h) | 0;
  }

  return [h0, h1, h2, h3, h4, h5, h6, h7]
    .map((value) => (value >>> 0).toString(16).padStart(8, '0'))
    .join('');
}

async function sha256Hex(message) {
  const text = String(message ?? '');
  if (window.crypto?.subtle) {
    const bytes = new TextEncoder().encode(text);
    const digest = await window.crypto.subtle.digest('SHA-256', bytes);
    return Array.from(new Uint8Array(digest))
      .map((value) => value.toString(16).padStart(2, '0'))
      .join('');
  }

  return sha256HexFallback(text);
}

function formatTimestamp(value) {
  if (!value) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '-';
  return date.toLocaleString();
}

function parseLineSeparatedValues(raw) {
  return String(raw || '')
    .split(/\r?\n/)
    .map((value) => value.trim())
    .filter(Boolean);
}

function joinLineSeparatedValues(values) {
  return Array.isArray(values) ? values.filter(Boolean).join('\n') : '';
}

function parseCommaSeparatedValues(raw) {
  return String(raw || '')
    .split(',')
    .map((value) => value.trim())
    .filter(Boolean);
}

function toLocalDateTimeInputValue(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const pad = (number) => String(number).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function fromLocalDateTimeInputValue(value) {
  const raw = String(value || '').trim();
  if (!raw) return null;
  const date = new Date(raw);
  if (Number.isNaN(date.getTime())) return null;
  return date.toISOString();
}

function safeJsonStringify(value) {
  return JSON.stringify(value, null, 2);
}

function parseJsonPayload(raw, fallbackMessage = 'Invalid JSON payload.') {
  try {
    return JSON.parse(String(raw || '{}'));
  } catch (error) {
    throw new Error(fallbackMessage);
  }
}

function pickFirst(values) {
  return Array.isArray(values) && values.length > 0 ? values[0] : '';
}

function coerceListPayload(payload, ...keys) {
  if (Array.isArray(payload)) return payload;
  for (const key of keys) {
    if (Array.isArray(payload?.[key])) return payload[key];
    const pascalKey = key ? `${key.charAt(0).toUpperCase()}${key.slice(1)}` : key;
    if (Array.isArray(payload?.[pascalKey])) return payload[pascalKey];
  }
  return [];
}

function coerceDetailPayload(payload, key) {
  if (payload?.[key]) return payload[key];
  const pascalKey = key ? `${key.charAt(0).toUpperCase()}${key.slice(1)}` : key;
  return payload?.[pascalKey] || payload;
}

function extractPrimaryId(payload) {
  if (!payload || typeof payload !== 'object') return '';
  if (payload.id) return payload.id;
  if (payload.Id) return payload.Id;
  for (const key of ['session', 'Session', 'voyage', 'Voyage', 'runbookExecution', 'RunbookExecution']) {
    if (payload[key]?.id) return payload[key].id;
    if (payload[key]?.Id) return payload[key].Id;
  }
  return '';
}

function badgeClass(value) {
  return String(value || '').toLowerCase().replace(/[^a-z0-9]+/g, '');
}

function renderBadge(text) {
  const value = String(text || 'unknown');
  return `<span class="badge ${badgeClass(value)}">${escapeHtml(value)}</span>`;
}

function renderStatusPill(label, value, extraClass = '') {
  const className = badgeClass(value) || 'unknown';
  return `
    <span class="summary-meta-item ${extraClass}">
      <span class="summary-meta-label">${escapeHtml(label)}</span>
      <span class="summary-meta-value ${extraClass ? `summary-meta-value-${extraClass}` : ''} ${className}">${escapeHtml(value || '-')}</span>
    </span>
  `;
}

function setFormStatus(element, message, kind) {
  if (!element) return;
  element.textContent = message || '';
  element.className = 'form-status';
  if (kind) element.classList.add(kind);
}

async function fetchJson(url, options = {}) {
  const skipProxySession = Boolean(options.skipProxySession);
  const request = {
    cache: 'no-store',
    ...options,
    headers: {
      ...(options.headers || {}),
    },
  };

  if (request.body !== undefined && typeof request.body !== 'string') {
    request.headers['Content-Type'] = 'application/json';
    request.body = JSON.stringify(request.body);
  }

  if (!skipProxySession && state.sessionToken) {
    request.headers[PROXY_SESSION_HEADER] = state.sessionToken;
  }

  const response = await fetch(url, request);
  const text = await response.text();
  let body = {};

  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = { error: text };
    }
  }

  if (!response.ok) {
    if (response.status === 401 && !String(url).includes('/api/v1/auth/')) {
      handleUnauthorizedProxySession(body.error || body.message || 'Proxy session expired.');
    }
    throw new Error(body.error || body.message || `Request failed: ${response.status}`);
  }

  return body;
}

function getStoredDeploymentId() {
  try {
    return localStorage.getItem(DEPLOYMENT_STORAGE_KEY);
  } catch {
    return null;
  }
}

function storeDeploymentId(instanceId) {
  try {
    if (instanceId) {
      localStorage.setItem(DEPLOYMENT_STORAGE_KEY, instanceId);
    } else {
      localStorage.removeItem(DEPLOYMENT_STORAGE_KEY);
    }
  } catch {
  }
}

function getStoredProxySessionToken() {
  try {
    return localStorage.getItem(PROXY_SESSION_STORAGE_KEY);
  } catch {
    return null;
  }
}

function storeProxySessionToken(sessionToken) {
  try {
    if (sessionToken) {
      localStorage.setItem(PROXY_SESSION_STORAGE_KEY, sessionToken);
    } else {
      localStorage.removeItem(PROXY_SESSION_STORAGE_KEY);
    }
  } catch {
  }
}

function getStoredTheme() {
  try {
    return localStorage.getItem(THEME_STORAGE_KEY);
  } catch {
    return null;
  }
}

function storeTheme(theme) {
  try {
    localStorage.setItem(THEME_STORAGE_KEY, theme);
  } catch {
  }
}

function getPreferredTheme() {
  const stored = getStoredTheme();
  if (stored === 'light' || stored === 'dark') return stored;
  if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) return 'dark';
  return 'light';
}

function syncThemeButtons() {
  const nextTheme = state.theme === 'dark' ? 'light' : 'dark';
  const icon = nextTheme === 'dark'
    ? '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 1 0 9.8 9.8z" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"></path></svg>'
    : '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="4" fill="none" stroke="currentColor" stroke-width="1.8"></circle><path d="M12 2v2.2M12 19.8V22M4.93 4.93l1.56 1.56M17.51 17.51l1.56 1.56M2 12h2.2M19.8 12H22M4.93 19.07l1.56-1.56M17.51 6.49l1.56-1.56" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"></path></svg>';
  const label = nextTheme === 'dark' ? 'Switch to dark mode' : 'Switch to light mode';

  if (elements.themeToggleButton) {
    elements.themeToggleButton.innerHTML = icon;
    elements.themeToggleButton.setAttribute('aria-label', label);
    elements.themeToggleButton.setAttribute('title', label);
  }

  if (elements.loginThemeToggleButton) {
    elements.loginThemeToggleButton.innerHTML = icon;
    elements.loginThemeToggleButton.setAttribute('aria-label', label);
    elements.loginThemeToggleButton.setAttribute('title', label);
  }
}

function syncThemeLogos() {
  if (elements.loginLogo) {
    elements.loginLogo.src = state.theme === 'dark'
      ? '/img/logo-light-grey.png'
      : '/img/logo-dark-grey.png';
  }
}

function applyTheme(theme) {
  state.theme = theme === 'dark' ? 'dark' : 'light';
  document.documentElement.setAttribute('data-theme', state.theme);
  syncThemeButtons();
  syncThemeLogos();
}

function toggleTheme() {
  const nextTheme = state.theme === 'dark' ? 'light' : 'dark';
  applyTheme(nextTheme);
  storeTheme(nextTheme);
}

function getInstanceIdValue(instance) {
  return instance?.instanceId || instance?.InstanceId || '';
}

function getInstanceStateValue(instance) {
  return instance?.state || instance?.State || '';
}

function getInstanceArmadaVersionValue(instance) {
  return instance?.armadaVersion || instance?.ArmadaVersion || '';
}

function getInstanceProtocolVersionValue(instance) {
  return instance?.protocolVersion || instance?.ProtocolVersion || '';
}

function getInstanceLastErrorValue(instance) {
  return instance?.lastError || instance?.LastError || '';
}

function getInstanceRemoteAddressValue(instance) {
  return instance?.remoteAddress || instance?.RemoteAddress || '';
}

function getInstanceById(instanceId) {
  const normalized = String(instanceId || '').trim().toLowerCase();
  return state.instances.find((instance) => getInstanceIdValue(instance).toLowerCase() === normalized) || null;
}

function setLoginStatus(message, kind) {
  setFormStatus(elements.loginStatus, message, kind);
}

function getPendingInstance() {
  return getInstanceById(state.pendingInstanceId);
}

function setLoginStep(step) {
  state.loginStep = step;

  const steps = [
    { key: 'proxy-password', element: elements.loginStepProxy },
    { key: 'deployment-select', element: elements.loginStepDeployment },
    { key: 'deployment-password', element: elements.loginStepAccess },
  ];

  const activeIndex = steps.findIndex((entry) => entry.key === step);
  steps.forEach((entry, index) => {
    if (!entry.element) return;
    entry.element.classList.toggle('is-active', index === activeIndex);
    entry.element.classList.toggle('is-complete', activeIndex > index);
  });

  if (elements.proxyPasswordStep) {
    elements.proxyPasswordStep.classList.toggle('hidden', step !== 'proxy-password');
  }

  if (elements.deploymentSelectionStep) {
    elements.deploymentSelectionStep.classList.toggle('hidden', step !== 'deployment-select');
  }

  if (elements.deploymentPasswordStep) {
    elements.deploymentPasswordStep.classList.toggle('hidden', step !== 'deployment-password');
  }

  if (elements.loginCopy) {
    if (step === 'proxy-password') {
      elements.loginCopy.textContent = 'Enter the shared password to unlock this proxy.';
    } else if (step === 'deployment-select') {
      elements.loginCopy.textContent = 'Choose a connected deployment.';
    } else {
      elements.loginCopy.textContent = 'Enter the password for the selected deployment.';
    }
  }

  const pendingInstance = getPendingInstance();
  if (elements.selectedInstanceId) {
    elements.selectedInstanceId.textContent = pendingInstance ? getInstanceIdValue(pendingInstance) : '-';
  }

  if (elements.selectedInstanceMeta) {
    elements.selectedInstanceMeta.textContent = pendingInstance
      ? `${getInstanceArmadaVersionValue(pendingInstance) || 'unknown version'} | ${getInstanceProtocolVersionValue(pendingInstance) || 'unknown protocol'}`
      : 'Choose a deployment to continue.';
  }
}

function normalizeSharedPassword(password) {
  return String(password || '').trim();
}

async function buildBrowserLoginProof(password, nonce) {
  const normalizedPassword = normalizeSharedPassword(password);
  if (!normalizedPassword) {
    throw new Error('Enter the shared password to unlock this proxy.');
  }
  const passwordHash = await sha256Hex(normalizedPassword);
  const normalizedNonce = String(nonce || '').trim().toLowerCase();
  return sha256Hex(`proxy-browser-login:proxy:${normalizedNonce}:${passwordHash}`);
}

function setProxySession(sessionToken) {
  state.sessionToken = sessionToken || null;
  state.isAuthenticated = Boolean(state.sessionToken);
  storeProxySessionToken(state.sessionToken);
}

function setDeploymentChrome() {
  const instance = getInstanceById(state.selectedInstanceId);
  const summaryHealth = state.summary?.health || {};
  const tunnel = summaryHealth.remoteTunnel || {};
  const statusValue = tunnel.state || getInstanceStateValue(instance) || 'Offline';

  elements.currentDeploymentLabel.textContent = state.selectedInstanceId || '-';
  elements.currentDeploymentState.textContent = String(statusValue);
  elements.currentDeploymentState.className = `tag ${badgeClass(statusValue) || 'idle'}`;

  elements.sidebarDeploymentId.textContent = state.selectedInstanceId || '-';
  elements.sidebarDeploymentState.textContent = String(statusValue);
  elements.sidebarDeploymentState.className = `tag ${badgeClass(statusValue) || 'idle'}`;
}

function closeSidebar() {
  state.sidebarOpen = false;
  elements.sidebar.classList.remove('sidebar-open');
  elements.sidebarOverlay.classList.add('hidden');
}

function openSidebar() {
  state.sidebarOpen = true;
  elements.sidebar.classList.add('sidebar-open');
  elements.sidebarOverlay.classList.remove('hidden');
}

function renderSessionState() {
  const authenticated = state.isAuthenticated && Boolean(state.selectedInstanceId);
  elements.loginView.classList.toggle('hidden', authenticated);
  elements.appView.classList.toggle('hidden', !authenticated);
  if (authenticated) {
    setDeploymentChrome();
  } else {
    closeSidebar();
    setLoginStep(state.loginStep || (state.isAuthenticated ? 'deployment-select' : 'proxy-password'));
  }
}

function isAnyModalOpen() {
  return document.querySelector('.modal-shell:not(.hidden)') !== null;
}

function openModal(element) {
  if (!element) return;
  element.classList.remove('hidden');
  document.body.classList.add('modal-open');
  closeSidebar();
}

function closeModal(element) {
  if (!element) return;
  element.classList.add('hidden');
  if (!isAnyModalOpen()) document.body.classList.remove('modal-open');
}

function closeModalById(id) {
  if (!id) return;
  const element = document.getElementById(id);
  if (!element) return;
  closeModal(element);
  if (id === 'detailModal') state.detailModal = null;
}

function closeAllModals() {
  document.querySelectorAll('.modal-shell').forEach((modal) => modal.classList.add('hidden'));
  document.body.classList.remove('modal-open');
  state.detailModal = null;
}

function buildOptionMarkup(rows, placeholder, getValue, getLabel) {
  const options = [`<option value="">${escapeHtml(placeholder)}</option>`];
  for (const row of rows) {
    const value = String(getValue(row) ?? '');
    const label = String(getLabel(row) ?? value);
    options.push(`<option value="${escapeHtml(value)}">${escapeHtml(label)}</option>`);
  }
  return options.join('');
}

function populateSelect(select, rows, placeholder, getValue, getLabel, selectedValue = '') {
  if (!select) return;
  select.innerHTML = buildOptionMarkup(rows, placeholder, getValue, getLabel);
  select.value = selectedValue || '';
}

function vesselOptionLabel(vessel) {
  const name = vessel?.name || vessel?.id || 'Unnamed vessel';
  const id = vessel?.id ? ` | ${vessel.id}` : '';
  return `${name}${id}`;
}

function pipelineOptionLabel(pipeline) {
  const name = getPipelineNameValue(pipeline) || 'Unnamed pipeline';
  const id = getPipelineIdValue(pipeline);
  return id && id !== name ? `${name} | ${id}` : name;
}

function getActivePlaybooks() {
  return (state.playbooks || [])
    .filter((playbook) => playbook?.active !== false)
    .sort((left, right) => String(left.fileName || left.id || '').localeCompare(String(right.fileName || right.id || '')));
}

function normalizeDispatchPlaybookSelections() {
  const activeIds = new Set(getActivePlaybooks().map((playbook) => playbook.id));
  state.dispatchSelectedPlaybooks = (state.dispatchSelectedPlaybooks || [])
    .filter((selection) => selection?.playbookId && activeIds.has(selection.playbookId))
    .map((selection) => ({
      playbookId: selection.playbookId,
      deliveryMode: PLAYBOOK_DELIVERY_MODES.some((mode) => mode.value === selection.deliveryMode)
        ? selection.deliveryMode
        : 'InlineFullContent',
    }));
}

function getDispatchPlaybookSelection(playbookId) {
  return (state.dispatchSelectedPlaybooks || []).find((selection) => selection.playbookId === playbookId) || null;
}

function renderDispatchPlaybookSelection() {
  if (!elements.dispatchPlaybookList) return;
  normalizeDispatchPlaybookSelections();

  const activePlaybooks = getActivePlaybooks();
  if (activePlaybooks.length === 0) {
    elements.dispatchPlaybookList.innerHTML = '<div class="text-muted">No active playbooks are available for this deployment.</div>';
    return;
  }

  const selectionOrder = new Map(state.dispatchSelectedPlaybooks.map((selection, index) => [selection.playbookId, index + 1]));
  elements.dispatchPlaybookList.innerHTML = activePlaybooks.map((playbook) => {
    const selection = getDispatchPlaybookSelection(playbook.id);
    const selected = Boolean(selection);
    const order = selectionOrder.get(playbook.id);
    const modeDescription = selected
      ? PLAYBOOK_DELIVERY_MODES.find((mode) => mode.value === selection.deliveryMode)?.description || ''
      : 'Select this playbook to apply it to every mission in the dispatch.';

    return `
      <label class="playbook-selection-card${selected ? ' selected' : ''}">
        <div class="playbook-selection-main">
          <div class="playbook-selection-toggle">
            <input type="checkbox" data-playbook-toggle value="${escapeHtml(playbook.id)}" ${selected ? 'checked' : ''}>
          </div>
          <div class="playbook-selection-copy">
            <div class="playbook-selection-title-row">
              <strong>${escapeHtml(playbook.fileName || playbook.id || 'Playbook')}</strong>
              ${selected ? `<span class="tag working">#${order}</span>` : '<span class="tag idle">Optional</span>'}
            </div>
            <div class="text-dim">${escapeHtml(playbook.description || 'No description')}</div>
            <div class="playbook-selection-meta text-dim">
              <span>${escapeHtml(`${String(playbook.content || '').length.toLocaleString()} chars`)}</span>
              <span>${escapeHtml(`Updated ${formatTimestamp(playbook.lastUpdateUtc)}`)}</span>
            </div>
          </div>
        </div>
        <div class="playbook-selection-mode">
          <label class="field">
            <span>Delivery Mode</span>
            <select data-playbook-mode data-playbook-id="${escapeHtml(playbook.id)}" ${selected ? '' : 'disabled'}>
              ${PLAYBOOK_DELIVERY_MODES.map((mode) => `
                <option value="${escapeHtml(mode.value)}" ${selection?.deliveryMode === mode.value ? 'selected' : ''}>${escapeHtml(mode.label)}</option>
              `).join('')}
            </select>
          </label>
          <div class="text-dim">${escapeHtml(modeDescription)}</div>
        </div>
      </label>
    `;
  }).join('');
}

function playbookDeliveryModeLabel(value) {
  return PLAYBOOK_DELIVERY_MODES.find((mode) => mode.value === value)?.label || value || 'Inline full content';
}

function fleetOptionLabel(fleet) {
  const name = fleet?.name || fleet?.id || 'Unnamed fleet';
  const id = fleet?.id ? ` | ${fleet.id}` : '';
  return `${name}${id}`;
}

function getPipelineIdValue(pipeline) {
  return pipeline?.id || pipeline?.Id || pipeline?.name || pipeline?.Name || '';
}

function getPipelineNameValue(pipeline) {
  return pipeline?.name || pipeline?.Name || pipeline?.id || pipeline?.Id || '';
}

function buildPipelineOptionRows() {
  const rows = [];
  const seenIds = new Set();
  const seenNames = new Set();

  const addPipeline = (pipeline) => {
    const id = String(getPipelineIdValue(pipeline) || '').trim();
    const name = String(getPipelineNameValue(pipeline) || '').trim();
    if (!id && !name) return;

    const normalizedId = id.toLowerCase();
    const normalizedName = name.toLowerCase();
    if ((normalizedId && seenIds.has(normalizedId)) || (normalizedName && seenNames.has(normalizedName))) return;

    rows.push({
      id: id || name,
      name: name || id,
    });

    if (normalizedId) seenIds.add(normalizedId);
    if (normalizedName) seenNames.add(normalizedName);
  };

  for (const pipeline of state.pipelines || []) addPipeline(pipeline);
  for (const row of [...(state.fleets || []), ...(state.vessels || [])]) {
    if (row?.defaultPipelineId) addPipeline({ id: row.defaultPipelineId, name: row.defaultPipelineId });
  }
  for (const selectedValue of [
    elements.dispatchPipelineId?.value,
    elements.fleetDefaultPipelineId?.value,
    elements.vesselDefaultPipelineId?.value,
  ]) {
    if (selectedValue) addPipeline({ id: selectedValue, name: selectedValue });
  }

  return rows.sort((left, right) => pipelineOptionLabel(left).localeCompare(pipelineOptionLabel(right)));
}

function populateFormSelects() {
  const pipelineRows = buildPipelineOptionRows();
  const captainRows = state.summary?.recentCaptains || [];
  populateSelect(elements.dispatchVesselId, state.vessels, 'Select a vessel', (row) => row.id, vesselOptionLabel, elements.dispatchVesselId.value);
  populateSelect(elements.dispatchPipelineId, pipelineRows, 'Use default pipeline', getPipelineIdValue, pipelineOptionLabel, elements.dispatchPipelineId.value);
  populateSelect(elements.fleetDefaultPipelineId, pipelineRows, 'No default pipeline', getPipelineIdValue, pipelineOptionLabel, elements.fleetDefaultPipelineId.value);
  populateSelect(elements.vesselFleetId, state.fleets, 'No fleet', (row) => row.id, fleetOptionLabel, elements.vesselFleetId.value);
  populateSelect(elements.vesselDefaultPipelineId, pipelineRows, 'No default pipeline', getPipelineIdValue, pipelineOptionLabel, elements.vesselDefaultPipelineId.value);
  populateSelect(elements.missionVesselId, state.vessels, 'Select a vessel', (row) => row.id, vesselOptionLabel, elements.missionVesselId.value);
  populateSelect(elements.backlogBrowseVesselId, state.vessels, 'All vessels', (row) => row.id, vesselOptionLabel, elements.backlogBrowseVesselId.value);
  populateSelect(elements.backlogVesselId, state.vessels, 'No vessel', (row) => row.id, vesselOptionLabel, elements.backlogVesselId.value);
  populateSelect(elements.planningBrowseCaptainId, captainRows, 'All recent captains', (row) => row.id, (row) => `${row.name || row.id || 'Captain'}${row.id ? ` | ${row.id}` : ''}`, elements.planningBrowseCaptainId.value);
  populateSelect(elements.planningCaptainId, captainRows, 'Select a captain', (row) => row.id, (row) => `${row.name || row.id || 'Captain'}${row.id ? ` | ${row.id}` : ''}`, elements.planningCaptainId.value);
  populateSelect(elements.planningBrowseVesselId, state.vessels, 'All vessels', (row) => row.id, vesselOptionLabel, elements.planningBrowseVesselId.value);
  populateSelect(elements.planningVesselId, state.vessels, 'Select a vessel', (row) => row.id, vesselOptionLabel, elements.planningVesselId.value);
  populateSelect(elements.planningPipelineId, pipelineRows, 'Use vessel default pipeline', getPipelineIdValue, pipelineOptionLabel, elements.planningPipelineId.value);
  populateSelect(elements.captainToolsCaptainId, captainRows, 'Select a captain', (row) => row.id, (row) => `${row.name || row.id || 'Captain'}${row.id ? ` | ${row.id}` : ''}`, elements.captainToolsCaptainId.value);
  populateSelect(elements.workspaceVesselId, state.vessels, 'Select a vessel', (row) => row.id, vesselOptionLabel, elements.workspaceVesselId.value);
  populateSelect(elements.apiExplorerPreset, API_EXPLORER_PRESETS, 'Choose a safe GET route', (row) => row.key, (row) => `${row.label} | ${row.path}`, elements.apiExplorerPreset.value);
  renderDispatchPlaybookSelection();
}

function applyApiExplorerPreset() {
  const presetKey = elements.apiExplorerPreset?.value || '';
  const preset = API_EXPLORER_PRESETS.find((entry) => entry.key === presetKey);
  if (!preset) {
    setFormStatus(elements.apiExplorerStatusText, 'Choose a safe GET preset first.', 'error');
    return;
  }

  elements.apiExplorerMethod.value = preset.method;
  elements.apiExplorerPath.value = preset.path;
  elements.apiExplorerBody.value = preset.method === 'GET' || preset.method === 'DELETE'
    ? ''
    : safeJsonStringify(preset.body || {});
  setFormStatus(elements.apiExplorerStatusText, `Loaded ${preset.label} preset.`, 'success');
}

function resetDispatchForm() {
  elements.dispatchForm.reset();
  state.dispatchSelectedPlaybooks = [];
  populateFormSelects();
  elements.dispatchPriority.value = '100';
  setButtonBusy(elements.dispatchSubmitButton, false, 'Dispatch', 'Dispatching...');
  setFormStatus(elements.dispatchFormStatus, '', null);
}

function populateFleetForm(fleet) {
  state.selectedFleetId = fleet?.id || null;
  state.editingFleet = fleet || null;
  elements.fleetName.value = fleet?.name || '';
  elements.fleetDescription.value = fleet?.description || '';
  populateFormSelects();
  elements.fleetDefaultPipelineId.value = fleet?.defaultPipelineId || '';
  elements.fleetActive.checked = fleet?.active !== false;
  setFormStatus(elements.fleetFormStatus, '', null);
}

function resetFleetForm() {
  if (state.editingFleet && state.selectedFleetId) {
    populateFleetForm(state.editingFleet);
    return;
  }

  state.selectedFleetId = null;
  state.editingFleet = null;
  elements.fleetForm.reset();
  populateFormSelects();
  elements.fleetActive.checked = true;
  setFormStatus(elements.fleetFormStatus, '', null);
}

function openFleetModal(fleet = null) {
  populateFleetForm(fleet);
  elements.fleetModalTitle.textContent = fleet ? 'Edit Fleet' : 'Add Fleet';
  elements.fleetModalSubtitle.textContent = fleet?.id || 'Create a new fleet.';
  openModal(elements.fleetModal);
}

function populateVesselForm(vessel) {
  state.selectedVesselId = vessel?.id || null;
  state.editingVessel = vessel || null;
  elements.vesselName.value = vessel?.name || '';
  elements.vesselRepoUrl.value = vessel?.repoUrl || '';
  elements.vesselWorkingDirectory.value = vessel?.workingDirectory || '';
  elements.vesselDefaultBranch.value = vessel?.defaultBranch || 'main';
  populateFormSelects();
  elements.vesselFleetId.value = vessel?.fleetId || '';
  elements.vesselDefaultPipelineId.value = vessel?.defaultPipelineId || '';
  elements.vesselAllowConcurrentMissions.checked = Boolean(vessel?.allowConcurrentMissions);
  elements.vesselActive.checked = vessel?.active !== false;
  setFormStatus(elements.vesselFormStatus, '', null);
}

function resetVesselForm() {
  if (state.editingVessel && state.selectedVesselId) {
    populateVesselForm(state.editingVessel);
    return;
  }

  state.selectedVesselId = null;
  state.editingVessel = null;
  elements.vesselForm.reset();
  populateFormSelects();
  elements.vesselDefaultBranch.value = 'main';
  elements.vesselAllowConcurrentMissions.checked = false;
  elements.vesselActive.checked = true;
  setFormStatus(elements.vesselFormStatus, '', null);
}

function openVesselModal(vessel = null) {
  populateVesselForm(vessel);
  elements.vesselModalTitle.textContent = vessel ? 'Edit Vessel' : 'Add Vessel';
  elements.vesselModalSubtitle.textContent = vessel?.id || 'Register a repository for remote work.';
  openModal(elements.vesselModal);
}

function setButtonBusy(button, isBusy, idleLabel, busyLabel) {
  if (!button) return;
  button.disabled = Boolean(isBusy);
  button.textContent = isBusy ? busyLabel : idleLabel;
}

function populateMissionForm(mission) {
  state.selectedMissionId = mission?.id || null;
  state.editingMission = mission || null;
  elements.missionTitle.value = mission?.title || '';
  elements.missionDescription.value = mission?.description || '';
  populateFormSelects();
  elements.missionVesselId.value = mission?.vesselId || '';
  elements.missionVoyageId.value = mission?.voyageId || '';
  elements.missionPersona.value = mission?.persona || '';
  elements.missionPriority.value = mission?.priority != null ? String(mission.priority) : '100';
  setFormStatus(elements.missionFormStatus, '', null);
}

function resetMissionForm() {
  if (state.editingMission && state.selectedMissionId) {
    populateMissionForm(state.editingMission);
    return;
  }

  state.selectedMissionId = null;
  state.editingMission = null;
  elements.missionForm.reset();
  populateFormSelects();
  elements.missionPriority.value = '100';
  setFormStatus(elements.missionFormStatus, '', null);
}

function openMissionModal(mission) {
  populateMissionForm(mission);
  elements.missionModalTitle.textContent = 'Edit Mission';
  elements.missionModalSubtitle.textContent = mission?.id || 'Update mission details.';
  openModal(elements.missionModal);
}

function populatePlaybookForm(playbook) {
  state.selectedPlaybookId = playbook?.id || null;
  state.editingPlaybook = playbook || null;
  elements.playbookFileName.value = playbook?.fileName || '';
  elements.playbookDescription.value = playbook?.description || '';
  elements.playbookContent.value = playbook?.content || '';
  elements.playbookActive.checked = playbook?.active !== false;
  elements.playbookDeleteButton.classList.toggle('hidden', !playbook?.id);
  setFormStatus(elements.playbookFormStatus, '', null);
}

function resetPlaybookForm() {
  if (state.editingPlaybook && state.selectedPlaybookId) {
    populatePlaybookForm(state.editingPlaybook);
    return;
  }

  state.selectedPlaybookId = null;
  state.editingPlaybook = null;
  elements.playbookForm.reset();
  elements.playbookActive.checked = true;
  elements.playbookDeleteButton.classList.add('hidden');
  setFormStatus(elements.playbookFormStatus, '', null);
}

function openPlaybookModal(playbook = null) {
  populatePlaybookForm(playbook);
  elements.playbookModalTitle.textContent = playbook ? 'Edit Playbook' : 'Add Playbook';
  elements.playbookModalSubtitle.textContent = playbook?.id || 'Create reusable markdown guidance for dispatch.';
  openModal(elements.playbookModal);
}

function populateBacklogForm(objective) {
  state.selectedObjectiveId = objective?.id || null;
  state.editingObjective = objective || null;
  elements.backlogTitle.value = objective?.title || '';
  elements.backlogDescription.value = objective?.description || '';
  elements.backlogStatus.value = objective?.status || 'Draft';
  elements.backlogBacklogState.value = objective?.backlogState || 'Inbox';
  elements.backlogKind.value = objective?.kind || 'Feature';
  elements.backlogPriority.value = objective?.priority || 'P2';
  elements.backlogEffort.value = objective?.effort || 'M';
  elements.backlogOwner.value = objective?.owner || '';
  populateFormSelects();
  elements.backlogVesselId.value = pickFirst(objective?.vesselIds) || '';
  elements.backlogTargetVersion.value = objective?.targetVersion || '';
  elements.backlogDueUtc.value = toLocalDateTimeInputValue(objective?.dueUtc);
  elements.backlogParentObjectiveId.value = objective?.parentObjectiveId || '';
  elements.backlogBlockedByObjectiveIds.value = (objective?.blockedByObjectiveIds || []).join(', ');
  elements.backlogTags.value = joinLineSeparatedValues(objective?.tags);
  elements.backlogAcceptanceCriteria.value = joinLineSeparatedValues(objective?.acceptanceCriteria);
  elements.backlogNonGoals.value = joinLineSeparatedValues(objective?.nonGoals);
  elements.backlogRolloutConstraints.value = joinLineSeparatedValues(objective?.rolloutConstraints);
  elements.backlogEvidenceLinks.value = joinLineSeparatedValues(objective?.evidenceLinks);
  elements.backlogDeleteButton.classList.toggle('hidden', !objective?.id);
  setFormStatus(elements.backlogFormStatus, '', null);
}

function resetBacklogForm() {
  if (state.editingObjective && state.selectedObjectiveId) {
    populateBacklogForm(state.editingObjective);
    return;
  }

  state.selectedObjectiveId = null;
  state.editingObjective = null;
  elements.backlogForm.reset();
  populateFormSelects();
  elements.backlogStatus.value = 'Draft';
  elements.backlogBacklogState.value = 'Inbox';
  elements.backlogKind.value = 'Feature';
  elements.backlogPriority.value = 'P2';
  elements.backlogEffort.value = 'M';
  elements.backlogDeleteButton.classList.add('hidden');
  setFormStatus(elements.backlogFormStatus, '', null);
}

function openBacklogModal(objective = null) {
  populateBacklogForm(objective);
  elements.backlogModalTitle.textContent = objective ? 'Edit Backlog Item' : 'Add Backlog Item';
  elements.backlogModalSubtitle.textContent = objective?.id || 'Capture future work with enough context for refinement and planning.';
  openModal(elements.backlogModal);
}

function resetPlanningForm() {
  elements.planningForm.reset();
  populateFormSelects();
  setFormStatus(elements.planningFormStatus, '', null);
}

function openPlanningModal(seed = null) {
  resetPlanningForm();
  elements.planningModalTitle.textContent = 'New Planning Session';
  elements.planningModalSubtitle.textContent = 'Reserve a captain, attach context, and start the planning transcript.';
  if (seed?.title) elements.planningTitle.value = seed.title;
  if (seed?.objectiveId) elements.planningObjectiveId.value = seed.objectiveId;
  if (seed?.vesselId) elements.planningVesselId.value = seed.vesselId;
  if (seed?.pipelineId) elements.planningPipelineId.value = seed.pipelineId;
  if (seed?.initialMessage) elements.planningInitialMessage.value = seed.initialMessage;
  openModal(elements.planningModal);
}

function resetResourceEditor() {
  if (!state.resourceEditor) {
    elements.resourceEditorForm.reset();
    elements.resourceEditorDeleteButton.classList.add('hidden');
    setFormStatus(elements.resourceEditorStatus, '', null);
    return;
  }

  elements.resourceEditorPayload.value = state.resourceEditor.initialPayloadText || '';
  elements.resourceEditorDeleteButton.classList.toggle('hidden', !state.resourceEditor.id || Boolean(state.resourceEditor.readOnly));
  setFormStatus(elements.resourceEditorStatus, '', null);
}

function openResourceEditor(resourceKey, mode, payload = null, id = null) {
  const definition = RESOURCE_DEFINITIONS[resourceKey];
  if (!definition) return;

  const editablePayload = payload ?? (definition.createTemplate ? definition.createTemplate() : {});
  const initialPayloadText = safeJsonStringify(editablePayload);
  state.resourceEditor = {
    resourceKey,
    mode,
    id,
    readOnly: Boolean(definition.readOnly),
    initialPayloadText,
  };
  elements.resourceEditorTitle.textContent = `${mode === 'create' ? 'Create' : 'Edit'} ${definition.label}`;
  elements.resourceEditorSubtitle.textContent = definition.readOnly
    ? `${definition.label} is exposed read-only in the proxy.`
    : `Review the ${definition.label.toLowerCase()} payload before saving it remotely.`;
  elements.resourceEditorHelp.textContent = definition.readOnly
    ? 'This reference surface is read-only in the proxy shell.'
    : 'Only include fields accepted by the underlying Armada route.';
  elements.resourceEditorSaveButton.classList.toggle('hidden', Boolean(definition.readOnly));
  elements.resourceEditorPayload.readOnly = Boolean(definition.readOnly);
  resetResourceEditor();
  openModal(elements.resourceEditorModal);
}

function resetProxyState() {
  state.summary = null;
  state.fleets = [];
  state.vessels = [];
  state.pipelines = [];
  state.playbooks = [];
  state.objectives = [];
  state.planningSessions = [];
  state.workflowProfiles = [];
  state.checkRuns = [];
  state.environments = [];
  state.releases = [];
  state.deployments = [];
  state.incidents = [];
  state.runbooks = [];
  state.runbookExecutions = [];
  state.requestHistory = [];
  state.requestHistorySummary = null;
  state.personas = [];
  state.promptTemplates = [];
  state.captainToolAccess = {};
  state.workspaceSnapshot = null;
  state.selectedFleetId = null;
  state.selectedVesselId = null;
  state.selectedMissionId = null;
  state.selectedPlaybookId = null;
  state.selectedObjectiveId = null;
  state.selectedPlanningSessionId = null;
  state.selectedPlanningMessageId = '';
  state.editingFleet = null;
  state.editingVessel = null;
  state.editingMission = null;
  state.editingPlaybook = null;
  state.editingObjective = null;
  state.planningDetail = null;
  state.planningComposer = '';
  state.planningDraftTitle = '';
  state.planningDraftDescription = '';
  state.planningWorkspaceLoading = false;
  state.planningWorkspaceStatus = { message: '', kind: null };
  state.planningSending = false;
  state.planningSummarizing = false;
  state.planningDispatching = false;
  state.planningStopping = false;
  state.planningDeleting = false;
  state.dispatchSelectedPlaybooks = [];
  state.resourceEditor = null;
  closeAllModals();
  resetFleetForm();
  resetVesselForm();
  resetDispatchForm();
  resetMissionForm();
  resetPlaybookForm();
  resetBacklogForm();
  resetPlanningForm();
  resetResourceEditor();
  renderPlanningWorkspace();
}

function returnToDeploymentSelection(message = '', prefill = '') {
  state.selectedInstanceId = null;
  state.pendingInstanceId = String(prefill || '').trim() || null;
  storeDeploymentId(null);
  resetProxyState();
  setLoginStep('deployment-select');
  renderSessionState();
  if (message) setLoginStatus(message, 'error');
  else setLoginStatus('', null);
}

function handleUnauthorizedProxySession(message) {
  if (!state.isAuthenticated && !state.sessionToken) return;
  logoutToLogin(message || 'Proxy session expired. Sign in again.');
}

async function authenticateInstance(instanceId) {
  if (!state.isAuthenticated || !state.sessionToken) {
    setLoginStatus('Enter the shared password to unlock this proxy first.', 'error');
    return;
  }

  const normalized = String(instanceId || '').trim();
  if (!normalized) {
    setLoginStatus('Deployment ID is required.', 'error');
    return;
  }

  let instance = getInstanceById(normalized);
  if (!instance) {
    try {
      await loadInstances();
    } catch {
    }
    instance = getInstanceById(normalized);
  }

  if (!instance) {
    setLoginStatus(`No Armada deployment with identifier "${normalized}" is connected to this proxy.`, 'error');
    return;
  }

  const resolvedInstanceId = getInstanceIdValue(instance);
  state.selectedInstanceId = resolvedInstanceId;
  state.pendingInstanceId = null;
  state.isAuthenticated = true;
  storeDeploymentId(resolvedInstanceId);
  if (elements.deploymentPassword) {
    elements.deploymentPassword.value = '';
  }
  setLoginStatus('', null);
  resetProxyState();
  renderSessionState();
  await loadSelectedInstance();
}

function logoutToLogin(message = '') {
  state.selectedInstanceId = null;
  state.pendingInstanceId = null;
  setProxySession(null);
  state.instances = [];
  storeDeploymentId(null);
  resetProxyState();
  renderInstanceList();
  elements.instanceCount.textContent = '0';
  setLoginStep('proxy-password');
  renderSessionState();
  elements.loginPassword.value = '';
  if (elements.deploymentPassword) {
    elements.deploymentPassword.value = '';
  }
  if (message) setLoginStatus(message, 'error');
  else setLoginStatus('', null);
}

async function selectInstanceForLogin(instanceId) {
  if (!state.isAuthenticated || !state.sessionToken) {
    setLoginStep('proxy-password');
    setLoginStatus('Enter the shared password to unlock this proxy first.', 'error');
    return;
  }

  const normalized = String(instanceId || '').trim();
  if (!normalized) {
    setLoginStatus('Select a deployment to continue.', 'error');
    return;
  }

  let instance = getInstanceById(normalized);
  if (!instance) {
    try {
      await loadInstances();
    } catch {
    }
    instance = getInstanceById(normalized);
  }

  if (!instance) {
    setLoginStatus(`No Armada deployment with identifier "${normalized}" is connected to this proxy.`, 'error');
    return;
  }

  state.pendingInstanceId = getInstanceIdValue(instance);
  if (elements.deploymentPassword) {
    elements.deploymentPassword.value = '';
    elements.deploymentPassword.focus();
  }
  setLoginStep('deployment-password');
  setLoginStatus('', null);
  renderSessionState();
}

function instanceBaseUrl() {
  return `/api/v1/instances/${encodeURIComponent(state.selectedInstanceId)}`;
}

function buildQuery(params) {
  const query = new URLSearchParams();
  Object.entries(params || {}).forEach(([key, value]) => {
    if (value === null || value === undefined) return;
    if (String(value).trim() === '') return;
    query.set(key, String(value).trim());
  });
  const serialized = query.toString();
  return serialized ? `?${serialized}` : '';
}

async function loadInstances() {
  const data = await fetchJson('/api/v1/instances');
  state.instances = data.instances || data.Instances || [];
  elements.instanceCount.textContent = String(data.count || data.Count || state.instances.length || 0);
  renderInstanceList();

  if (state.isAuthenticated && state.selectedInstanceId) {
    if (!getInstanceById(state.selectedInstanceId)) {
      const missingId = state.selectedInstanceId;
      returnToDeploymentSelection(`Deployment "${missingId}" is no longer registered with this proxy.`, missingId);
      return;
    }
    await loadSelectedInstance();
  } else {
    if (state.isAuthenticated && state.pendingInstanceId && !getInstanceById(state.pendingInstanceId)) {
      const missingId = state.pendingInstanceId;
      state.pendingInstanceId = null;
      setLoginStep('deployment-select');
      setLoginStatus(`Deployment "${missingId}" is not currently connected to this proxy.`, 'error');
    }
    renderSessionState();
  }
}

async function loadSelectedInstance() {
  if (!state.selectedInstanceId) return;

  try {
    const base = instanceBaseUrl();
    const [summary, fleets, vessels, pipelines, playbooks] = await Promise.all([
      fetchJson(`${base}/summary`),
      fetchJson(`${base}/fleets?limit=24`),
      fetchJson(`${base}/vessels?limit=48`),
      fetchJson(`${base}/pipelines?limit=48`).catch(() => ({ pipelines: [] })),
      fetchJson(`${base}/playbooks?limit=48`).catch(() => ({ playbooks: [] })),
    ]);

    state.summary = summary;
    state.fleets = fleets.fleets || [];
    state.vessels = vessels.vessels || [];
    state.pipelines = pipelines.pipelines || [];
    state.playbooks = playbooks.playbooks || [];
    populateFormSelects();
    renderSessionState();
    renderSelectedInstance();
  } catch (error) {
    state.summary = null;
    renderSessionState();
    elements.emptyState.classList.remove('hidden');
    elements.instanceWorkspace.classList.add('hidden');
    elements.emptyState.innerHTML = `
      <h2>Deployment Unavailable</h2>
      <p>${escapeHtml(error instanceof Error ? error.message : 'Unable to load deployment summary through the proxy.')}</p>
    `;
    throw error;
  }
}

async function unlockProxySession(password) {
  const challenge = await fetchJson('/api/v1/auth/challenge', { skipProxySession: true });
  const nonce = challenge.nonce || challenge.Nonce || '';
  if (!nonce) {
    throw new Error('Proxy did not return a login challenge.');
  }

  const proofSha256 = await buildBrowserLoginProof(password, nonce);
  const login = await fetchJson('/api/v1/auth/login', {
    method: 'POST',
    body: { nonce, proofSha256 },
    skipProxySession: true,
  });

  const sessionToken = login.token || login.Token || '';
  if (!sessionToken) {
    throw new Error('Proxy did not return a session token.');
  }

  setProxySession(sessionToken);
  await loadInstances();
}

async function revokeProxySession() {
  if (!state.sessionToken) {
    return;
  }

  try {
    await fetchJson('/api/v1/auth/logout', { method: 'POST' });
  } catch {
  }
}

function renderInstanceList() {
  elements.instanceList.innerHTML = '';
  if (state.instances.length === 0) return;

  for (const instance of state.instances) {
    const instanceId = getInstanceIdValue(instance);
    const instanceState = getInstanceStateValue(instance);
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'deployment-card';
    button.innerHTML = `
      <div class="entity-card-top">
        <span class="entity-title">${escapeHtml(instanceId)}</span>
        ${renderBadge(instanceState)}
      </div>
      <div class="entity-meta">${escapeHtml(getInstanceArmadaVersionValue(instance) || 'unknown version')} | ${escapeHtml(getInstanceProtocolVersionValue(instance) || 'unknown protocol')}</div>
      <div class="entity-meta-secondary">${escapeHtml(getInstanceLastErrorValue(instance) || getInstanceRemoteAddressValue(instance) || 'No current error')}</div>
    `;

    button.addEventListener('click', async () => {
      await selectInstanceForLogin(instanceId);
    });

    if (state.selectedInstanceId === instanceId || state.pendingInstanceId === instanceId) {
      button.classList.add('is-selected');
    }

    elements.instanceList.appendChild(button);
  }
}

function renderSummaryMeta(health, generatedUtc) {
  return `
    ${renderStatusPill('Version', health.version || 'unknown version')}
    ${renderStatusPill('Tunnel', health.remoteTunnel?.state || 'unknown', 'state')}
    ${renderStatusPill('Generated', formatTimestamp(generatedUtc))}
  `;
}

function makeSummaryCard(label, valueHtml, detailHtml = '', extraClass = '') {
  return `
    <article class="summary-card${extraClass ? ` ${extraClass}` : ''}">
      <div class="summary-label">${escapeHtml(label)}</div>
      <div class="summary-value">${valueHtml}</div>
      ${detailHtml ? `<div class="summary-detail">${detailHtml}</div>` : ''}
    </article>
  `;
}

function renderMissionStateMarkup(states) {
  const entries = Object.entries(states || {})
    .sort((left, right) => Number(right[1]) - Number(left[1]));

  if (entries.length === 0) {
    return '<div class="text-muted">No recent mission states.</div>';
  }

  return `
    <div class="state-chip-grid">
      ${entries.map(([key, value]) => `
        <div class="state-chip ${badgeClass(key)}">
          <span class="state-chip-label">${escapeHtml(key)}</span>
          <span class="state-chip-value">${escapeHtml(String(value))}</span>
        </div>
      `).join('')}
    </div>
  `;
}

function renderSelectedInstance() {
  if (!state.selectedInstanceId || !state.summary) {
    elements.emptyState.classList.remove('hidden');
    elements.instanceWorkspace.classList.add('hidden');
    setDeploymentChrome();
    return;
  }

  const summary = state.summary;
  const health = summary.health || {};
  const status = summary.status || {};

  elements.emptyState.classList.add('hidden');
  elements.instanceWorkspace.classList.remove('hidden');
  elements.summaryTitle.textContent = state.selectedInstanceId;
  elements.summarySubtitle.innerHTML = renderSummaryMeta(health, summary.generatedUtc);
  setDeploymentChrome();

  const tunnelState = health.remoteTunnel?.state || 'unknown';
  const latencyValue = health.remoteTunnel?.latencyMs != null ? `${health.remoteTunnel.latencyMs} ms` : '-';
  const activeVoyages = String(status.activeVoyages ?? 0);
  const workingCaptains = String(status.workingCaptains ?? 0);

  elements.summaryCards.innerHTML = [
    makeSummaryCard('Tunnel', renderBadge(tunnelState), escapeHtml(health.remoteTunnel?.tunnelUrl || 'No tunnel URL configured')),
    makeSummaryCard('Latency', escapeHtml(latencyValue), escapeHtml(`Last heartbeat ${formatTimestamp(health.remoteTunnel?.lastHeartbeatUtc)}`)),
    makeSummaryCard('Active Voyages', escapeHtml(activeVoyages), escapeHtml(`Working captains ${workingCaptains}`)),
    makeSummaryCard('Mission States', renderMissionStateMarkup(status.missionsByStatus || {}), '', 'summary-card-wide'),
  ].join('');

  renderActivity(summary.recentActivity || []);
  loadRecentMissionList();
  loadRecentVoyageList();
  renderEntityList(elements.captainList, summary.recentCaptains || [], 'captain');
  renderEntityList(elements.fleetList, state.fleets || [], 'fleet');
  renderEntityList(elements.vesselList, state.vessels || [], 'vessel');
  renderEntityList(elements.playbookList, state.playbooks || [], 'playbook');
  renderEntityList(elements.backlogList, state.objectives || [], 'backlog');
  renderEntityList(elements.planningList, state.planningSessions || [], 'planning');
  renderEntityList(elements.workflowProfileList, state.workflowProfiles || [], 'workflow-profile');
  renderEntityList(elements.checkRunList, state.checkRuns || [], 'check-run');
  renderEntityList(elements.environmentList, state.environments || [], 'environment');
  renderEntityList(elements.releaseList, state.releases || [], 'release');
  renderEntityList(elements.deploymentList, state.deployments || [], 'deployment');
  renderEntityList(elements.incidentList, state.incidents || [], 'incident');
  renderEntityList(elements.runbookList, state.runbooks || [], 'runbook');
  renderEntityList(elements.runbookExecutionList, state.runbookExecutions || [], 'runbook-execution');
  renderEntityList(elements.requestHistoryList, state.requestHistory || [], 'request-history');
  renderEntityList(elements.pipelineReferenceList, state.pipelines || [], 'pipeline');
  renderEntityList(elements.personaReferenceList, state.personas || [], 'persona');
  renderEntityList(elements.promptTemplateReferenceList, state.promptTemplates || [], 'prompt-template');
  renderRequestHistorySummaryCards(state.requestHistorySummary);
  renderWorkspaceStatusPanel(state.workspaceSnapshot);
  renderCaptainToolsSummary();
  renderPlanningWorkspace();
  void loadRecentBacklogList();
  void loadRecentPlanningList();
  void loadDeliveryResources();
  void loadRequestHistoryData();
  void loadReferenceData();
}

function loadRecentMissionList() {
  const rows = state.summary?.recentMissions || [];
  renderEntityList(elements.missionList, rows, 'mission');
  setFormStatus(elements.missionBrowseStatusText, `Showing ${rows.length} recent mission${rows.length === 1 ? '' : 's'}.`, null);
}

function loadRecentVoyageList() {
  const rows = state.summary?.recentVoyages || [];
  renderEntityList(elements.voyageList, rows, 'voyage');
  setFormStatus(elements.voyageBrowseStatusText, `Showing ${rows.length} recent voyage${rows.length === 1 ? '' : 's'}.`, null);
}

function getResourceStateKey(resourceKey) {
  switch (resourceKey) {
    case 'workflowProfile': return 'workflowProfiles';
    case 'checkRun': return 'checkRuns';
    case 'environment': return 'environments';
    case 'release': return 'releases';
    case 'deployment': return 'deployments';
    case 'incident': return 'incidents';
    case 'runbook': return 'runbooks';
    case 'runbookExecution': return 'runbookExecutions';
    case 'pipeline': return 'pipelines';
    case 'persona': return 'personas';
    case 'promptTemplate': return 'promptTemplates';
    default: return null;
  }
}

function getResourceRows(resourceKey) {
  const stateKey = getResourceStateKey(resourceKey);
  return stateKey ? (state[stateKey] || []) : [];
}

function setResourceRows(resourceKey, rows) {
  const definition = RESOURCE_DEFINITIONS[resourceKey];
  const stateKey = getResourceStateKey(resourceKey);
  if (!definition || !stateKey) return;
  state[stateKey] = rows;
  renderEntityList(elements[definition.listElementKey], rows, definition.kind);
}

function renderRequestHistorySummaryCards(summary) {
  if (!elements.requestHistorySummary) return;
  if (!summary) {
    elements.requestHistorySummary.innerHTML = '<div class="detail-empty">Request-history totals will appear after the first load.</div>';
    return;
  }

  elements.requestHistorySummary.innerHTML = [
    makeSummaryCard('Requests', escapeHtml(String(summary.totalCount ?? 0)), escapeHtml(`Window ${summary.bucketMinutes || 15} min buckets`)),
    makeSummaryCard('Success', escapeHtml(String(summary.successCount ?? 0)), escapeHtml(`Rate ${Number(summary.successRate || 0).toFixed(1)}%`)),
    makeSummaryCard('Failures', escapeHtml(String(summary.failureCount ?? 0)), escapeHtml(`Average ${Number(summary.averageDurationMs || 0).toFixed(1)} ms`)),
  ].join('');
}

function renderCaptainToolsSummary() {
  if (!elements.captainToolsSummary) return;
  const captainId = elements.captainToolsCaptainId?.value || '';
  const summary = captainId ? state.captainToolAccess?.[captainId] : null;
  if (!summary) {
    elements.captainToolsSummary.innerHTML = 'Select a captain to inspect accessible runtime tools.';
    elements.captainToolsSummary.className = 'detail-card detail-card-compact detail-empty';
    return;
  }

  elements.captainToolsSummary.className = 'detail-card detail-card-compact';
  elements.captainToolsSummary.innerHTML = `
    <div class="detail-row">
      <span class="detail-key">Runtime</span>
      <span class="detail-value">${escapeHtml(summary.runtime || '-')}</span>
    </div>
    <div class="detail-row">
      <span class="detail-key">Configured Sources</span>
      <span class="detail-value">${escapeHtml(String(summary.configuredServerCount ?? 0))}</span>
    </div>
    <div class="detail-row">
      <span class="detail-key">Reachable Sources</span>
      <span class="detail-value">${escapeHtml(String(summary.reachableServerCount ?? 0))}</span>
    </div>
    <div class="detail-row">
      <span class="detail-key">Summary</span>
      <span class="detail-value">${escapeHtml(summary.summary || '-')}</span>
    </div>
  `;
}

function renderWorkspaceStatusPanel(snapshot) {
  if (!elements.workspaceStatusPanel) return;
  if (!snapshot) {
    elements.workspaceStatusPanel.innerHTML = '<div class="detail-empty">Load workspace status, tree, search, or changes for a vessel.</div>';
    return;
  }

  const status = snapshot.status || {};
  elements.workspaceStatusPanel.innerHTML = [
    makeSummaryCard('Working Dir', renderBadge(status.hasWorkingDirectory ? 'Available' : 'Missing'), escapeHtml(status.rootPath || status.error || 'No working directory configured')),
    makeSummaryCard('Branch', escapeHtml(status.branchName || '-'), escapeHtml(`Dirty ${status.isDirty ? 'yes' : 'no'}`)),
    makeSummaryCard('Ahead / Behind', escapeHtml(`${status.commitsAhead ?? 0} / ${status.commitsBehind ?? 0}`), escapeHtml(`Active missions ${status.activeMissionCount ?? 0}`)),
  ].join('');
}

function renderWorkspaceResultRows(rows, kind) {
  renderEntityList(elements.workspaceResultList, rows, kind);
}

function focusPlanningSection() {
  document.getElementById('planningSection')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function setPlanningWorkspaceStatus(message, kind = null) {
  state.planningWorkspaceStatus = {
    message: String(message || ''),
    kind: kind || null,
  };
  renderPlanningWorkspace();
}

function clearPlanningWorkspaceStatus() {
  state.planningWorkspaceStatus = { message: '', kind: null };
}

function getPlanningSessionParts(payload) {
  const detail = payload || {};
  return {
    session: detail.session || detail.Session || {},
    messages: detail.messages || detail.Messages || [],
    captain: detail.captain || detail.Captain || {},
    vessel: detail.vessel || detail.Vessel || {},
  };
}

function getLinkedObjectiveForPlanningSession(sessionId) {
  if (!sessionId) return null;
  return (state.objectives || []).find((objective) => (objective.planningSessionIds || []).includes(sessionId)) || null;
}

function resolvePlanningDispatchSourceMessage(messages, preferredId = '') {
  const rows = Array.isArray(messages) ? messages : [];
  if (rows.length < 1) return null;

  const canUse = (message) => String(message?.role || '').toLowerCase() === 'assistant' && String(message?.content || '').trim().length > 0;

  if (preferredId) {
    const preferred = rows.find((message) => message.id === preferredId && canUse(message));
    if (preferred) return preferred;
  }

  const explicitlySelected = rows.find((message) => (message.isSelected || message.isSelectedForDispatch) && canUse(message));
  if (explicitlySelected) return explicitlySelected;

  for (let idx = rows.length - 1; idx >= 0; idx -= 1) {
    if (canUse(rows[idx])) return rows[idx];
  }

  return null;
}

function openDispatchFromPlanningWorkspace() {
  if (!state.planningDetail) return;

  const { session, messages } = getPlanningSessionParts(state.planningDetail);
  const selectedMessage = resolvePlanningDispatchSourceMessage(messages, state.selectedPlanningMessageId);
  const missionTitle = (state.planningDraftTitle || session.title || 'Planned Work').trim();
  const missionDescription = (state.planningDraftDescription || selectedMessage?.content || '')
    .replace(/\r?\n+/g, ' ')
    .trim();

  resetDispatchForm();
  elements.dispatchVesselId.value = session.vesselId || '';
  elements.dispatchPipelineId.value = session.pipelineId || '';
  elements.dispatchTitle.value = missionTitle;
  elements.dispatchDescription.value = (state.planningDraftDescription || '').trim();
  elements.dispatchMissions.value = missionDescription
    ? `${missionTitle} :: ${missionDescription}`
    : missionTitle;
  state.dispatchSelectedPlaybooks = (session.selectedPlaybooks || []).map((selection) => ({
    playbookId: selection.playbookId,
    deliveryMode: selection.deliveryMode,
  }));
  renderDispatchPlaybookSelection();
  setFormStatus(elements.dispatchFormStatus, 'Planning draft loaded into dispatch.', 'success');
  openModal(elements.dispatchModal);
}

function renderPlanningWorkspace() {
  if (!elements.planningWorkspace) return;

  if (!state.selectedInstanceId) {
    elements.planningWorkspace.innerHTML = '<div class="detail-empty">Select a deployment to inspect planning sessions.</div>';
    return;
  }

  if (state.planningWorkspaceLoading) {
    elements.planningWorkspace.innerHTML = '<div class="detail-empty">Loading planning session from the deployment...</div>';
    return;
  }

  if (!state.selectedPlanningSessionId) {
    elements.planningWorkspace.innerHTML = `
      <div class="planning-workspace-empty">
        <h3>Planning Workspace</h3>
        <p>Select a planning session to continue the conversation, summarize a cleaner dispatch draft, or hand the work off into dispatch.</p>
      </div>
    `;
    return;
  }

  if (!state.planningDetail) {
    elements.planningWorkspace.innerHTML = `
      <div class="detail-empty">${escapeHtml(state.planningWorkspaceStatus?.message || 'Unable to load planning session detail.')}</div>
    `;
    return;
  }

  const { session, messages, captain, vessel } = getPlanningSessionParts(state.planningDetail);
  const linkedObjective = getLinkedObjectiveForPlanningSession(session.id);
  const dispatchSourceMessage = resolvePlanningDispatchSourceMessage(messages, state.selectedPlanningMessageId);
  const selectedMessage = dispatchSourceMessage || messages[messages.length - 1] || null;
  const hasDispatchSource = Boolean(dispatchSourceMessage);
  const canSend = String(session.status || '') === 'Active' && !state.planningSending && state.planningComposer.trim().length > 0;
  const canSummarize = hasDispatchSource && !state.planningSummarizing;
  const canOpenInDispatch = hasDispatchSource && !state.planningDispatching;
  const canDispatch = hasDispatchSource && !state.planningDispatching;
  const statusMessage = state.planningWorkspaceStatus?.message || '';
  const statusKind = state.planningWorkspaceStatus?.kind || null;
  const selectedPlaybooks = session.selectedPlaybooks || [];

  elements.planningWorkspace.innerHTML = `
    <div class="planning-workspace-stack">
      <div class="planning-workspace-header">
        <div>
          <h3>${escapeHtml(session.title || session.id || 'Planning Session')}</h3>
          <div class="planning-workspace-summary">
            ${renderBadge(session.status || 'unknown')}
            <span class="mono text-dim">${escapeHtml(session.id || '')}</span>
            <span class="text-dim">${escapeHtml(captain.name || session.captainId || 'Unknown captain')}</span>
            <span class="text-dim">${escapeHtml(vessel.name || session.vesselId || 'Unknown vessel')}</span>
          </div>
        </div>
        <div class="detail-actions">
          <button type="button" class="button" data-planning-action="refresh">Refresh</button>
          ${linkedObjective ? '<button type="button" class="button" data-planning-action="open-objective">Open Objective</button>' : ''}
          <button type="button" class="button" data-planning-action="stop" ${state.planningStopping ? 'disabled' : ''}>${state.planningStopping ? 'Stopping...' : 'Stop'}</button>
          <button type="button" class="button button-danger" data-planning-action="delete" ${state.planningDeleting ? 'disabled' : ''}>${state.planningDeleting ? 'Deleting...' : 'Delete'}</button>
        </div>
      </div>
      ${statusMessage ? `<div class="form-status${statusKind ? ` ${escapeHtml(statusKind)}` : ''}">${escapeHtml(statusMessage)}</div>` : ''}
      <div class="detail-grid">
        ${renderKeyValueCard('Session', [
          ['Captain', captain.name || session.captainId || '-'],
          ['Vessel', vessel.name || session.vesselId || '-'],
          ['Pipeline', session.pipelineId || '-'],
          ['Status', session.status || '-'],
          ['Created', formatTimestamp(session.createdUtc)],
          ['Updated', formatTimestamp(session.lastUpdateUtc)],
        ])}
        ${renderKeyValueCard('Scope', [
          ['Fleet', session.fleetId || '-'],
          ['Dock', session.dockId || '-'],
          ['Branch', session.branchName || '-'],
          ['Failure', session.failureReason || '-'],
          ['Linked Objective', linkedObjective?.title || linkedObjective?.id || '-'],
          ['Messages', String(messages.length)],
        ])}
        ${selectedPlaybooks.length > 0 ? renderSelectedPlaybooksCard('Playbooks', selectedPlaybooks) : ''}
      </div>
      <div class="planning-workspace-grid">
        <section class="detail-card planning-transcript-card">
          <div class="planning-card-head">
            <div>
              <h3>Transcript</h3>
              <p class="text-muted">${escapeHtml(`${messages.length} message${messages.length === 1 ? '' : 's'}`)}</p>
            </div>
            <span class="text-dim">${escapeHtml(String(session.status || ''))}</span>
          </div>
          <div class="planning-message-list">
            ${messages.length < 1
              ? '<div class="text-muted">No transcript yet. Send the first planning message below.</div>'
              : messages.map((message) => {
                const role = String(message.role || 'Message');
                const roleLower = role.toLowerCase();
                const isAssistant = roleLower === 'assistant';
                const isUser = roleLower === 'user';
                const isSelected = dispatchSourceMessage?.id === message.id;
                const hasContent = String(message.content || '').trim().length > 0;
                const shellTag = isAssistant && hasContent ? 'button' : 'div';
                const actionAttrs = isAssistant && hasContent
                  ? `type="button" data-planning-action="select-message" data-message-id="${escapeHtml(message.id || '')}"`
                  : '';
                const subtitle = `${formatTimestamp(message.lastUpdateUtc || message.createdUtc)}${isSelected ? ' | Selected for dispatch' : ''}`;
                return `
                  <${shellTag} class="planning-message-card${isAssistant ? ' planning-message-card-assistant' : ''}${isUser ? ' planning-message-card-user' : ''}${isSelected ? ' is-selected' : ''}" ${actionAttrs}>
                    <div class="planning-message-top">
                      <div>
                        <strong>${escapeHtml(role)}</strong>
                        <div class="detail-list-meta mono">${escapeHtml(message.id || '')}</div>
                      </div>
                      <div class="planning-message-meta">
                        <span class="text-dim">${escapeHtml(subtitle)}</span>
                        ${isAssistant && hasContent
                          ? `<span class="badge ${isSelected ? 'working' : 'idle'}">${isSelected ? 'Selected For Dispatch' : 'Use For Dispatch'}</span>`
                          : ''}
                      </div>
                    </div>
                    <pre class="planning-message-content">${escapeHtml(message.content || (isAssistant ? 'Waiting for response...' : ''))}</pre>
                  </${shellTag}>
                `;
              }).join('')}
          </div>
          <form class="form-stack planning-composer-form" data-planning-form="composer">
            <label class="field">
              <span>Send Message</span>
              <textarea rows="6" data-planning-field="composer" placeholder="Describe the problem, ask for a plan, or negotiate the next steps with the captain." ${String(session.status || '') !== 'Active' || state.planningSending ? 'disabled' : ''}>${escapeHtml(state.planningComposer)}</textarea>
            </label>
            <div class="form-actions">
              <button type="submit" class="button button-primary" ${canSend ? '' : 'disabled'}>
                ${state.planningSending ? 'Sending...' : (String(session.status || '') === 'Responding' ? 'Captain Responding...' : 'Send')}
              </button>
            </div>
          </form>
        </section>
        <div class="planning-sidebar-stack">
          <section class="detail-card">
            <div class="planning-card-head">
              <div>
                <h3>Dispatch Source</h3>
                <p class="text-muted">Pick an assistant response, refine the draft, then dispatch it or open it in the voyage composer.</p>
              </div>
            </div>
            ${selectedMessage
              ? `
                <div class="planning-selected-message-meta">
                  <strong>${escapeHtml(selectedMessage.role || 'Message')}</strong>
                  <span class="mono text-dim">${escapeHtml(selectedMessage.id || '')}</span>
                </div>
                <pre class="planning-message-content planning-message-content-compact">${escapeHtml(selectedMessage.content || '')}</pre>
              `
              : '<div class="text-muted">Select an assistant response to use for summarization and dispatch.</div>'}
          </section>
          <section class="detail-card">
            <div class="planning-card-head">
              <div>
                <h3>Dispatch Draft</h3>
                <p class="text-muted">Summarize the selected assistant response into a cleaner voyage title and mission brief before dispatch.</p>
              </div>
            </div>
            <div class="form-stack">
              <label class="field">
                <span>Voyage Title</span>
                <input
                  type="text"
                  data-planning-field="draft-title"
                  value="${escapeHtml(state.planningDraftTitle)}"
                  placeholder="Optional override for the resulting voyage title"
                >
              </label>
              <label class="field">
                <span>Mission Description</span>
                <textarea rows="10" data-planning-field="draft-description" placeholder="Select an assistant response to seed the dispatch description.">${escapeHtml(state.planningDraftDescription)}</textarea>
              </label>
              <div class="form-actions">
                <button type="button" class="button" data-planning-action="summarize" ${canSummarize ? '' : 'disabled'}>
                  ${state.planningSummarizing ? 'Summarizing...' : 'Summarize Draft'}
                </button>
                <button type="button" class="button" data-planning-action="open-dispatch" ${canOpenInDispatch ? '' : 'disabled'}>
                  Open In Dispatch
                </button>
                <button type="button" class="button button-primary" data-planning-action="dispatch" ${canDispatch ? '' : 'disabled'}>
                  ${state.planningDispatching ? 'Dispatching...' : 'Dispatch'}
                </button>
              </div>
            </div>
          </section>
        </div>
      </div>
    </div>
  `;
}

async function selectPlanningSession(id, options = {}) {
  if (!state.selectedInstanceId || !id) return;

  const priorSelection = state.selectedPlanningSessionId;
  const shouldResetDraft = priorSelection !== id;
  state.selectedPlanningSessionId = id;
  state.planningWorkspaceLoading = true;
  state.planningDetail = null;
  if (shouldResetDraft) {
    state.selectedPlanningMessageId = '';
    state.planningComposer = '';
    state.planningDraftTitle = '';
    state.planningDraftDescription = '';
    clearPlanningWorkspaceStatus();
  }
  renderEntityList(elements.planningList, state.planningSessions || [], 'planning');
  renderPlanningWorkspace();

  if (options.scroll) {
    focusPlanningSection();
  }

  try {
    const payload = await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(id)}`);
    const { session, messages } = getPlanningSessionParts(payload);
    const linkedObjective = getLinkedObjectiveForPlanningSession(session.id);
    const dispatchSourceMessage = resolvePlanningDispatchSourceMessage(messages, state.selectedPlanningMessageId);

    state.planningDetail = payload;
    state.planningWorkspaceLoading = false;
    state.selectedPlanningMessageId = dispatchSourceMessage?.id || '';
    if (!state.planningDraftTitle) state.planningDraftTitle = session.title || '';
    if (!state.planningDraftDescription) {
      state.planningDraftDescription = linkedObjective?.refinementSummary || linkedObjective?.description || '';
    }

    if (options.statusMessage) {
      state.planningWorkspaceStatus = {
        message: String(options.statusMessage),
        kind: options.statusKind || 'success',
      };
    }

    renderEntityList(elements.planningList, state.planningSessions || [], 'planning');
    renderPlanningWorkspace();
  } catch (error) {
    state.planningWorkspaceLoading = false;
    state.planningDetail = null;
    state.planningWorkspaceStatus = {
      message: error instanceof Error ? error.message : 'Unable to load planning session.',
      kind: 'error',
    };
    renderEntityList(elements.planningList, state.planningSessions || [], 'planning');
    renderPlanningWorkspace();
  }
}

async function sendPlanningWorkspaceMessage() {
  if (!state.selectedInstanceId || !state.selectedPlanningSessionId || !state.planningDetail) return;

  const { session } = getPlanningSessionParts(state.planningDetail);
  const content = state.planningComposer.trim();
  if (!content) {
    setPlanningWorkspaceStatus('Enter a planning message before sending it.', 'error');
    return;
  }
  if (String(session.status || '') !== 'Active') {
    setPlanningWorkspaceStatus('This planning session is not active right now, so it cannot accept another message.', 'error');
    return;
  }

  state.planningSending = true;
  clearPlanningWorkspaceStatus();
  renderPlanningWorkspace();

  try {
    const payload = await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(state.selectedPlanningSessionId)}/messages`, {
      method: 'POST',
      body: { content },
    });
    state.planningComposer = '';
    state.planningDetail = payload;
    const { messages } = getPlanningSessionParts(payload);
    const dispatchSourceMessage = resolvePlanningDispatchSourceMessage(messages, state.selectedPlanningMessageId);
    state.selectedPlanningMessageId = dispatchSourceMessage?.id || state.selectedPlanningMessageId;
    await loadRecentPlanningList(true);
    setPlanningWorkspaceStatus('Planning session updated.', 'success');
  } catch (error) {
    setPlanningWorkspaceStatus(error instanceof Error ? error.message : 'Planning-session message failed.', 'error');
  } finally {
    state.planningSending = false;
    renderPlanningWorkspace();
  }
}

async function summarizePlanningWorkspace() {
  if (!state.selectedInstanceId || !state.selectedPlanningSessionId || !state.planningDetail) return;

  const { messages } = getPlanningSessionParts(state.planningDetail);
  const dispatchSourceMessage = resolvePlanningDispatchSourceMessage(messages, state.selectedPlanningMessageId);
  if (!dispatchSourceMessage) {
    setPlanningWorkspaceStatus('Select an assistant response before summarizing a dispatch draft.', 'error');
    return;
  }

  state.planningSummarizing = true;
  clearPlanningWorkspaceStatus();
  renderPlanningWorkspace();

  try {
    const summary = await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(state.selectedPlanningSessionId)}/summarize`, {
      method: 'POST',
      body: {
        messageId: dispatchSourceMessage.id,
        title: state.planningDraftTitle.trim() || null,
      },
    });
    state.selectedPlanningMessageId = summary?.messageId || summary?.MessageId || dispatchSourceMessage.id;
    state.planningDraftTitle = summary?.title || summary?.Title || state.planningDraftTitle;
    state.planningDraftDescription = summary?.description || summary?.Description || state.planningDraftDescription;
    const method = summary?.method || summary?.Method || 'summary';
    setPlanningWorkspaceStatus(`Dispatch draft refreshed using ${method}.`, 'success');
  } catch (error) {
    setPlanningWorkspaceStatus(error instanceof Error ? error.message : 'Planning-session summarization failed.', 'error');
  } finally {
    state.planningSummarizing = false;
    renderPlanningWorkspace();
  }
}

async function dispatchPlanningWorkspace() {
  if (!state.selectedInstanceId || !state.selectedPlanningSessionId || !state.planningDetail) return;

  const { messages } = getPlanningSessionParts(state.planningDetail);
  const dispatchSourceMessage = resolvePlanningDispatchSourceMessage(messages, state.selectedPlanningMessageId);
  if (!dispatchSourceMessage) {
    setPlanningWorkspaceStatus('Select an assistant response before dispatching this planning session.', 'error');
    return;
  }

  state.planningDispatching = true;
  clearPlanningWorkspaceStatus();
  renderPlanningWorkspace();

  try {
    const detail = await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(state.selectedPlanningSessionId)}/dispatch`, {
      method: 'POST',
      body: {
        messageId: dispatchSourceMessage.id,
        title: state.planningDraftTitle.trim() || null,
        description: state.planningDraftDescription.trim() || null,
      },
    });
    await loadSelectedInstance();
    const voyageId = detail?.voyage?.id || detail?.Voyage?.id || detail?.id;
    setPlanningWorkspaceStatus('Voyage dispatched from this planning session.', 'success');
    if (voyageId) {
      await openVoyageDetailModal(voyageId);
    }
  } catch (error) {
    setPlanningWorkspaceStatus(error instanceof Error ? error.message : 'Planning-session dispatch failed.', 'error');
  } finally {
    state.planningDispatching = false;
    renderPlanningWorkspace();
  }
}

async function stopPlanningWorkspace() {
  if (!state.selectedInstanceId || !state.selectedPlanningSessionId) return;
  if (!confirm('Stop this planning session?')) return;

  state.planningStopping = true;
  clearPlanningWorkspaceStatus();
  renderPlanningWorkspace();

  try {
    const payload = await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(state.selectedPlanningSessionId)}/stop`, {
      method: 'POST',
      body: {},
    });
    state.planningDetail = payload;
    await loadRecentPlanningList(true);
    setPlanningWorkspaceStatus('Planning session stop requested.', 'success');
  } catch (error) {
    setPlanningWorkspaceStatus(error instanceof Error ? error.message : 'Planning-session stop failed.', 'error');
  } finally {
    state.planningStopping = false;
    renderPlanningWorkspace();
  }
}

async function deletePlanningWorkspace() {
  if (!state.selectedInstanceId || !state.selectedPlanningSessionId) return;
  if (!confirm('Delete this planning session?')) return;

  state.planningDeleting = true;
  clearPlanningWorkspaceStatus();
  renderPlanningWorkspace();

  try {
    await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(state.selectedPlanningSessionId)}`, { method: 'DELETE' });
    const deletedId = state.selectedPlanningSessionId;
    state.selectedPlanningSessionId = null;
    state.selectedPlanningMessageId = '';
    state.planningDetail = null;
    state.planningComposer = '';
    state.planningDraftTitle = '';
    state.planningDraftDescription = '';
    await loadRecentPlanningList(true);
    setFormStatus(elements.planningBrowseStatusText, `Planning session deleted: ${deletedId}`, 'success');
    state.planningWorkspaceStatus = {
      message: `Planning session deleted: ${deletedId}`,
      kind: 'success',
    };
  } catch (error) {
    setPlanningWorkspaceStatus(error instanceof Error ? error.message : 'Planning-session delete failed.', 'error');
  } finally {
    state.planningDeleting = false;
    renderPlanningWorkspace();
  }
}

async function loadRecentBacklogList(silent = false) {
  if (!state.selectedInstanceId) return;

  try {
    const data = await fetchJson(`${instanceBaseUrl()}/backlog/enumerate`, {
      method: 'POST',
      body: { pageNumber: 1, pageSize: Number(elements.backlogBrowseLimit?.value || '12') || 12 },
    });
    const rows = coerceListPayload(data, 'objectives', 'items');
    state.objectives = rows;
    renderEntityList(elements.backlogList, rows, 'backlog');
    renderPlanningWorkspace();
    if (!silent) {
      setFormStatus(elements.backlogBrowseStatusText, `Loaded ${rows.length} backlog item${rows.length === 1 ? '' : 's'}.`, 'success');
    }
  } catch (error) {
    if (!silent) {
      setFormStatus(elements.backlogBrowseStatusText, error instanceof Error ? error.message : 'Backlog load failed.', 'error');
    }
  }
}

async function submitBacklogBrowseForm(event) {
  event?.preventDefault?.();
  if (!state.selectedInstanceId) return;

  try {
    const data = await fetchJson(`${instanceBaseUrl()}/backlog/enumerate`, {
      method: 'POST',
      body: {
        status: elements.backlogBrowseStatus.value || null,
        backlogState: elements.backlogBrowseBacklogState.value || null,
        pageNumber: 1,
        pageSize: Number(elements.backlogBrowseLimit.value || '12') || 12,
        search: elements.backlogBrowseSearch.value.trim() || null,
        owner: elements.backlogBrowseOwner.value.trim() || null,
        vesselId: elements.backlogBrowseVesselId.value.trim() || null,
        tag: elements.backlogBrowseTag.value.trim() || null,
      },
    });
    const rows = coerceListPayload(data, 'objectives', 'items');
    state.objectives = rows;
    renderEntityList(elements.backlogList, rows, 'backlog');
    renderPlanningWorkspace();
    setFormStatus(elements.backlogBrowseStatusText, `Loaded ${rows.length} backlog item${rows.length === 1 ? '' : 's'} from the deployment.`, 'success');
  } catch (error) {
    setFormStatus(elements.backlogBrowseStatusText, error instanceof Error ? error.message : 'Backlog browse failed.', 'error');
  }
}

async function loadRecentPlanningList(silent = false) {
  if (!state.selectedInstanceId) return;

  try {
    const data = await fetchJson(`${instanceBaseUrl()}/planning-sessions/enumerate`, {
      method: 'POST',
      body: { limit: Number(elements.planningBrowseLimit?.value || '12') || 12 },
    });
    const rows = coerceListPayload(data, 'planningSessions', 'items');
    state.planningSessions = rows;
    renderEntityList(elements.planningList, rows, 'planning');
    renderPlanningWorkspace();
    if (!silent) {
      setFormStatus(elements.planningBrowseStatusText, `Loaded ${rows.length} planning session${rows.length === 1 ? '' : 's'}.`, 'success');
    }
  } catch (error) {
    if (!silent) {
      setFormStatus(elements.planningBrowseStatusText, error instanceof Error ? error.message : 'Planning-session load failed.', 'error');
    }
  }
}

async function submitPlanningBrowseForm(event) {
  event?.preventDefault?.();
  if (!state.selectedInstanceId) return;

  try {
    const data = await fetchJson(`${instanceBaseUrl()}/planning-sessions/enumerate`, {
      method: 'POST',
      body: {
        captainId: elements.planningBrowseCaptainId.value.trim() || null,
        vesselId: elements.planningBrowseVesselId.value.trim() || null,
        status: elements.planningBrowseStatus.value.trim() || null,
        objectiveId: elements.planningBrowseObjectiveId.value.trim() || null,
        limit: Number(elements.planningBrowseLimit.value || '12') || 12,
      },
    });
    const rows = coerceListPayload(data, 'planningSessions', 'items');
    state.planningSessions = rows;
    renderEntityList(elements.planningList, rows, 'planning');
    renderPlanningWorkspace();
    setFormStatus(elements.planningBrowseStatusText, `Loaded ${rows.length} planning session${rows.length === 1 ? '' : 's'} from the deployment.`, 'success');
  } catch (error) {
    setFormStatus(elements.planningBrowseStatusText, error instanceof Error ? error.message : 'Planning-session browse failed.', 'error');
  }
}

async function loadResourceCollection(resourceKey) {
  if (!state.selectedInstanceId) return;
  const definition = RESOURCE_DEFINITIONS[resourceKey];
  if (!definition) return;

  const base = instanceBaseUrl();
  let data;
  if (resourceKey === 'pipeline') {
    data = await fetchJson(`${base}/pipelines?limit=${DEFAULT_RESOURCE_LIMIT}`);
  } else if (definition.enumeratePath) {
    data = await fetchJson(`${base}${definition.enumeratePath}`, {
      method: definition.enumeratePath === '/pipelines' ? 'GET' : 'POST',
      body: definition.enumeratePath === '/pipelines'
        ? undefined
        : { pageNumber: 1, pageSize: DEFAULT_RESOURCE_LIMIT },
    });
  } else {
    data = await fetchJson(`${base}/${definition.endpoint}?limit=${DEFAULT_RESOURCE_LIMIT}`);
  }

  const rows = coerceListPayload(
    data,
    `${resourceKey}s`,
    definition.endpoint.replace(/-/g, ''),
    definition.endpoint,
    'items');
  setResourceRows(resourceKey, rows);
}

async function loadDeliveryResources() {
  const keys = ['workflowProfile', 'checkRun', 'environment', 'release', 'deployment', 'incident', 'runbook', 'runbookExecution'];
  await Promise.allSettled(keys.map((key) => loadResourceCollection(key)));
}

async function loadRequestHistorySummary() {
  if (!state.selectedInstanceId) return;
  try {
    const summary = await fetchJson(`${instanceBaseUrl()}/request-history/summary`, {
      method: 'POST',
      body: {
        bucketMinutes: 15,
        pageNumber: 1,
        pageSize: Number(elements.requestHistoryLimit?.value || '12') || 12,
      },
    });
    state.requestHistorySummary = summary;
    renderRequestHistorySummaryCards(summary);
  } catch {
    state.requestHistorySummary = null;
    renderRequestHistorySummaryCards(null);
  }
}

async function loadRequestHistoryData(silent = true) {
  if (!state.selectedInstanceId) return;

  try {
    const data = await fetchJson(`${instanceBaseUrl()}/request-history/enumerate`, {
      method: 'POST',
      body: {
        pageNumber: 1,
        pageSize: Number(elements.requestHistoryLimit?.value || '12') || 12,
        method: elements.requestHistoryMethod?.value?.trim() || null,
        route: elements.requestHistoryRoute?.value?.trim() || null,
        statusCode: elements.requestHistoryStatusCode?.value ? Number(elements.requestHistoryStatusCode.value) : null,
      },
    });
    const rows = coerceListPayload(data, 'entries', 'history', 'items');
    state.requestHistory = rows;
    renderEntityList(elements.requestHistoryList, rows, 'request-history');
    await loadRequestHistorySummary();
    if (!silent) {
      setFormStatus(elements.requestHistoryStatusText, `Loaded ${rows.length} request-history entr${rows.length === 1 ? 'y' : 'ies'}.`, 'success');
    }
  } catch (error) {
    if (!silent) {
      setFormStatus(elements.requestHistoryStatusText, error instanceof Error ? error.message : 'Request-history load failed.', 'error');
    }
  }
}

async function loadReferenceData() {
  const keys = ['pipeline', 'persona', 'promptTemplate'];
  await Promise.allSettled(keys.map((key) => loadResourceCollection(key)));
}

async function loadCaptainToolAccess(captainId) {
  if (!state.selectedInstanceId || !captainId) return null;
  const result = await fetchJson(`${instanceBaseUrl()}/captains/${encodeURIComponent(captainId)}/tools`);
  state.captainToolAccess[captainId] = result;
  renderCaptainToolsSummary();
  return result;
}

async function loadWorkspaceStatusView() {
  if (!state.selectedInstanceId) return;
  const vesselId = elements.workspaceVesselId.value.trim();
  if (!vesselId) {
    setFormStatus(elements.workspaceStatusText, 'Select a vessel first.', 'error');
    return;
  }

  try {
    const status = await fetchJson(`${instanceBaseUrl()}/workspace/vessels/${encodeURIComponent(vesselId)}/status`);
    state.workspaceSnapshot = { ...(state.workspaceSnapshot || {}), status, vesselId };
    renderWorkspaceStatusPanel(state.workspaceSnapshot);
    setFormStatus(elements.workspaceStatusText, 'Workspace status loaded.', 'success');
  } catch (error) {
    setFormStatus(elements.workspaceStatusText, error instanceof Error ? error.message : 'Workspace status failed.', 'error');
  }
}

async function loadWorkspaceTreeView() {
  if (!state.selectedInstanceId) return;
  const vesselId = elements.workspaceVesselId.value.trim();
  if (!vesselId) {
    setFormStatus(elements.workspaceStatusText, 'Select a vessel first.', 'error');
    return;
  }

  try {
    const path = elements.workspacePath.value.trim();
    const query = buildQuery({ path });
    const result = await fetchJson(`${instanceBaseUrl()}/workspace/vessels/${encodeURIComponent(vesselId)}/tree${query}`);
    state.workspaceSnapshot = { ...(state.workspaceSnapshot || {}), tree: result, vesselId };
    const rows = (result.entries || []).map((entry) => ({
      id: entry.relativePath || entry.name,
      name: entry.name,
      title: entry.name,
      status: entry.isDirectory ? 'Directory' : 'File',
      relativePath: entry.relativePath,
      isDirectory: entry.isDirectory,
      sizeBytes: entry.sizeBytes,
      lastWriteUtc: entry.lastWriteUtc,
      vesselId,
    }));
    renderWorkspaceResultRows(rows, 'workspace-entry');
    setFormStatus(elements.workspaceStatusText, `Loaded ${rows.length} workspace entr${rows.length === 1 ? 'y' : 'ies'}.`, 'success');
  } catch (error) {
    setFormStatus(elements.workspaceStatusText, error instanceof Error ? error.message : 'Workspace tree load failed.', 'error');
  }
}

async function loadWorkspaceSearchView() {
  if (!state.selectedInstanceId) return;
  const vesselId = elements.workspaceVesselId.value.trim();
  const search = elements.workspaceSearchQuery.value.trim();
  if (!vesselId) {
    setFormStatus(elements.workspaceStatusText, 'Select a vessel first.', 'error');
    return;
  }
  if (!search) {
    setFormStatus(elements.workspaceStatusText, 'Enter a search query.', 'error');
    return;
  }

  try {
    const query = buildQuery({
      query: search,
      maxResults: Number(elements.workspaceSearchLimit.value || '50') || 50,
    });
    const result = await fetchJson(`${instanceBaseUrl()}/workspace/vessels/${encodeURIComponent(vesselId)}/search${query}`);
    state.workspaceSnapshot = { ...(state.workspaceSnapshot || {}), search: result, vesselId };
    const rows = (result.matches || []).map((match) => ({
      id: `${match.path}:${match.lineNumber}`,
      name: match.path,
      title: `${match.path}:${match.lineNumber}`,
      status: 'Match',
      preview: match.preview,
      lineNumber: match.lineNumber,
      path: match.path,
      vesselId,
    }));
    renderWorkspaceResultRows(rows, 'workspace-search');
    setFormStatus(elements.workspaceStatusText, `Found ${rows.length} match${rows.length === 1 ? '' : 'es'}.`, 'success');
  } catch (error) {
    setFormStatus(elements.workspaceStatusText, error instanceof Error ? error.message : 'Workspace search failed.', 'error');
  }
}

async function loadWorkspaceChangesView() {
  if (!state.selectedInstanceId) return;
  const vesselId = elements.workspaceVesselId.value.trim();
  if (!vesselId) {
    setFormStatus(elements.workspaceStatusText, 'Select a vessel first.', 'error');
    return;
  }

  try {
    const result = await fetchJson(`${instanceBaseUrl()}/workspace/vessels/${encodeURIComponent(vesselId)}/changes`);
    state.workspaceSnapshot = { ...(state.workspaceSnapshot || {}), changes: result, vesselId };
    const rows = (result.changes || []).map((change) => ({
      id: change.path,
      name: change.path,
      title: change.path,
      status: change.status || 'Changed',
      originalPath: change.originalPath,
      vesselId,
    }));
    renderWorkspaceResultRows(rows, 'workspace-change');
    renderWorkspaceStatusPanel({
      ...(state.workspaceSnapshot || {}),
      status: {
        ...(state.workspaceSnapshot?.status || {}),
        branchName: result.branchName,
        isDirty: result.isDirty,
        commitsAhead: result.commitsAhead,
        commitsBehind: result.commitsBehind,
      },
    });
    setFormStatus(elements.workspaceStatusText, `Loaded ${rows.length} change${rows.length === 1 ? '' : 's'}.`, 'success');
  } catch (error) {
    setFormStatus(elements.workspaceStatusText, error instanceof Error ? error.message : 'Workspace changes failed.', 'error');
  }
}

function renderActivity(activity) {
  elements.activityFeed.innerHTML = '';
  if (activity.length === 0) {
    elements.activityFeed.innerHTML = '<div class="text-muted">No recent activity available.</div>';
    return;
  }

  for (const item of activity) {
    const node = document.createElement('div');
    node.className = 'feed-item';
    node.innerHTML = `
      <div class="feed-type">${escapeHtml(item.eventType || 'event')}</div>
      <div class="feed-message">${escapeHtml(item.message || 'No message')}</div>
      <div class="feed-meta">${formatTimestamp(item.createdUtc)} | ${escapeHtml(item.entityType || 'system')} ${escapeHtml(item.entityId || '')}</div>
    `;
    elements.activityFeed.appendChild(node);
  }
}

function buildEntityMeta(kind, row) {
  if (kind === 'mission') {
    return {
      meta: `${row.persona || 'Unspecified persona'} | ${row.id || '-'}`,
      secondary: `Priority ${row.priority ?? 100} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'captain') {
    return {
      meta: `${row.runtime || 'runtime'} | ${row.id || '-'}`,
      secondary: `Heartbeat ${formatTimestamp(row.lastHeartbeatUtc || row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'voyage') {
    return {
      meta: row.id || '-',
      secondary: `Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'fleet') {
    return {
      meta: row.id || '-',
      secondary: row.description || 'No description',
    };
  }

  if (kind === 'vessel') {
    return {
      meta: row.repoUrl || row.id || '-',
      secondary: row.workingDirectory || 'No working directory',
    };
  }

  if (kind === 'playbook') {
    const contentLength = String(row.content || '').length.toLocaleString();
    return {
      meta: `${row.id || '-'} | ${contentLength} chars`,
      secondary: `${row.description || 'No description'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'backlog') {
    return {
      meta: `${row.id || '-'} | ${(row.kind || 'Feature')} | ${(row.priority || 'P2')}`,
      secondary: `${row.owner || 'Unowned'} | ${(row.backlogState || 'Inbox')} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'planning') {
    return {
      meta: `${row.id || '-'} | Captain ${row.captainId || '-'} | Vessel ${row.vesselId || '-'}`,
      secondary: `${row.pipelineId || 'Default pipeline'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'workflow-profile') {
    return {
      meta: `${row.id || '-'} | ${row.scope || 'Global'}${row.isDefault ? ' | Default' : ''}`,
      secondary: `${row.description || 'No description'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'check-run') {
    return {
      meta: `${row.id || '-'} | ${row.type || 'Check'} | Vessel ${row.vesselId || '-'}`,
      secondary: `${row.environmentName || 'No environment'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'environment') {
    return {
      meta: `${row.id || '-'} | ${row.kind || 'Environment'} | ${row.vesselId || 'Shared'}`,
      secondary: `${row.baseUrl || row.healthEndpoint || 'No endpoint'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'release') {
    return {
      meta: `${row.version || row.id || '-'} | Vessel ${row.vesselId || '-'}`,
      secondary: `${row.summary || row.notes || 'No summary'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'deployment') {
    return {
      meta: `${row.id || '-'} | ${row.environmentName || row.environmentId || '-'} | ${row.releaseId || 'No release'}`,
      secondary: `${row.summary || row.notes || 'No summary'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'incident') {
    return {
      meta: `${row.id || '-'} | ${row.severity || 'High'} | ${row.environmentName || row.environmentId || '-'}`,
      secondary: `${row.summary || row.impact || 'No summary'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'runbook') {
    return {
      meta: `${row.id || '-'} | ${row.fileName || 'No file'} | ${row.environmentName || row.environmentId || '-'}`,
      secondary: `${row.description || 'No description'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'runbook-execution') {
    return {
      meta: `${row.id || '-'} | Runbook ${row.runbookId || '-'} | ${row.environmentName || row.environmentId || '-'}`,
      secondary: `${row.notes || 'No notes'} | Updated ${formatTimestamp(row.lastUpdateUtc)}`,
    };
  }

  if (kind === 'request-history') {
    return {
      meta: `${row.method || 'GET'} ${row.route || '-'} | ${row.statusCode || '-'}`,
      secondary: `${formatTimestamp(row.createdUtc)} | ${Number(row.durationMs || 0).toFixed(1)} ms`,
    };
  }

  if (kind === 'pipeline') {
    return {
      meta: `${row.id || row.name || '-'} | ${Array.isArray(row.stages) ? row.stages.length : 0} stages`,
      secondary: row.description || 'No description',
    };
  }

  if (kind === 'persona') {
    return {
      meta: `${row.id || row.name || '-'} | ${row.category || 'Persona'}`,
      secondary: row.description || 'No description',
    };
  }

  if (kind === 'prompt-template') {
    return {
      meta: `${row.id || row.name || '-'} | ${row.category || 'Prompt'}`,
      secondary: row.description || 'No description',
    };
  }

  if (kind === 'workspace-entry') {
    return {
      meta: `${row.relativePath || row.id || '-'} | ${row.isDirectory ? 'Directory' : 'File'}`,
      secondary: `${row.sizeBytes != null ? `${row.sizeBytes} bytes` : 'Unknown size'} | Updated ${formatTimestamp(row.lastWriteUtc)}`,
    };
  }

  if (kind === 'workspace-search') {
    return {
      meta: `${row.path || '-'} | Line ${row.lineNumber || '-'}`,
      secondary: row.preview || 'No preview',
    };
  }

  if (kind === 'workspace-change') {
    return {
      meta: `${row.originalPath ? `${row.originalPath} -> ` : ''}${row.name || row.id || '-'}`,
      secondary: row.status || 'Changed',
    };
  }

  return {
    meta: row.id || '-',
    secondary: '',
  };
}

async function handleEntitySelection(kind, row) {
  if (!row || !row.id) return;

  if (kind === 'mission') {
    await openMissionDetailModal(row.id);
    return;
  }

  if (kind === 'voyage') {
    await openVoyageDetailModal(row.id);
    return;
  }

  if (kind === 'captain') {
    await openCaptainDetailModal(row.id);
    return;
  }

  if (kind === 'fleet') {
    openFleetModal(row);
    return;
  }

  if (kind === 'vessel') {
    openVesselModal(row);
    return;
  }

  if (kind === 'playbook') {
    openPlaybookModal(row);
    return;
  }

  if (kind === 'backlog') {
    await openBacklogDetailModal(row.id);
    return;
  }

  if (kind === 'planning') {
    await selectPlanningSession(row.id, { scroll: true });
    return;
  }

  if (kind === 'workflow-profile') {
    await openResourceDetailModal('workflowProfile', row.id);
    return;
  }

  if (kind === 'check-run') {
    await openResourceDetailModal('checkRun', row.id);
    return;
  }

  if (kind === 'environment') {
    await openResourceDetailModal('environment', row.id);
    return;
  }

  if (kind === 'release') {
    await openResourceDetailModal('release', row.id);
    return;
  }

  if (kind === 'deployment') {
    await openResourceDetailModal('deployment', row.id);
    return;
  }

  if (kind === 'incident') {
    await openResourceDetailModal('incident', row.id);
    return;
  }

  if (kind === 'runbook') {
    await openResourceDetailModal('runbook', row.id);
    return;
  }

  if (kind === 'runbook-execution') {
    await openResourceDetailModal('runbookExecution', row.id);
    return;
  }

  if (kind === 'request-history') {
    await openRequestHistoryDetailModal(row.id);
    return;
  }

  if (kind === 'pipeline') {
    await openResourceDetailModal('pipeline', row.id || row.name);
    return;
  }

  if (kind === 'persona') {
    await openResourceDetailModal('persona', row.name || row.id);
    return;
  }

  if (kind === 'prompt-template') {
    await openResourceDetailModal('promptTemplate', row.name || row.id);
    return;
  }

  if (kind === 'workspace-entry') {
    if (row.isDirectory) {
      elements.workspacePath.value = row.relativePath || '';
      await loadWorkspaceTreeView();
    } else {
      await openWorkspaceFileDetailModal(row.vesselId, row.relativePath || row.id);
    }
    return;
  }

  if (kind === 'workspace-search') {
    await openWorkspaceFileDetailModal(row.vesselId, row.path);
    return;
  }

  if (kind === 'workspace-change') {
    await openWorkspaceFileDetailModal(row.vesselId, row.name || row.id);
  }
}

function renderEntityList(container, rows, kind) {
  container.innerHTML = '';
  if (!rows || rows.length === 0) {
    const emptyMessages = {
      playbook: 'No playbooks are configured for this deployment.',
      backlog: 'No backlog items are currently visible for this deployment.',
      planning: 'No planning sessions are currently visible for this deployment.',
      'workflow-profile': 'No workflow profiles are currently visible.',
      'check-run': 'No check runs are currently visible.',
      environment: 'No environments are currently visible.',
      release: 'No releases are currently visible.',
      deployment: 'No deployments are currently visible.',
      incident: 'No incidents are currently visible.',
      runbook: 'No runbooks are currently visible.',
      'runbook-execution': 'No runbook executions are currently visible.',
      'request-history': 'No matching request-history entries are currently visible.',
      pipeline: 'No pipelines are currently visible.',
      persona: 'No personas are currently visible.',
      'prompt-template': 'No prompt templates are currently visible.',
      'workspace-entry': 'No workspace entries are available for this path.',
      'workspace-search': 'No workspace search matches were found.',
      'workspace-change': 'No local workspace changes were found.',
    };
    container.innerHTML = `<div class="text-muted">${escapeHtml(emptyMessages[kind] || 'Nothing recent to show.')}</div>`;
    return;
  }

  for (const row of rows) {
    const fragment = elements.entityCardTemplate.content.cloneNode(true);
    const card = fragment.querySelector('.entity-card');
    const title = fragment.querySelector('.entity-title');
    const badge = fragment.querySelector('.badge');
    const meta = fragment.querySelector('.entity-meta');
    const secondary = fragment.querySelector('.entity-meta-secondary');
    const titleValue = row.title || row.name || row.fileName || row.id;
    const badgeValue = kind === 'playbook'
      ? (row.active === false ? 'Inactive' : 'Active')
      : (row.status || row.state || row.persona || row.scope || row.kind || row.type || row.severity || 'detail');
    const description = buildEntityMeta(kind, row);

    title.textContent = titleValue;
    badge.textContent = String(badgeValue);
    badge.classList.add(badgeClass(badgeValue));
    meta.textContent = description.meta;
    secondary.textContent = description.secondary;
    if (kind === 'planning' && row.id === state.selectedPlanningSessionId) {
      card.classList.add('is-selected');
    }

    card.addEventListener('click', async () => {
      await handleEntitySelection(kind, row);
    });

    container.appendChild(fragment);
  }
}

async function loadDetailPayload(kind, id) {
  const base = instanceBaseUrl();
  if (kind === 'mission') return fetchJson(`${base}/missions/${encodeURIComponent(id)}`);
  if (kind === 'voyage') return fetchJson(`${base}/voyages/${encodeURIComponent(id)}`);
  if (kind === 'captain') return fetchJson(`${base}/captains/${encodeURIComponent(id)}`);
  throw new Error(`Unsupported detail type: ${kind}`);
}

async function loadMissionLog(id) {
  const base = instanceBaseUrl();
  return fetchJson(`${base}/missions/${encodeURIComponent(id)}/log?lines=240&offset=0`);
}

async function loadMissionDiff(id) {
  const base = instanceBaseUrl();
  return fetchJson(`${base}/missions/${encodeURIComponent(id)}/diff`);
}

function renderKeyValueCard(title, rows, extraClass = '') {
  const safeRows = rows && rows.length > 0 ? rows : [['Value', '-']];
  return `
    <section class="detail-card${extraClass ? ` ${extraClass}` : ''}">
      <h3>${escapeHtml(title)}</h3>
      ${safeRows.map(([key, value]) => `
        <div class="detail-row">
          <span class="detail-key">${escapeHtml(String(key))}</span>
          <span class="detail-value mono">${escapeHtml(String(value ?? '-'))}</span>
        </div>
      `).join('')}
    </section>
  `;
}

function renderMissionListCard(title, rows) {
  return `
    <section class="detail-card">
      <h3>${escapeHtml(title)}</h3>
      <div class="detail-list">
        ${rows.length === 0 ? '<div class="text-muted">Nothing to show.</div>' : rows.map((mission) => `
          <button type="button" class="detail-list-item" data-detail-action="open-mission" data-id="${escapeHtml(mission.id || '')}">
            <span class="detail-list-copy">
              <span class="detail-list-title">${escapeHtml(mission.title || mission.id || 'Mission')}</span>
              <span class="detail-list-meta mono">${escapeHtml(mission.id || '')}</span>
            </span>
            ${renderBadge(mission.status || 'unknown')}
          </button>
        `).join('')}
      </div>
    </section>
  `;
}

function renderSelectedPlaybooksCard(title, selections) {
  const rows = (selections || []).map((selection) => {
    const match = (state.playbooks || []).find((playbook) => playbook.id === selection.playbookId);
    return [
      match?.fileName || selection.playbookId || 'Playbook',
      playbookDeliveryModeLabel(selection.deliveryMode),
    ];
  });

  return renderKeyValueCard(title, rows, 'detail-card-full');
}

function setDetailModalFrame(title, subtitleHtml, bodyHtml) {
  elements.detailModalTitle.textContent = title;
  elements.detailModalSubtitle.innerHTML = subtitleHtml || '';
  elements.detailModalBody.innerHTML = bodyHtml;
}

function renderMissionDetailModal() {
  const modal = state.detailModal;
  const payload = modal?.payload || {};
  const mission = payload.mission || {};
  const captain = payload.captain || {};
  const voyage = payload.voyage || {};
  const vessel = payload.vessel || {};
  const dock = payload.dock || {};
  const activeTab = modal?.tab || 'overview';
  const log = modal?.log || '';
  const diff = modal?.diff || '';
  const logMeta = modal?.logMeta || {};

  let tabMarkup = '';
  if (activeTab === 'overview') {
    tabMarkup = `
      <div class="detail-grid">
        ${renderKeyValueCard('Mission', [
          ['Status', mission.status],
          ['Persona', mission.persona || '-'],
          ['Priority', mission.priority ?? 100],
          ['Branch', mission.branchName || '-'],
          ['Runtime', mission.totalRuntimeMs != null ? `${mission.totalRuntimeMs} ms` : '-'],
          ['Created', formatTimestamp(mission.createdUtc)],
          ['Updated', formatTimestamp(mission.lastUpdateUtc)],
        ])}
        ${renderKeyValueCard('Routing', [
          ['Captain', captain.name || mission.captainId || '-'],
          ['Voyage', voyage.title || mission.voyageId || '-'],
          ['Vessel', vessel.name || mission.vesselId || '-'],
          ['Dock', dock.id || mission.dockId || '-'],
          ['Path', dock.worktreePath || '-'],
          ['Failure', mission.failureReason || '-'],
        ])}
      </div>
    `;
  } else if (activeTab === 'instructions') {
    tabMarkup = `
      <section class="detail-card detail-card-full">
        <h3>Instructions</h3>
        <div class="detail-prose">${escapeHtml(mission.description || 'No instructions provided.')}</div>
      </section>
    `;
  } else if (activeTab === 'log') {
    tabMarkup = `
      <div class="detail-actions detail-actions-inline">
        <button type="button" class="button" data-detail-action="mission-log-refresh" data-id="${escapeHtml(mission.id || '')}">Reload Log</button>
        <span class="text-muted">Showing ${escapeHtml(String(logMeta.totalLines ?? logMeta.lines ?? 0))} log lines</span>
      </div>
      <pre class="code-view">${escapeHtml(log || 'No log content available.')}</pre>
    `;
  } else if (activeTab === 'diff') {
    tabMarkup = `
      <div class="detail-actions detail-actions-inline">
        <button type="button" class="button" data-detail-action="mission-diff-refresh" data-id="${escapeHtml(mission.id || '')}">Reload Diff</button>
      </div>
      <pre class="code-view">${escapeHtml(diff || 'No diff content available.')}</pre>
    `;
  }

  setDetailModalFrame(
    mission.title || mission.id || 'Mission Detail',
    `${renderBadge(mission.status || 'unknown')} <span class="mono">${escapeHtml(mission.id || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-actions">
          <button type="button" class="button" data-detail-action="mission-edit" data-id="${escapeHtml(mission.id || '')}">Edit</button>
          <button type="button" class="button" data-detail-action="mission-restart" data-id="${escapeHtml(mission.id || '')}">Restart</button>
          <button type="button" class="button" data-detail-action="mission-cancel" data-id="${escapeHtml(mission.id || '')}">Cancel</button>
        </div>
        <div class="tab-strip">
          <button type="button" class="tab-button${activeTab === 'overview' ? ' active' : ''}" data-detail-tab="overview">Overview</button>
          <button type="button" class="tab-button${activeTab === 'instructions' ? ' active' : ''}" data-detail-tab="instructions">Instructions</button>
          <button type="button" class="tab-button${activeTab === 'log' ? ' active' : ''}" data-detail-tab="log">Log</button>
          <button type="button" class="tab-button${activeTab === 'diff' ? ' active' : ''}" data-detail-tab="diff">Diff</button>
        </div>
        ${tabMarkup}
      </div>
    `,
  );
}

function renderVoyageDetailModal() {
  const payload = state.detailModal?.payload || {};
  const voyage = payload.voyage || {};
  const missions = payload.missions || [];
  const selectedPlaybooks = voyage.selectedPlaybooks || [];

  setDetailModalFrame(
    voyage.title || voyage.id || 'Voyage Detail',
    `${renderBadge(voyage.status || 'unknown')} <span class="mono">${escapeHtml(voyage.id || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-actions">
          <button type="button" class="button" data-detail-action="voyage-cancel" data-id="${escapeHtml(voyage.id || '')}">Cancel Voyage</button>
        </div>
        <div class="detail-grid">
          ${renderKeyValueCard('Voyage', [
            ['Status', voyage.status],
            ['Created', formatTimestamp(voyage.createdUtc)],
            ['Updated', formatTimestamp(voyage.lastUpdateUtc)],
            ['Completed', formatTimestamp(voyage.completedUtc)],
            ['Missions', missions.length],
          ])}
          ${renderKeyValueCard('Description', [['Summary', voyage.description || '-']])}
          ${selectedPlaybooks.length > 0 ? renderSelectedPlaybooksCard('Playbooks', selectedPlaybooks) : ''}
        </div>
        ${renderMissionListCard('Mission Chain', missions)}
      </div>
    `,
  );
}

function renderCaptainDetailModal() {
  const payload = state.detailModal?.payload || {};
  const captain = payload.captain || {};
  const currentMission = payload.currentMission || {};
  const currentDock = payload.currentDock || {};
  const recentMissions = payload.recentMissions || [];

  setDetailModalFrame(
    captain.name || captain.id || 'Captain Detail',
    `${renderBadge(captain.state || 'unknown')} <span class="mono">${escapeHtml(captain.id || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-actions">
          <button type="button" class="button" data-detail-action="captain-log" data-id="${escapeHtml(captain.id || '')}">Load Captain Log</button>
          <button type="button" class="button" data-detail-action="captain-tools" data-id="${escapeHtml(captain.id || '')}">View Tools</button>
          <button type="button" class="button" data-detail-action="captain-stop" data-id="${escapeHtml(captain.id || '')}">Stop Captain</button>
        </div>
        <div class="detail-grid">
          ${renderKeyValueCard('Captain', [
            ['State', captain.state],
            ['Runtime', captain.runtime || '-'],
            ['Model', captain.model || 'auto'],
            ['Heartbeat', formatTimestamp(captain.lastHeartbeatUtc)],
            ['Current Mission', currentMission.title || captain.currentMissionId || '-'],
            ['Current Dock', currentDock.id || captain.currentDockId || '-'],
          ])}
          ${renderMissionListCard('Recent Work', recentMissions.slice(0, 8))}
        </div>
        <pre class="code-view">${escapeHtml(state.detailModal?.log || 'Select "Load Captain Log" to inspect the active captain session.')}</pre>
      </div>
    `,
  );
}

function renderJsonCard(title, value) {
  return `
    <section class="detail-card detail-card-full">
      <h3>${escapeHtml(title)}</h3>
      <pre class="code-view">${escapeHtml(safeJsonStringify(value))}</pre>
    </section>
  `;
}

function renderMessageTranscriptCard(title, messages) {
  return `
    <section class="detail-card detail-card-full">
      <h3>${escapeHtml(title)}</h3>
      <div class="detail-list">
        ${(messages || []).length === 0
          ? '<div class="text-muted">No transcript messages yet.</div>'
          : messages.map((message) => `
            <div class="detail-list-item detail-list-item-static">
              <span class="detail-list-copy">
                <span class="detail-list-title">${escapeHtml(message.role || 'Message')}</span>
                <span class="detail-list-meta">${formatTimestamp(message.createdUtc)}${message.isSelected || message.isSelectedForDispatch ? ' | Selected' : ''}</span>
              </span>
              <div class="detail-prose">${escapeHtml(message.content || '')}</div>
            </div>
          `).join('')}
      </div>
    </section>
  `;
}

function renderIdListCard(title, ids, action, resourceKey = '') {
  return `
    <section class="detail-card">
      <h3>${escapeHtml(title)}</h3>
      <div class="detail-list">
        ${(ids || []).length === 0
          ? '<div class="text-muted">Nothing linked yet.</div>'
          : ids.map((id) => `
            <button type="button" class="detail-list-item" data-detail-action="${escapeHtml(action)}" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(id)}">
              <span class="detail-list-copy">
                <span class="detail-list-title mono">${escapeHtml(id)}</span>
              </span>
              ${renderBadge('Open')}
            </button>
          `).join('')}
      </div>
    </section>
  `;
}

function renderLinkedResourceSection(title, ids, resourceKey, action = 'open-resource-detail') {
  const items = (ids || []).filter((id) => Boolean(String(id || '').trim()));
  if (items.length === 0) {
    return '';
  }

  return renderIdListCard(title, items, action, resourceKey);
}

function renderLinkedSingleResourceSection(title, id, resourceKey, action = 'open-resource-detail') {
  const normalized = String(id || '').trim();
  if (!normalized) {
    return '';
  }

  return renderIdListCard(title, [normalized], action, resourceKey);
}

function renderGenericResourceLinkSections(resourceKey, row) {
  const sections = [];

  if (resourceKey === 'release') {
    sections.push(renderLinkedResourceSection('Linked Objectives', row.objectiveIds, '', 'open-backlog-item'));
    sections.push(renderLinkedResourceSection('Linked Check Runs', row.checkRunIds, 'checkRun'));
    sections.push(renderLinkedResourceSection('Linked Deployments', row.deploymentIds, 'deployment'));
  }

  if (resourceKey === 'deployment') {
    sections.push(renderLinkedResourceSection('Linked Objectives', row.objectiveIds, '', 'open-backlog-item'));
    sections.push(renderLinkedSingleResourceSection('Release', row.releaseId, 'release'));
    sections.push(renderLinkedSingleResourceSection('Environment', row.environmentId, 'environment'));
    sections.push(renderLinkedSingleResourceSection('Workflow Profile', row.workflowProfileId, 'workflowProfile'));
  }

  if (resourceKey === 'incident') {
    sections.push(renderLinkedResourceSection('Linked Objectives', row.objectiveIds, '', 'open-backlog-item'));
    sections.push(renderLinkedSingleResourceSection('Deployment', row.deploymentId, 'deployment'));
    sections.push(renderLinkedSingleResourceSection('Release', row.releaseId, 'release'));
    sections.push(renderLinkedSingleResourceSection('Environment', row.environmentId, 'environment'));
  }

  if (resourceKey === 'runbook') {
    sections.push(renderLinkedSingleResourceSection('Workflow Profile', row.workflowProfileId, 'workflowProfile'));
    sections.push(renderLinkedSingleResourceSection('Environment', row.environmentId, 'environment'));
  }

  if (resourceKey === 'runbookExecution') {
    sections.push(renderLinkedSingleResourceSection('Runbook', row.runbookId, 'runbook'));
    sections.push(renderLinkedSingleResourceSection('Deployment', row.deploymentId, 'deployment'));
    sections.push(renderLinkedSingleResourceSection('Incident', row.incidentId, 'incident'));
    sections.push(renderLinkedSingleResourceSection('Environment', row.environmentId, 'environment'));
  }

  if (resourceKey === 'persona') {
    sections.push(renderLinkedSingleResourceSection('Backing Prompt Template', row.promptTemplateId, 'promptTemplate'));
  }

  return sections.filter(Boolean).join('');
}

function buildGenericResourceRows(resourceKey, row) {
  const rows = [
    ['Id', row.id || row.name || '-'],
    ['Status', row.status || row.state || '-'],
    ['Title', row.title || row.name || row.fileName || '-'],
    ['Description', row.description || row.summary || row.notes || '-'],
    ['Vessel', row.vesselId || '-'],
    ['Environment', row.environmentName || row.environmentId || '-'],
    ['Workflow Profile', row.workflowProfileId || '-'],
    ['Created', formatTimestamp(row.createdUtc)],
    ['Updated', formatTimestamp(row.lastUpdateUtc)],
  ];

  if (resourceKey === 'workflowProfile') {
    rows.push(['Scope', row.scope || '-']);
    rows.push(['Default', row.isDefault ? 'Yes' : 'No']);
  }

  if (resourceKey === 'checkRun') {
    rows.push(['Type', row.type || '-']);
    rows.push(['Command', row.command || row.commandOverride || '-']);
  }

  if (resourceKey === 'release') {
    rows.push(['Version', row.version || '-']);
    rows.push(['Tag', row.tagName || '-']);
  }

  if (resourceKey === 'deployment') {
    rows.push(['Verification', row.verificationStatus || '-']);
    rows.push(['Release', row.releaseId || '-']);
  }

  if (resourceKey === 'incident') {
    rows.push(['Severity', row.severity || '-']);
    rows.push(['Impact', row.impact || '-']);
  }

  if (resourceKey === 'runbook') {
    rows.push(['File', row.fileName || '-']);
    rows.push(['Default Check', row.defaultCheckType || '-']);
  }

  if (resourceKey === 'runbookExecution') {
    rows.push(['Runbook', row.runbookId || '-']);
    rows.push(['Execution Status', row.status || '-']);
  }

  if (resourceKey === 'pipeline') {
    rows.push(['Stages', Array.isArray(row.stages) ? String(row.stages.length) : '0']);
  }

  if (resourceKey === 'persona') {
    rows.push(['Category', row.category || '-']);
    rows.push(['Prompt Template', row.promptTemplateId || '-']);
  }

  if (resourceKey === 'promptTemplate') {
    rows.push(['Category', row.category || '-']);
  }

  return rows;
}

function renderResourceActionButtons(resourceKey, row) {
  const definition = RESOURCE_DEFINITIONS[resourceKey];
  if (!definition) return '';

  const buttons = [];
  if (!definition.readOnly && definition.updateSupported !== false) {
    buttons.push(`<button type="button" class="button" data-detail-action="resource-edit" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Edit</button>`);
  }

  if (resourceKey === 'checkRun') {
    buttons.push(`<button type="button" class="button" data-detail-action="check-run-retry" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Retry</button>`);
  }

  if (resourceKey === 'release') {
    buttons.push(`<button type="button" class="button" data-detail-action="release-refresh" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Refresh</button>`);
  }

  if (resourceKey === 'deployment') {
    buttons.push(`<button type="button" class="button" data-detail-action="deployment-approve" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Approve</button>`);
    buttons.push(`<button type="button" class="button" data-detail-action="deployment-deny" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Deny</button>`);
    buttons.push(`<button type="button" class="button" data-detail-action="deployment-verify" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Verify</button>`);
    buttons.push(`<button type="button" class="button" data-detail-action="deployment-rollback" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Rollback</button>`);
  }

  if (resourceKey === 'runbook') {
    buttons.push(`<button type="button" class="button" data-detail-action="runbook-execute" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Start Execution</button>`);
  }

  if (!definition.readOnly && resourceKey !== 'checkRun') {
    buttons.push(`<button type="button" class="button button-danger" data-detail-action="resource-delete" data-resource-key="${escapeHtml(resourceKey)}" data-id="${escapeHtml(row.id || '')}">Delete</button>`);
  }

  return buttons.join('');
}

function renderBacklogDetailModal() {
  const objective = coerceDetailPayload(state.detailModal?.payload || {}, 'objective');
  const latestPlanningId = pickFirst(objective.planningSessionIds);
  const latestRefinementId = pickFirst(objective.refinementSessionIds);
  setDetailModalFrame(
    objective.title || objective.id || 'Backlog Item',
    `${renderBadge(objective.backlogState || objective.status || 'unknown')} <span class="mono">${escapeHtml(objective.id || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-actions">
          <button type="button" class="button" data-detail-action="objective-edit" data-id="${escapeHtml(objective.id || '')}">Edit</button>
          <button type="button" class="button" data-detail-action="objective-start-refinement" data-id="${escapeHtml(objective.id || '')}">Start Refinement</button>
          <button type="button" class="button" data-detail-action="objective-create-planning" data-id="${escapeHtml(objective.id || '')}">Create Planning Session</button>
          ${latestRefinementId ? `<button type="button" class="button" data-detail-action="open-refinement-session" data-id="${escapeHtml(latestRefinementId)}">Open Latest Refinement</button>` : ''}
          ${latestPlanningId ? `<button type="button" class="button" data-detail-action="open-planning-session" data-id="${escapeHtml(latestPlanningId)}">Open Latest Planning</button>` : ''}
          <button type="button" class="button button-danger" data-detail-action="objective-delete" data-id="${escapeHtml(objective.id || '')}">Delete</button>
        </div>
        <div class="detail-grid">
          ${renderKeyValueCard('Backlog Item', [
            ['Status', objective.status || '-'],
            ['Backlog State', objective.backlogState || '-'],
            ['Kind', objective.kind || '-'],
            ['Priority', objective.priority || '-'],
            ['Effort', objective.effort || '-'],
            ['Owner', objective.owner || '-'],
            ['Target Version', objective.targetVersion || '-'],
            ['Due', formatTimestamp(objective.dueUtc)],
          ])}
          ${renderKeyValueCard('Scope And Lineage', [
            ['Parent Objective', objective.parentObjectiveId || '-'],
            ['Vessel', pickFirst(objective.vesselIds) || '-'],
            ['Fleet', pickFirst(objective.fleetIds) || '-'],
            ['Planning Sessions', String((objective.planningSessionIds || []).length)],
            ['Refinement Sessions', String((objective.refinementSessionIds || []).length)],
            ['Voyages', String((objective.voyageIds || []).length)],
            ['Missions', String((objective.missionIds || []).length)],
          ])}
          ${renderKeyValueCard('Description', [['Summary', objective.description || '-'], ['Refinement Summary', objective.refinementSummary || '-']])}
          ${renderKeyValueCard('Lists', [
            ['Tags', (objective.tags || []).join(', ') || '-'],
            ['Blocked By', (objective.blockedByObjectiveIds || []).join(', ') || '-'],
            ['Acceptance Criteria', (objective.acceptanceCriteria || []).join(' | ') || '-'],
            ['Non-Goals', (objective.nonGoals || []).join(' | ') || '-'],
          ], 'detail-card-full')}
        </div>
        ${renderIdListCard('Linked Refinement Sessions', objective.refinementSessionIds || [], 'open-refinement-session')}
        ${renderIdListCard('Linked Planning Sessions', objective.planningSessionIds || [], 'open-planning-session')}
        ${renderJsonCard('Raw JSON', objective)}
      </div>
    `,
  );
}

function renderObjectiveRefinementDetailModal() {
  const payload = state.detailModal?.payload || {};
  const session = payload.session || payload.Session || {};
  const messages = payload.messages || payload.Messages || [];
  const captain = payload.captain || payload.Captain || {};
  const vessel = payload.vessel || payload.Vessel || {};
  const objective = payload.objective || payload.Objective || {};

  setDetailModalFrame(
    session.title || session.id || 'Refinement Session',
    `${renderBadge(session.status || 'unknown')} <span class="mono">${escapeHtml(session.id || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-actions">
          <button type="button" class="button" data-detail-action="refinement-message" data-id="${escapeHtml(session.id || '')}">Send Message</button>
          <button type="button" class="button" data-detail-action="refinement-summarize" data-id="${escapeHtml(session.id || '')}">Summarize</button>
          <button type="button" class="button" data-detail-action="refinement-apply" data-id="${escapeHtml(session.id || '')}">Apply To Objective</button>
          <button type="button" class="button" data-detail-action="refinement-stop" data-id="${escapeHtml(session.id || '')}">Stop</button>
          <button type="button" class="button" data-detail-action="open-backlog-item" data-id="${escapeHtml(objective.id || session.objectiveId || '')}">Open Objective</button>
          <button type="button" class="button button-danger" data-detail-action="refinement-delete" data-id="${escapeHtml(session.id || '')}">Delete</button>
        </div>
        <div class="detail-grid">
          ${renderKeyValueCard('Session', [
            ['Captain', captain.name || session.captainId || '-'],
            ['Vessel', vessel.name || session.vesselId || '-'],
            ['Objective', objective.title || session.objectiveId || '-'],
            ['Status', session.status || '-'],
            ['Created', formatTimestamp(session.createdUtc)],
            ['Updated', formatTimestamp(session.lastUpdateUtc)],
          ])}
          ${renderKeyValueCard('Summary', [
            ['Selected', String(messages.filter((message) => message.isSelected).length)],
            ['Failure', session.failureReason || '-'],
            ['Messages', String(messages.length)],
            ['Process', session.processId != null ? String(session.processId) : '-'],
          ])}
        </div>
        ${renderMessageTranscriptCard('Transcript', messages)}
        ${renderJsonCard('Raw JSON', payload)}
      </div>
    `,
  );
}

function renderPlanningSessionDetailModal() {
  const payload = state.detailModal?.payload || {};
  const session = payload.session || payload.Session || {};
  const messages = payload.messages || payload.Messages || [];
  const captain = payload.captain || payload.Captain || {};
  const vessel = payload.vessel || payload.Vessel || {};
  const linkedObjective = (state.objectives || []).find((objective) => (objective.planningSessionIds || []).includes(session.id));

  setDetailModalFrame(
    session.title || session.id || 'Planning Session',
    `${renderBadge(session.status || 'unknown')} <span class="mono">${escapeHtml(session.id || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-actions">
          <button type="button" class="button" data-detail-action="planning-message" data-id="${escapeHtml(session.id || '')}">Send Message</button>
          <button type="button" class="button" data-detail-action="planning-summarize" data-id="${escapeHtml(session.id || '')}">Summarize</button>
          <button type="button" class="button" data-detail-action="planning-dispatch" data-id="${escapeHtml(session.id || '')}">Dispatch</button>
          <button type="button" class="button" data-detail-action="planning-stop" data-id="${escapeHtml(session.id || '')}">Stop</button>
          ${linkedObjective ? `<button type="button" class="button" data-detail-action="open-backlog-item" data-id="${escapeHtml(linkedObjective.id || '')}">Open Objective</button>` : ''}
          <button type="button" class="button button-danger" data-detail-action="planning-delete" data-id="${escapeHtml(session.id || '')}">Delete</button>
        </div>
        <div class="detail-grid">
          ${renderKeyValueCard('Session', [
            ['Captain', captain.name || session.captainId || '-'],
            ['Vessel', vessel.name || session.vesselId || '-'],
            ['Pipeline', session.pipelineId || '-'],
            ['Status', session.status || '-'],
            ['Created', formatTimestamp(session.createdUtc)],
            ['Updated', formatTimestamp(session.lastUpdateUtc)],
          ])}
          ${renderKeyValueCard('Scope', [
            ['Fleet', session.fleetId || '-'],
            ['Dock', session.dockId || '-'],
            ['Branch', session.branchName || '-'],
            ['Failure', session.failureReason || '-'],
            ['Linked Objective', linkedObjective?.title || linkedObjective?.id || '-'],
            ['Messages', String(messages.length)],
          ])}
        </div>
        ${renderMessageTranscriptCard('Transcript', messages)}
        ${renderJsonCard('Raw JSON', payload)}
      </div>
    `,
  );
}

function renderGenericResourceDetailModal() {
  const payload = state.detailModal?.payload || {};
  const resourceKey = state.detailModal?.resourceKey || '';
  const definition = RESOURCE_DEFINITIONS[resourceKey];
  const row = payload;
  const title = row.title || row.name || row.fileName || row.id || definition?.label || 'Detail';
  const subtitle = `${renderBadge(row.status || row.state || row.scope || row.kind || row.type || 'detail')} <span class="mono">${escapeHtml(row.id || row.name || '')}</span>`;
  setDetailModalFrame(
    title,
    subtitle,
    `
      <div class="detail-body">
        <div class="detail-actions">
          ${renderResourceActionButtons(resourceKey, row)}
        </div>
        <div class="detail-grid">
          ${renderKeyValueCard(definition?.label || 'Resource', buildGenericResourceRows(resourceKey, row))}
        </div>
        ${renderGenericResourceLinkSections(resourceKey, row)}
        ${renderJsonCard('Raw JSON', row)}
      </div>
    `,
  );
}

function renderCaptainToolsDetailModal() {
  const payload = state.detailModal?.payload || {};
  const servers = payload.servers || [];
  const tools = payload.tools || [];
  setDetailModalFrame(
    payload.captainName || payload.captainId || 'Captain Tools',
    `${renderBadge(payload.toolsAccessible ? 'Accessible' : 'Unavailable')} <span class="mono">${escapeHtml(payload.runtime || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-grid">
          ${renderKeyValueCard('Runtime Summary', [
            ['Runtime', payload.runtime || '-'],
            ['Accessible', payload.toolsAccessible ? 'Yes' : 'No'],
            ['Availability Verified', payload.availabilityVerified ? 'Yes' : 'No'],
            ['Configured Sources', String(payload.configuredServerCount ?? 0)],
            ['Reachable Sources', String(payload.reachableServerCount ?? 0)],
            ['Effective Tool Count', payload.effectiveToolCount != null ? String(payload.effectiveToolCount) : '-'],
          ])}
          ${renderKeyValueCard('Notes', [['Summary', payload.summary || '-'], ['Source', payload.availabilitySource || '-']])}
        </div>
        ${renderJsonCard('Configured Sources', servers)}
        ${renderJsonCard('Named Tools', tools)}
      </div>
    `,
  );
}

function renderRequestHistoryDetailModal() {
  const payload = state.detailModal?.payload || {};
  const entry = payload.entry || payload.Entry || payload;
  setDetailModalFrame(
    `${entry.method || '-'} ${entry.route || '-'}`,
    `${renderBadge(entry.isSuccess ? 'Success' : 'Failure')} <span class="mono">${escapeHtml(entry.id || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-grid">
          ${renderKeyValueCard('Request', [
            ['Method', entry.method || '-'],
            ['Route', entry.route || '-'],
            ['Status Code', entry.statusCode != null ? String(entry.statusCode) : '-'],
            ['Principal', entry.principal || '-'],
            ['Duration', entry.durationMs != null ? `${Number(entry.durationMs).toFixed(1)} ms` : '-'],
            ['Created', formatTimestamp(entry.createdUtc)],
          ])}
        </div>
        ${renderJsonCard('Raw JSON', payload)}
      </div>
    `,
  );
}

function renderWorkspaceFileDetailModal() {
  const payload = state.detailModal?.payload || {};
  setDetailModalFrame(
    payload.name || payload.path || 'Workspace File',
    `${renderBadge(payload.isBinary ? 'Binary' : (payload.language || 'Text'))} <span class="mono">${escapeHtml(payload.path || '')}</span>`,
    `
      <div class="detail-body">
        <div class="detail-grid">
          ${renderKeyValueCard('File', [
            ['Path', payload.path || '-'],
            ['Language', payload.language || '-'],
            ['Size', payload.sizeBytes != null ? `${payload.sizeBytes} bytes` : '-'],
            ['Editable', payload.isEditable ? 'Yes' : 'No'],
            ['Last Write', formatTimestamp(payload.lastWriteUtc)],
            ['Preview Truncated', payload.previewTruncated ? 'Yes' : 'No'],
          ])}
        </div>
        <pre class="code-view">${escapeHtml(payload.content || 'No preview content available.')}</pre>
      </div>
    `,
  );
}

function renderDetailModal() {
  if (!state.detailModal) {
    setDetailModalFrame('Detail', '', '<div class="detail-empty">No detail selected.</div>');
    return;
  }

  if (state.detailModal.loading) {
    setDetailModalFrame(
      state.detailModal.title || 'Loading detail',
      state.detailModal.subtitle || '',
      '<div class="detail-empty">Loading detail from the deployment...</div>',
    );
    return;
  }

  if (state.detailModal.type === 'mission') {
    renderMissionDetailModal();
    return;
  }

  if (state.detailModal.type === 'voyage') {
    renderVoyageDetailModal();
    return;
  }

  if (state.detailModal.type === 'captain') {
    renderCaptainDetailModal();
    return;
  }

  if (state.detailModal.type === 'backlog') {
    renderBacklogDetailModal();
    return;
  }

  if (state.detailModal.type === 'refinement') {
    renderObjectiveRefinementDetailModal();
    return;
  }

  if (state.detailModal.type === 'planning') {
    renderPlanningSessionDetailModal();
    return;
  }

  if (state.detailModal.type === 'generic-resource') {
    renderGenericResourceDetailModal();
    return;
  }

  if (state.detailModal.type === 'captain-tools') {
    renderCaptainToolsDetailModal();
    return;
  }

  if (state.detailModal.type === 'request-history') {
    renderRequestHistoryDetailModal();
    return;
  }

  if (state.detailModal.type === 'workspace-file') {
    renderWorkspaceFileDetailModal();
    return;
  }

  setDetailModalFrame('Detail', '', '<div class="detail-empty">Unsupported detail type.</div>');
}

function showDetailLoading(type, title, subtitle = '') {
  state.detailModal = { type, title, subtitle, loading: true, tab: 'overview' };
  renderDetailModal();
  openModal(elements.detailModal);
}

async function openMissionDetailModal(id, preferredTab = 'overview') {
  showDetailLoading('mission', 'Mission Detail');
  try {
    const [payload, logData, diffData] = await Promise.all([
      loadDetailPayload('mission', id),
      loadMissionLog(id).catch(() => ({ log: 'Unable to load mission log.', lines: 0, totalLines: 0 })),
      loadMissionDiff(id).catch(() => ({ diff: 'Unable to load mission diff.' })),
    ]);

    state.detailModal = {
      type: 'mission',
      id,
      tab: preferredTab,
      loading: false,
      payload,
      log: logData.log || '',
      logMeta: logData,
      diff: diffData.diff || '',
    };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    state.detailModal = { type: 'mission', id, loading: false, payload: null };
    setDetailModalFrame(
      'Mission Detail',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load mission detail.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openVoyageDetailModal(id) {
  showDetailLoading('voyage', 'Voyage Detail');
  try {
    const payload = await loadDetailPayload('voyage', id);
    state.detailModal = { type: 'voyage', id, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Voyage Detail',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load voyage detail.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openCaptainDetailModal(id) {
  showDetailLoading('captain', 'Captain Detail');
  try {
    const payload = await loadDetailPayload('captain', id);
    state.detailModal = { type: 'captain', id, loading: false, payload, log: '' };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Captain Detail',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load captain detail.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openBacklogDetailModal(id) {
  showDetailLoading('backlog', 'Backlog Item');
  try {
    const payload = await fetchJson(`${instanceBaseUrl()}/backlog/${encodeURIComponent(id)}`);
    state.detailModal = { type: 'backlog', id, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Backlog Item',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load backlog item.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openObjectiveRefinementDetailModal(id) {
  showDetailLoading('refinement', 'Refinement Session');
  try {
    const payload = await fetchJson(`${instanceBaseUrl()}/objective-refinement-sessions/${encodeURIComponent(id)}`);
    state.detailModal = { type: 'refinement', id, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Refinement Session',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load refinement session.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openPlanningSessionDetailModal(id) {
  showDetailLoading('planning', 'Planning Session');
  try {
    const payload = await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(id)}`);
    state.detailModal = { type: 'planning', id, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Planning Session',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load planning session.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openResourceDetailModal(resourceKey, id) {
  const definition = RESOURCE_DEFINITIONS[resourceKey];
  if (!definition) return;
  showDetailLoading('generic-resource', `${definition.label} Detail`);
  try {
    const payload = await fetchJson(`${instanceBaseUrl()}${definition.detailPath(id)}`);
    state.detailModal = { type: 'generic-resource', id, resourceKey, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      `${definition.label} Detail`,
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : `Unable to load ${definition.label.toLowerCase()}.`)}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openCaptainToolsDetailModal(id) {
  showDetailLoading('captain-tools', 'Captain Tools');
  try {
    const payload = await loadCaptainToolAccess(id);
    state.detailModal = { type: 'captain-tools', id, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Captain Tools',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to inspect captain tools.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openRequestHistoryDetailModal(id) {
  showDetailLoading('request-history', 'Request History');
  try {
    const payload = await fetchJson(`${instanceBaseUrl()}/request-history/${encodeURIComponent(id)}`);
    state.detailModal = { type: 'request-history', id, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Request History',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load request-history detail.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function openWorkspaceFileDetailModal(vesselId, path) {
  if (!vesselId || !path) return;
  showDetailLoading('workspace-file', 'Workspace File');
  try {
    const query = buildQuery({ path });
    const payload = await fetchJson(`${instanceBaseUrl()}/workspace/vessels/${encodeURIComponent(vesselId)}/file${query}`);
    state.detailModal = { type: 'workspace-file', id: path, loading: false, payload };
    renderDetailModal();
    openModal(elements.detailModal);
  } catch (error) {
    setDetailModalFrame(
      'Workspace File',
      '',
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Unable to load workspace file.')}</div>`,
    );
    openModal(elements.detailModal);
  }
}

async function performDetailAction(action, id, resourceKey = '') {
  const base = instanceBaseUrl();

  try {
    if (action === 'open-mission') {
      await openMissionDetailModal(id);
      return;
    }

    if (action === 'mission-edit') {
      if (state.detailModal?.payload?.mission) openMissionModal(state.detailModal.payload.mission);
      return;
    }

    if (action === 'mission-log-refresh') {
      const logData = await loadMissionLog(id);
      if (state.detailModal) {
        state.detailModal.log = logData.log || '';
        state.detailModal.logMeta = logData;
      }
      renderDetailModal();
      return;
    }

    if (action === 'mission-diff-refresh') {
      const diffData = await loadMissionDiff(id);
      if (state.detailModal) state.detailModal.diff = diffData.diff || '';
      renderDetailModal();
      return;
    }

    if (action === 'mission-cancel') {
      if (!confirm('Cancel this mission?')) return;
      await fetchJson(`${base}/missions/${encodeURIComponent(id)}`, { method: 'DELETE' });
      await loadSelectedInstance();
      await openMissionDetailModal(id, state.detailModal?.tab || 'overview');
      return;
    }

    if (action === 'mission-restart') {
      if (!confirm('Restart this mission?')) return;
      await fetchJson(`${base}/missions/${encodeURIComponent(id)}/restart`, { method: 'POST', body: {} });
      await loadSelectedInstance();
      await openMissionDetailModal(id, state.detailModal?.tab || 'overview');
      return;
    }

    if (action === 'voyage-cancel') {
      if (!confirm('Cancel this voyage and its active work?')) return;
      await fetchJson(`${base}/voyages/${encodeURIComponent(id)}`, { method: 'DELETE' });
      await loadSelectedInstance();
      await openVoyageDetailModal(id);
      return;
    }

    if (action === 'captain-log') {
      const data = await fetchJson(`${base}/captains/${encodeURIComponent(id)}/log?lines=120&offset=0`);
      if (state.detailModal) state.detailModal.log = data.log || 'No log content available.';
      renderDetailModal();
      return;
    }

    if (action === 'captain-tools') {
      await openCaptainToolsDetailModal(id);
      return;
    }

    if (action === 'captain-stop') {
      if (!confirm('Stop this captain?')) return;
      await fetchJson(`${base}/captains/${encodeURIComponent(id)}/stop`, { method: 'POST' });
      await loadSelectedInstance();
      await openCaptainDetailModal(id);
      return;
    }

    if (action === 'objective-edit') {
      const objective = coerceDetailPayload(state.detailModal?.payload || {}, 'objective');
      openBacklogModal(objective);
      return;
    }

    if (action === 'objective-delete') {
      if (!confirm('Delete this backlog item?')) return;
      await fetchJson(`${base}/backlog/${encodeURIComponent(id)}`, { method: 'DELETE' });
      closeModal(elements.detailModal);
      await loadRecentBacklogList();
      return;
    }

    if (action === 'objective-start-refinement') {
      const objective = coerceDetailPayload(state.detailModal?.payload || {}, 'objective');
      const defaultCaptainId = state.summary?.recentCaptains?.[0]?.id || '';
      const captainId = prompt('Captain ID for refinement:', defaultCaptainId);
      if (!captainId) return;
      const initialMessage = prompt('Initial refinement prompt (optional):', objective.description || objective.title || '') || '';
      const payload = {
        captainId,
        vesselId: pickFirst(objective.vesselIds) || null,
        fleetId: pickFirst(objective.fleetIds) || null,
        title: objective.title || null,
        initialMessage: initialMessage.trim() || null,
      };
      const detail = await fetchJson(`${base}/backlog/${encodeURIComponent(id)}/refinement-sessions`, { method: 'POST', body: payload });
      await loadRecentBacklogList(true);
      const sessionId = detail?.session?.id || detail?.Session?.id || detail?.id;
      if (sessionId) {
        await openObjectiveRefinementDetailModal(sessionId);
      }
      return;
    }

    if (action === 'objective-create-planning') {
      const objective = coerceDetailPayload(state.detailModal?.payload || {}, 'objective');
      openPlanningModal({
        title: objective.title || '',
        objectiveId: objective.id || '',
        vesselId: pickFirst(objective.vesselIds) || '',
        pipelineId: objective.suggestedPipelineId || '',
        initialMessage: objective.refinementSummary || objective.description || '',
      });
      return;
    }

    if (action === 'open-refinement-session') {
      await openObjectiveRefinementDetailModal(id);
      return;
    }

    if (action === 'open-planning-session') {
      closeModal(elements.detailModal);
      await selectPlanningSession(id, { scroll: true });
      return;
    }

    if (action === 'open-backlog-item') {
      await openBacklogDetailModal(id);
      return;
    }

    if (action === 'open-resource-detail') {
      if (!resourceKey) return;
      await openResourceDetailModal(resourceKey, id);
      return;
    }

    if (action === 'refinement-message') {
      const content = prompt('Send message to this refinement session:', '');
      if (!content) return;
      await fetchJson(`${base}/objective-refinement-sessions/${encodeURIComponent(id)}/messages`, {
        method: 'POST',
        body: { content },
      });
      await openObjectiveRefinementDetailModal(id);
      await loadRecentBacklogList(true);
      return;
    }

    if (action === 'refinement-summarize') {
      await fetchJson(`${base}/objective-refinement-sessions/${encodeURIComponent(id)}/summarize`, {
        method: 'POST',
        body: {},
      });
      await openObjectiveRefinementDetailModal(id);
      await loadRecentBacklogList(true);
      return;
    }

    if (action === 'refinement-apply') {
      await fetchJson(`${base}/objective-refinement-sessions/${encodeURIComponent(id)}/apply`, {
        method: 'POST',
        body: {},
      });
      await openObjectiveRefinementDetailModal(id);
      await loadRecentBacklogList(true);
      return;
    }

    if (action === 'refinement-stop') {
      if (!confirm('Stop this refinement session?')) return;
      await fetchJson(`${base}/objective-refinement-sessions/${encodeURIComponent(id)}/stop`, { method: 'POST', body: {} });
      await openObjectiveRefinementDetailModal(id);
      return;
    }

    if (action === 'refinement-delete') {
      if (!confirm('Delete this refinement session?')) return;
      await fetchJson(`${base}/objective-refinement-sessions/${encodeURIComponent(id)}`, { method: 'DELETE' });
      closeModal(elements.detailModal);
      await loadRecentBacklogList(true);
      return;
    }

    if (action === 'planning-message') {
      closeModal(elements.detailModal);
      await selectPlanningSession(id, { scroll: true });
      return;
    }

    if (action === 'planning-summarize') {
      closeModal(elements.detailModal);
      await selectPlanningSession(id, { scroll: true });
      return;
    }

    if (action === 'planning-dispatch') {
      closeModal(elements.detailModal);
      await selectPlanningSession(id, { scroll: true });
      return;
    }

    if (action === 'planning-stop') {
      closeModal(elements.detailModal);
      await selectPlanningSession(id, { scroll: true });
      return;
    }

    if (action === 'planning-delete') {
      closeModal(elements.detailModal);
      await selectPlanningSession(id, { scroll: true });
      return;
    }

    if (action === 'resource-edit') {
      const payload = state.detailModal?.payload || {};
      openResourceEditor(resourceKey, 'update', payload, id);
      return;
    }

    if (action === 'resource-delete') {
      const definition = RESOURCE_DEFINITIONS[resourceKey];
      if (!definition) return;
      if (!confirm(`Delete this ${definition.label.toLowerCase()}?`)) return;
      await fetchJson(`${base}${definition.detailPath(id)}`, { method: 'DELETE' });
      closeModal(elements.detailModal);
      await loadResourceCollection(resourceKey);
      return;
    }

    if (action === 'check-run-retry') {
      await fetchJson(`${base}/check-runs/${encodeURIComponent(id)}/retry`, { method: 'POST', body: {} });
      await openResourceDetailModal('checkRun', id);
      await loadResourceCollection('checkRun');
      return;
    }

    if (action === 'release-refresh') {
      await fetchJson(`${base}/releases/${encodeURIComponent(id)}/refresh`, { method: 'POST', body: {} });
      await openResourceDetailModal('release', id);
      await loadResourceCollection('release');
      return;
    }

    if (action === 'deployment-approve') {
      const comment = prompt('Approval comment (optional):', '') || '';
      await fetchJson(`${base}/deployments/${encodeURIComponent(id)}/approve`, { method: 'POST', body: { comment: comment.trim() || null } });
      await openResourceDetailModal('deployment', id);
      await loadResourceCollection('deployment');
      return;
    }

    if (action === 'deployment-deny') {
      const comment = prompt('Denial comment (optional):', '') || '';
      await fetchJson(`${base}/deployments/${encodeURIComponent(id)}/deny`, { method: 'POST', body: { comment: comment.trim() || null } });
      await openResourceDetailModal('deployment', id);
      await loadResourceCollection('deployment');
      return;
    }

    if (action === 'deployment-verify') {
      await fetchJson(`${base}/deployments/${encodeURIComponent(id)}/verify`, { method: 'POST', body: {} });
      await openResourceDetailModal('deployment', id);
      await loadResourceCollection('deployment');
      return;
    }

    if (action === 'deployment-rollback') {
      if (!confirm('Trigger rollback for this deployment?')) return;
      await fetchJson(`${base}/deployments/${encodeURIComponent(id)}/rollback`, { method: 'POST', body: {} });
      await openResourceDetailModal('deployment', id);
      await loadResourceCollection('deployment');
      return;
    }

    if (action === 'runbook-execute') {
      const title = prompt('Execution title (optional):', '') || '';
      const detail = await fetchJson(`${base}/runbooks/${encodeURIComponent(id)}/executions`, {
        method: 'POST',
        body: { title: title.trim() || null },
      });
      await loadResourceCollection('runbookExecution');
      const executionId = detail?.id || detail?.runbookExecution?.id || detail?.RunbookExecution?.id;
      if (executionId) {
        await openResourceDetailModal('runbookExecution', executionId);
      }
      return;
    }
  } catch (error) {
    if (state.detailModal?.type === 'mission') {
      state.detailModal.log = error instanceof Error ? error.message : 'Action failed.';
      renderDetailModal();
      return;
    }

    setDetailModalFrame(
      elements.detailModalTitle.textContent || 'Detail',
      elements.detailModalSubtitle.innerHTML,
      `<div class="detail-empty">${escapeHtml(error instanceof Error ? error.message : 'Action failed.')}</div>`,
    );
  }
}

function parseDispatchMissions(raw) {
  return String(raw || '')
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      const separator = line.indexOf('::');
      if (separator >= 0) {
        return {
          title: line.slice(0, separator).trim(),
          description: line.slice(separator + 2).trim(),
        };
      }

      return {
        title: line,
        description: line,
      };
    })
    .filter((mission) => mission.title);
}

async function applyDispatchPriority(voyageId, priority) {
  if (!voyageId) return;
  const targetPriority = Number(priority);
  if (!Number.isFinite(targetPriority) || targetPriority === 100) return;

  const base = instanceBaseUrl();
  const detail = await fetchJson(`${base}/voyages/${encodeURIComponent(voyageId)}`);
  const missions = detail.missions || [];

  await Promise.all(missions.map((mission) => fetchJson(`${base}/missions/${encodeURIComponent(mission.id)}`, {
    method: 'PUT',
    body: {
      title: mission.title,
      description: mission.description || null,
      priority: targetPriority,
      vesselId: mission.vesselId || null,
      voyageId: mission.voyageId || null,
      branchName: mission.branchName || null,
      prUrl: mission.prUrl || null,
      parentMissionId: mission.parentMissionId || null,
      persona: mission.persona || null,
    },
  })));
}

async function submitFleetForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const payload = {
    name: elements.fleetName.value.trim(),
    description: elements.fleetDescription.value.trim() || null,
    defaultPipelineId: elements.fleetDefaultPipelineId.value.trim() || null,
    active: elements.fleetActive.checked,
  };

  if (!payload.name) {
    setFormStatus(elements.fleetFormStatus, 'Fleet name is required.', 'error');
    return;
  }

  try {
    const base = instanceBaseUrl();
    if (state.selectedFleetId) {
      await fetchJson(`${base}/fleets/${encodeURIComponent(state.selectedFleetId)}`, { method: 'PUT', body: payload });
      setFormStatus(elements.fleetFormStatus, 'Fleet updated.', 'success');
    } else {
      await fetchJson(`${base}/fleets`, { method: 'POST', body: payload });
      setFormStatus(elements.fleetFormStatus, 'Fleet created.', 'success');
    }

    await loadSelectedInstance();
    closeModal(elements.fleetModal);
    state.selectedFleetId = null;
    state.editingFleet = null;
  } catch (error) {
    setFormStatus(elements.fleetFormStatus, error instanceof Error ? error.message : 'Fleet save failed.', 'error');
  }
}

async function submitVesselForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const payload = {
    fleetId: elements.vesselFleetId.value.trim() || null,
    name: elements.vesselName.value.trim(),
    repoUrl: elements.vesselRepoUrl.value.trim(),
    workingDirectory: elements.vesselWorkingDirectory.value.trim() || null,
    defaultBranch: elements.vesselDefaultBranch.value.trim() || 'main',
    defaultPipelineId: elements.vesselDefaultPipelineId.value.trim() || null,
    allowConcurrentMissions: elements.vesselAllowConcurrentMissions.checked,
    active: elements.vesselActive.checked,
  };

  if (!payload.name || !payload.repoUrl) {
    setFormStatus(elements.vesselFormStatus, 'Vessel name and repo URL are required.', 'error');
    return;
  }

  try {
    const base = instanceBaseUrl();
    if (state.selectedVesselId) {
      await fetchJson(`${base}/vessels/${encodeURIComponent(state.selectedVesselId)}`, { method: 'PUT', body: payload });
      setFormStatus(elements.vesselFormStatus, 'Vessel updated.', 'success');
    } else {
      await fetchJson(`${base}/vessels`, { method: 'POST', body: payload });
      setFormStatus(elements.vesselFormStatus, 'Vessel created.', 'success');
    }

    await loadSelectedInstance();
    closeModal(elements.vesselModal);
    state.selectedVesselId = null;
    state.editingVessel = null;
  } catch (error) {
    setFormStatus(elements.vesselFormStatus, error instanceof Error ? error.message : 'Vessel save failed.', 'error');
  }
}

async function submitPlaybookForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const payload = {
    fileName: elements.playbookFileName.value.trim(),
    description: elements.playbookDescription.value.trim() || null,
    content: elements.playbookContent.value,
    active: elements.playbookActive.checked,
  };

  if (!payload.fileName || !payload.content.trim()) {
    setFormStatus(elements.playbookFormStatus, 'Playbook file name and content are required.', 'error');
    return;
  }

  try {
    const base = instanceBaseUrl();
    if (state.selectedPlaybookId) {
      await fetchJson(`${base}/playbooks/${encodeURIComponent(state.selectedPlaybookId)}`, { method: 'PUT', body: payload });
      setFormStatus(elements.playbookFormStatus, 'Playbook updated.', 'success');
    } else {
      await fetchJson(`${base}/playbooks`, { method: 'POST', body: payload });
      setFormStatus(elements.playbookFormStatus, 'Playbook created.', 'success');
    }

    await loadSelectedInstance();
    closeModal(elements.playbookModal);
    state.selectedPlaybookId = null;
    state.editingPlaybook = null;
  } catch (error) {
    setFormStatus(elements.playbookFormStatus, error instanceof Error ? error.message : 'Playbook save failed.', 'error');
  }
}

async function deleteSelectedPlaybook() {
  if (!state.selectedInstanceId || !state.selectedPlaybookId) return;
  if (!confirm('Delete this playbook?')) return;

  try {
    const base = instanceBaseUrl();
    await fetchJson(`${base}/playbooks/${encodeURIComponent(state.selectedPlaybookId)}`, { method: 'DELETE' });
    await loadSelectedInstance();
    closeModal(elements.playbookModal);
    state.selectedPlaybookId = null;
    state.editingPlaybook = null;
  } catch (error) {
    setFormStatus(elements.playbookFormStatus, error instanceof Error ? error.message : 'Playbook delete failed.', 'error');
  }
}

async function submitBacklogForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const vesselId = elements.backlogVesselId.value.trim() || null;
  const selectedVessel = (state.vessels || []).find((vessel) => vessel.id === vesselId) || null;
  const payload = {
    title: elements.backlogTitle.value.trim(),
    description: elements.backlogDescription.value.trim() || null,
    status: elements.backlogStatus.value || 'Draft',
    backlogState: elements.backlogBacklogState.value || 'Inbox',
    kind: elements.backlogKind.value || 'Feature',
    priority: elements.backlogPriority.value || 'P2',
    effort: elements.backlogEffort.value || 'M',
    owner: elements.backlogOwner.value.trim() || null,
    vesselIds: vesselId ? [vesselId] : [],
    fleetIds: selectedVessel?.fleetId ? [selectedVessel.fleetId] : [],
    targetVersion: elements.backlogTargetVersion.value.trim() || null,
    dueUtc: fromLocalDateTimeInputValue(elements.backlogDueUtc.value),
    parentObjectiveId: elements.backlogParentObjectiveId.value.trim() || null,
    blockedByObjectiveIds: parseCommaSeparatedValues(elements.backlogBlockedByObjectiveIds.value),
    tags: parseLineSeparatedValues(elements.backlogTags.value),
    acceptanceCriteria: parseLineSeparatedValues(elements.backlogAcceptanceCriteria.value),
    nonGoals: parseLineSeparatedValues(elements.backlogNonGoals.value),
    rolloutConstraints: parseLineSeparatedValues(elements.backlogRolloutConstraints.value),
    evidenceLinks: parseLineSeparatedValues(elements.backlogEvidenceLinks.value),
  };

  if (!payload.title) {
    setFormStatus(elements.backlogFormStatus, 'Backlog title is required.', 'error');
    return;
  }

  try {
    const base = instanceBaseUrl();
    if (state.selectedObjectiveId) {
      await fetchJson(`${base}/backlog/${encodeURIComponent(state.selectedObjectiveId)}`, { method: 'PUT', body: payload });
      setFormStatus(elements.backlogFormStatus, 'Backlog item updated.', 'success');
    } else {
      await fetchJson(`${base}/backlog`, { method: 'POST', body: payload });
      setFormStatus(elements.backlogFormStatus, 'Backlog item created.', 'success');
    }

    await loadRecentBacklogList(true);
    closeModal(elements.backlogModal);
    state.selectedObjectiveId = null;
    state.editingObjective = null;
  } catch (error) {
    setFormStatus(elements.backlogFormStatus, error instanceof Error ? error.message : 'Backlog save failed.', 'error');
  }
}

async function deleteSelectedBacklog() {
  if (!state.selectedInstanceId || !state.selectedObjectiveId) return;
  if (!confirm('Delete this backlog item?')) return;

  try {
    await fetchJson(`${instanceBaseUrl()}/backlog/${encodeURIComponent(state.selectedObjectiveId)}`, { method: 'DELETE' });
    await loadRecentBacklogList(true);
    closeModal(elements.backlogModal);
    state.selectedObjectiveId = null;
    state.editingObjective = null;
  } catch (error) {
    setFormStatus(elements.backlogFormStatus, error instanceof Error ? error.message : 'Backlog delete failed.', 'error');
  }
}

async function submitPlanningForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const payload = {
    title: elements.planningTitle.value.trim() || null,
    captainId: elements.planningCaptainId.value.trim(),
    vesselId: elements.planningVesselId.value.trim(),
    pipelineId: elements.planningPipelineId.value.trim() || null,
    objectiveId: elements.planningObjectiveId.value.trim() || null,
  };
  const initialMessage = elements.planningInitialMessage.value.trim();

  if (!payload.captainId || !payload.vesselId) {
    setFormStatus(elements.planningFormStatus, 'Captain and vessel are required.', 'error');
    return;
  }

  try {
    const detail = await fetchJson(`${instanceBaseUrl()}/planning-sessions`, { method: 'POST', body: payload });
    const sessionId = detail?.session?.id || detail?.Session?.id || detail?.id || '';
    if (sessionId && initialMessage) {
      await fetchJson(`${instanceBaseUrl()}/planning-sessions/${encodeURIComponent(sessionId)}/messages`, {
        method: 'POST',
        body: { content: initialMessage },
      });
    }
    await loadRecentPlanningList(true);
    closeModal(elements.planningModal);
    if (sessionId) {
      await selectPlanningSession(sessionId, {
        scroll: true,
        statusMessage: initialMessage ? 'Planning session created and seeded.' : 'Planning session created.',
        statusKind: 'success',
      });
    }
  } catch (error) {
    setFormStatus(elements.planningFormStatus, error instanceof Error ? error.message : 'Planning-session creation failed.', 'error');
  }
}

async function submitResourceEditorForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId || !state.resourceEditor) return;

  const definition = RESOURCE_DEFINITIONS[state.resourceEditor.resourceKey];
  if (!definition || definition.readOnly) return;

  let payload;
  try {
    payload = parseJsonPayload(elements.resourceEditorPayload.value, `${definition.label} payload is not valid JSON.`);
  } catch (error) {
    setFormStatus(elements.resourceEditorStatus, error instanceof Error ? error.message : 'Invalid JSON payload.', 'error');
    return;
  }

  try {
    const base = instanceBaseUrl();
    let response = null;
    if (state.resourceEditor.mode === 'update') {
      if (definition.updateSupported === false || !state.resourceEditor.id) {
        throw new Error(`${definition.label} cannot be updated through the proxy editor.`);
      }
      response = await fetchJson(`${base}${definition.detailPath(state.resourceEditor.id)}`, { method: 'PUT', body: payload });
    } else {
      response = await fetchJson(`${base}/${definition.endpoint}`, { method: 'POST', body: payload });
    }

    await loadResourceCollection(state.resourceEditor.resourceKey);
    setFormStatus(elements.resourceEditorStatus, `${definition.label} saved.`, 'success');
    const nextId = state.resourceEditor.mode === 'update'
      ? state.resourceEditor.id
      : extractPrimaryId(response);
    closeModal(elements.resourceEditorModal);
    if (nextId) {
      await openResourceDetailModal(state.resourceEditor.resourceKey, nextId);
    }
  } catch (error) {
    setFormStatus(elements.resourceEditorStatus, error instanceof Error ? error.message : `${definition.label} save failed.`, 'error');
  }
}

async function deleteResourceFromEditor() {
  if (!state.selectedInstanceId || !state.resourceEditor?.id) return;
  const definition = RESOURCE_DEFINITIONS[state.resourceEditor.resourceKey];
  if (!definition || definition.readOnly) return;
  if (!confirm(`Delete this ${definition.label.toLowerCase()}?`)) return;

  try {
    await fetchJson(`${instanceBaseUrl()}${definition.detailPath(state.resourceEditor.id)}`, { method: 'DELETE' });
    await loadResourceCollection(state.resourceEditor.resourceKey);
    closeModal(elements.resourceEditorModal);
  } catch (error) {
    setFormStatus(elements.resourceEditorStatus, error instanceof Error ? error.message : `${definition.label} delete failed.`, 'error');
  }
}

async function submitDispatchForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const missions = parseDispatchMissions(elements.dispatchMissions.value);
  const payload = {
    title: elements.dispatchTitle.value.trim(),
    description: elements.dispatchDescription.value.trim(),
    vesselId: elements.dispatchVesselId.value.trim(),
    pipelineId: elements.dispatchPipelineId.value.trim() || null,
    missions,
  };
  const selectedPlaybooks = (state.dispatchSelectedPlaybooks || []).map((selection) => ({
    playbookId: selection.playbookId,
    deliveryMode: selection.deliveryMode,
  }));
  const priority = Number.parseInt(elements.dispatchPriority.value || '100', 10) || 100;

  if (!payload.title) {
    setFormStatus(elements.dispatchFormStatus, 'Voyage title is required.', 'error');
    return;
  }

  if (!payload.vesselId) {
    setFormStatus(elements.dispatchFormStatus, 'Select a vessel for dispatch.', 'error');
    return;
  }

  if (missions.length === 0) {
    setFormStatus(elements.dispatchFormStatus, 'Provide at least one mission line.', 'error');
    return;
  }

  if (selectedPlaybooks.length > 0) {
    payload.selectedPlaybooks = selectedPlaybooks;
  }

  setButtonBusy(elements.dispatchSubmitButton, true, 'Dispatch', 'Dispatching...');
  setFormStatus(elements.dispatchFormStatus, 'Dispatching voyage...', null);

  try {
    const base = instanceBaseUrl();
    const voyage = await fetchJson(`${base}/voyages/dispatch`, { method: 'POST', body: payload });
    await applyDispatchPriority(voyage.id, priority);
    setFormStatus(elements.dispatchFormStatus, `Voyage dispatched: ${voyage.id || voyage.title || 'created'}`, 'success');
    await loadSelectedInstance();
    closeModal(elements.dispatchModal);
    if (voyage.id) await openVoyageDetailModal(voyage.id);
  } catch (error) {
    setFormStatus(elements.dispatchFormStatus, error instanceof Error ? error.message : 'Dispatch failed.', 'error');
  } finally {
    setButtonBusy(elements.dispatchSubmitButton, false, 'Dispatch', 'Dispatching...');
  }
}

async function submitMissionForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const payload = {
    title: elements.missionTitle.value.trim(),
    description: elements.missionDescription.value.trim() || null,
    vesselId: elements.missionVesselId.value.trim() || null,
    voyageId: elements.missionVoyageId.value.trim() || null,
    persona: elements.missionPersona.value.trim() || null,
    priority: Number.parseInt(elements.missionPriority.value || '100', 10) || 100,
  };

  if (!payload.title) {
    setFormStatus(elements.missionFormStatus, 'Mission title is required.', 'error');
    return;
  }

  try {
    const base = instanceBaseUrl();
    if (state.selectedMissionId) {
      await fetchJson(`${base}/missions/${encodeURIComponent(state.selectedMissionId)}`, { method: 'PUT', body: payload });
      setFormStatus(elements.missionFormStatus, 'Mission updated.', 'success');
    } else {
      await fetchJson(`${base}/missions`, { method: 'POST', body: payload });
      setFormStatus(elements.missionFormStatus, 'Mission created.', 'success');
    }

    const currentMissionId = state.selectedMissionId;
    await loadSelectedInstance();
    closeModal(elements.missionModal);
    if (currentMissionId) await openMissionDetailModal(currentMissionId);
    state.selectedMissionId = null;
    state.editingMission = null;
  } catch (error) {
    setFormStatus(elements.missionFormStatus, error instanceof Error ? error.message : 'Mission save failed.', 'error');
  }
}

async function submitMissionBrowseForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const query = buildQuery({
    limit: elements.missionBrowseLimit.value || '12',
    status: elements.missionBrowseStatus.value,
    voyageId: elements.missionBrowseVoyageId.value,
    vesselId: elements.missionBrowseVesselId.value,
  });

  try {
    const base = instanceBaseUrl();
    const data = await fetchJson(`${base}/missions${query}`);
    const rows = data.missions || [];
    renderEntityList(elements.missionList, rows, 'mission');
    setFormStatus(elements.missionBrowseStatusText, `Loaded ${rows.length} mission${rows.length === 1 ? '' : 's'} from the deployment.`, 'success');
  } catch (error) {
    setFormStatus(elements.missionBrowseStatusText, error instanceof Error ? error.message : 'Mission browse failed.', 'error');
  }
}

async function submitVoyageBrowseForm(event) {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const query = buildQuery({
    limit: elements.voyageBrowseLimit.value || '12',
    status: elements.voyageBrowseStatus.value,
  });

  try {
    const base = instanceBaseUrl();
    const data = await fetchJson(`${base}/voyages${query}`);
    const rows = data.voyages || [];
    renderEntityList(elements.voyageList, rows, 'voyage');
    setFormStatus(elements.voyageBrowseStatusText, `Loaded ${rows.length} voyage${rows.length === 1 ? '' : 's'} from the deployment.`, 'success');
  } catch (error) {
    setFormStatus(elements.voyageBrowseStatusText, error instanceof Error ? error.message : 'Voyage browse failed.', 'error');
  }
}

function bindSidebarNavigation() {
  document.querySelectorAll('[data-scroll-target]').forEach((button) => {
    button.addEventListener('click', () => {
      const targetId = button.getAttribute('data-scroll-target');
      if (!targetId) return;

      const target = document.getElementById(targetId);
      if (!target) return;

      document.querySelectorAll('.sidebar-nav-item').forEach((item) => item.classList.remove('active'));
      button.classList.add('active');
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      closeSidebar();
    });
  });
}

async function initializeProxyShell() {
  const storedDeploymentId = getStoredDeploymentId();
  const storedSessionToken = getStoredProxySessionToken();

  if (!storedSessionToken) {
    setLoginStep('proxy-password');
    renderSessionState();
    return;
  }

  setProxySession(storedSessionToken);

  try {
    await loadInstances();

    if (storedDeploymentId) {
      if (getInstanceById(storedDeploymentId)) {
        state.pendingInstanceId = storedDeploymentId;
        setLoginStep('deployment-password');
        setLoginStatus('Enter the password for the selected deployment.', null);
        renderSessionState();
        return;
      }

      setLoginStatus(`Deployment "${storedDeploymentId}" is not currently connected to this proxy.`, 'error');
      setLoginStep('deployment-select');
      renderSessionState();
      return;
    }

    setLoginStep('deployment-select');
    renderSessionState();
    if (state.instances.length > 0) {
      setLoginStatus('Choose a deployment to continue.', 'success');
    } else {
      setLoginStatus('No deployments available.', null);
    }
  } catch {
    logoutToLogin('Proxy session expired. Sign in again.');
  }
}

elements.proxyUnlockForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    setButtonBusy(elements.proxyUnlockButton, true, 'Continue', 'Checking...');
    setLoginStatus('Validating shared password...', null);
    await unlockProxySession(elements.loginPassword.value);
    setLoginStep('deployment-select');
    if (state.instances.length > 0) {
      setLoginStatus('Choose a deployment to continue.', 'success');
    } else {
      setLoginStatus('No deployments available.', null);
    }
  } catch (error) {
    setLoginStatus(error instanceof Error ? error.message : 'Proxy authentication failed.', 'error');
  } finally {
    setButtonBusy(elements.proxyUnlockButton, false, 'Continue', 'Checking...');
  }
});

elements.loginRefreshButton.addEventListener('click', async () => {
  try {
    if (!state.isAuthenticated || !state.sessionToken) {
      setLoginStep('proxy-password');
      setLoginStatus('Enter the shared password to unlock this proxy first.', 'error');
      return;
    }

    setLoginStatus('Refreshing deployments...', null);
    await loadInstances();
    if (state.instances.length > 0) {
      setLoginStatus('Deployment list refreshed.', 'success');
    } else {
      setLoginStatus('No deployments available.', null);
    }
  } catch (error) {
    setLoginStatus(error instanceof Error ? error.message : 'Failed to refresh deployments.', 'error');
  }
});

elements.proxyRelockButton.addEventListener('click', async () => {
  await revokeProxySession();
  logoutToLogin('');
});

elements.deploymentBackButton.addEventListener('click', () => {
  returnToDeploymentSelection('', state.pendingInstanceId || '');
});

elements.deploymentPasswordForm.addEventListener('submit', async (event) => {
  event.preventDefault();

  if (!state.pendingInstanceId) {
    setLoginStep('deployment-select');
    setLoginStatus('Choose a deployment to continue.', 'error');
    return;
  }

  try {
    setButtonBusy(elements.deploymentOpenButton, true, 'Open Deployment', 'Opening...');
    setLoginStatus('Validating deployment password...', null);
    await unlockProxySession(elements.deploymentPassword.value);
    await authenticateInstance(state.pendingInstanceId);
  } catch (error) {
    setLoginStatus(error instanceof Error ? error.message : 'Deployment authentication failed.', 'error');
  } finally {
    setButtonBusy(elements.deploymentOpenButton, false, 'Open Deployment', 'Opening...');
  }
});

elements.refreshButton.addEventListener('click', async () => {
  await loadInstances();
});

elements.themeToggleButton.addEventListener('click', toggleTheme);
elements.loginThemeToggleButton.addEventListener('click', toggleTheme);

elements.openDispatchButton.addEventListener('click', () => {
  resetDispatchForm();
  openModal(elements.dispatchModal);
});

elements.openFleetModalButton.addEventListener('click', () => {
  state.selectedFleetId = null;
  state.editingFleet = null;
  resetFleetForm();
  openFleetModal();
});

elements.openVesselModalButton.addEventListener('click', () => {
  state.selectedVesselId = null;
  state.editingVessel = null;
  resetVesselForm();
  openVesselModal();
});

elements.openPlaybookModalButton.addEventListener('click', () => {
  state.selectedPlaybookId = null;
  state.editingPlaybook = null;
  resetPlaybookForm();
  openPlaybookModal();
});

elements.openBacklogModalButton.addEventListener('click', () => {
  state.selectedObjectiveId = null;
  state.editingObjective = null;
  resetBacklogForm();
  openBacklogModal();
});

elements.openPlanningModalButton.addEventListener('click', () => {
  resetPlanningForm();
  openPlanningModal();
});

elements.fleetForm.addEventListener('submit', submitFleetForm);
elements.fleetResetButton.addEventListener('click', resetFleetForm);
elements.vesselForm.addEventListener('submit', submitVesselForm);
elements.vesselResetButton.addEventListener('click', resetVesselForm);
elements.playbookForm.addEventListener('submit', submitPlaybookForm);
elements.playbookResetButton.addEventListener('click', resetPlaybookForm);
elements.playbookDeleteButton.addEventListener('click', deleteSelectedPlaybook);
elements.backlogForm.addEventListener('submit', submitBacklogForm);
elements.backlogResetButton.addEventListener('click', resetBacklogForm);
elements.backlogDeleteButton.addEventListener('click', deleteSelectedBacklog);
elements.planningForm.addEventListener('submit', submitPlanningForm);
elements.planningResetButton.addEventListener('click', resetPlanningForm);
elements.resourceEditorForm.addEventListener('submit', submitResourceEditorForm);
elements.resourceEditorResetButton.addEventListener('click', resetResourceEditor);
elements.resourceEditorDeleteButton.addEventListener('click', deleteResourceFromEditor);
elements.dispatchForm.addEventListener('submit', submitDispatchForm);
elements.missionForm.addEventListener('submit', submitMissionForm);
elements.missionResetButton.addEventListener('click', resetMissionForm);
elements.missionBrowseForm.addEventListener('submit', submitMissionBrowseForm);
elements.missionBrowseRecentButton.addEventListener('click', loadRecentMissionList);
elements.voyageBrowseForm.addEventListener('submit', submitVoyageBrowseForm);
elements.voyageBrowseRecentButton.addEventListener('click', loadRecentVoyageList);
elements.backlogBrowseForm.addEventListener('submit', submitBacklogBrowseForm);
elements.backlogBrowseRecentButton.addEventListener('click', () => { void loadRecentBacklogList(); });
elements.refreshBacklogButton.addEventListener('click', () => { void loadRecentBacklogList(); });
elements.planningBrowseForm.addEventListener('submit', submitPlanningBrowseForm);
elements.planningBrowseRecentButton.addEventListener('click', async () => {
  await loadRecentPlanningList();
  if (state.selectedPlanningSessionId) {
    await selectPlanningSession(state.selectedPlanningSessionId, { scroll: false });
  }
});
elements.refreshPlanningButton.addEventListener('click', async () => {
  await loadRecentPlanningList();
  if (state.selectedPlanningSessionId) {
    await selectPlanningSession(state.selectedPlanningSessionId, { scroll: false });
  }
});
elements.planningWorkspace.addEventListener('click', async (event) => {
  const button = event.target.closest('[data-planning-action]');
  if (!button) return;

  const action = button.getAttribute('data-planning-action');
  if (!action) return;

  if (action === 'select-message') {
    const messageId = button.getAttribute('data-message-id') || '';
    state.selectedPlanningMessageId = messageId;
    clearPlanningWorkspaceStatus();
    renderPlanningWorkspace();
    return;
  }

  if (action === 'refresh') {
    if (state.selectedPlanningSessionId) {
      await selectPlanningSession(state.selectedPlanningSessionId, { scroll: false });
    }
    return;
  }

  if (action === 'open-objective') {
    const objective = getLinkedObjectiveForPlanningSession(state.selectedPlanningSessionId);
    if (objective?.id) {
      await openBacklogDetailModal(objective.id);
    }
    return;
  }

  if (action === 'stop') {
    await stopPlanningWorkspace();
    return;
  }

  if (action === 'delete') {
    await deletePlanningWorkspace();
    return;
  }

  if (action === 'summarize') {
    await summarizePlanningWorkspace();
    return;
  }

  if (action === 'open-dispatch') {
    openDispatchFromPlanningWorkspace();
    return;
  }

  if (action === 'dispatch') {
    await dispatchPlanningWorkspace();
  }
});
elements.planningWorkspace.addEventListener('submit', async (event) => {
  const form = event.target.closest('[data-planning-form]');
  if (!form) return;

  if (form.getAttribute('data-planning-form') === 'composer') {
    event.preventDefault();
    await sendPlanningWorkspaceMessage();
  }
});
elements.planningWorkspace.addEventListener('input', (event) => {
  const field = event.target.closest('[data-planning-field]');
  if (!field) return;

  const value = field.value || '';
  const fieldKey = field.getAttribute('data-planning-field');
  if (fieldKey === 'composer') {
    state.planningComposer = value;
    return;
  }
  if (fieldKey === 'draft-title') {
    state.planningDraftTitle = value;
    return;
  }
  if (fieldKey === 'draft-description') {
    state.planningDraftDescription = value;
  }
});
elements.refreshDeliveryButton.addEventListener('click', () => { void loadDeliveryResources(); });
elements.requestHistoryForm.addEventListener('submit', async (event) => { event.preventDefault(); await loadRequestHistoryData(false); });
elements.requestHistoryRecentButton.addEventListener('click', () => { void loadRequestHistoryData(false); });
elements.captainToolsLoadButton.addEventListener('click', async () => {
  const captainId = elements.captainToolsCaptainId.value.trim();
  if (!captainId) {
    renderCaptainToolsSummary();
    return;
  }
  await openCaptainToolsDetailModal(captainId);
});
elements.apiExplorerUsePresetButton.addEventListener('click', () => {
  applyApiExplorerPreset();
});
elements.apiExplorerForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (!state.selectedInstanceId) return;

  const method = elements.apiExplorerMethod.value || 'GET';
  const path = elements.apiExplorerPath.value.trim();
  if (!path) {
    setFormStatus(elements.apiExplorerStatusText, 'Enter an instance-relative path such as /backlog.', 'error');
    return;
  }

  let body = undefined;
  if (method !== 'GET' && method !== 'DELETE') {
    try {
      body = parseJsonPayload(elements.apiExplorerBody.value || '{}', 'API Explorer body is not valid JSON.');
    } catch (error) {
      setFormStatus(elements.apiExplorerStatusText, error instanceof Error ? error.message : 'Invalid API Explorer JSON.', 'error');
      return;
    }
  }

  try {
    setButtonBusy(elements.apiExplorerSendButton, true, 'Send Request', 'Sending...');
    const response = await fetchJson(`${instanceBaseUrl()}${path.startsWith('/') ? path : `/${path}`}`, {
      method,
      body,
    });
    elements.apiExplorerResponse.textContent = safeJsonStringify(response);
    setFormStatus(elements.apiExplorerStatusText, `${method} ${path} succeeded.`, 'success');
  } catch (error) {
    elements.apiExplorerResponse.textContent = error instanceof Error ? error.message : 'API Explorer request failed.';
    setFormStatus(elements.apiExplorerStatusText, error instanceof Error ? error.message : 'API Explorer request failed.', 'error');
  } finally {
    setButtonBusy(elements.apiExplorerSendButton, false, 'Send Request', 'Sending...');
  }
});
elements.refreshReferenceButton.addEventListener('click', () => { void loadReferenceData(); });
elements.workspaceLoadStatusButton.addEventListener('click', () => { void loadWorkspaceStatusView(); });
elements.workspaceLoadTreeButton.addEventListener('click', () => { void loadWorkspaceTreeView(); });
elements.workspaceSearchButton.addEventListener('click', () => { void loadWorkspaceSearchView(); });
elements.workspaceLoadChangesButton.addEventListener('click', () => { void loadWorkspaceChangesView(); });
elements.dispatchPlaybookList.addEventListener('change', (event) => {
  const toggle = event.target.closest('[data-playbook-toggle]');
  if (toggle) {
    const playbookId = toggle.value;
    const checked = Boolean(toggle.checked);
    const existing = getDispatchPlaybookSelection(playbookId);

    if (checked && !existing) {
      state.dispatchSelectedPlaybooks = [
        ...(state.dispatchSelectedPlaybooks || []),
        { playbookId, deliveryMode: 'InlineFullContent' },
      ];
    } else if (!checked && existing) {
      state.dispatchSelectedPlaybooks = (state.dispatchSelectedPlaybooks || [])
        .filter((selection) => selection.playbookId !== playbookId);
    }

    renderDispatchPlaybookSelection();
    return;
  }

  const mode = event.target.closest('[data-playbook-mode]');
  if (!mode) return;

  const playbookId = mode.getAttribute('data-playbook-id');
  if (!playbookId) return;

  state.dispatchSelectedPlaybooks = (state.dispatchSelectedPlaybooks || []).map((selection) => (
    selection.playbookId === playbookId
      ? { ...selection, deliveryMode: mode.value || 'InlineFullContent' }
      : selection
  ));
  renderDispatchPlaybookSelection();
});

document.addEventListener('click', async (event) => {
  const createButton = event.target.closest('[data-resource-create]');
  if (createButton) {
    const resourceKey = createButton.getAttribute('data-resource-create');
    const definition = resourceKey ? RESOURCE_DEFINITIONS[resourceKey] : null;
    if (definition && !definition.readOnly && definition.createSupported !== false) {
      openResourceEditor(resourceKey, 'create', definition.createTemplate ? definition.createTemplate() : {}, null);
    }
    return;
  }

  const refreshButton = event.target.closest('[data-resource-refresh]');
  if (refreshButton) {
    const resourceKey = refreshButton.getAttribute('data-resource-refresh');
    if (resourceKey) {
      if (resourceKey === 'pipeline' || resourceKey === 'persona' || resourceKey === 'promptTemplate') {
        await loadReferenceData();
      } else {
        await loadResourceCollection(resourceKey);
      }
    }
  }
});

elements.switchDeploymentButton.addEventListener('click', async () => {
  await revokeProxySession();
  logoutToLogin('');
});

elements.sidebarSwitchDeploymentButton.addEventListener('click', async () => {
  await revokeProxySession();
  logoutToLogin('');
});

elements.mobileMenuButton.addEventListener('click', () => {
  if (state.sidebarOpen) closeSidebar();
  else openSidebar();
});

elements.sidebarOverlay.addEventListener('click', closeSidebar);

document.querySelectorAll('[data-close-modal]').forEach((button) => {
  button.addEventListener('click', () => {
    closeModalById(button.getAttribute('data-close-modal'));
  });
});

elements.detailModalBody.addEventListener('click', async (event) => {
  const tabButton = event.target.closest('[data-detail-tab]');
  if (tabButton && state.detailModal?.type === 'mission') {
    state.detailModal.tab = tabButton.getAttribute('data-detail-tab') || 'overview';
    renderDetailModal();
    return;
  }

  const actionButton = event.target.closest('[data-detail-action]');
  if (!actionButton || !state.selectedInstanceId) return;

  const action = actionButton.getAttribute('data-detail-action');
  const id = actionButton.getAttribute('data-id');
  const resourceKey = actionButton.getAttribute('data-resource-key') || '';
  if (!action || !id) return;
  await performDetailAction(action, id, resourceKey);
});

window.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') return;
  if (isAnyModalOpen()) {
    closeAllModals();
    return;
  }
  if (state.sidebarOpen) closeSidebar();
});

bindSidebarNavigation();
applyTheme(getPreferredTheme());
renderSessionState();
initializeProxyShell().catch(() => {
  renderSessionState();
  elements.instanceList.innerHTML = '';
  setLoginStatus('Unable to initialize the proxy shell.', 'error');
});
