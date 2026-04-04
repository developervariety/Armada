const DEPLOYMENT_STORAGE_KEY = 'armada_proxy_instance_id';

const state = {
  instances: [],
  selectedInstanceId: null,
  isAuthenticated: false,
  sidebarOpen: false,
  summary: null,
  detail: null,
  fleets: [],
  vessels: [],
  selectedFleetId: null,
  selectedVesselId: null,
  selectedMissionId: null,
};

const elements = {
  loginView: document.getElementById('loginView'),
  appView: document.getElementById('appView'),
  loginForm: document.getElementById('loginForm'),
  loginInstanceId: document.getElementById('loginInstanceId'),
  loginStatus: document.getElementById('loginStatus'),
  loginRefreshButton: document.getElementById('loginRefreshButton'),
  instanceCount: document.getElementById('instanceCount'),
  instanceList: document.getElementById('instanceList'),
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
  refreshSummaryButton: document.getElementById('refreshSummaryButton'),
  emptyState: document.getElementById('emptyState'),
  instanceWorkspace: document.getElementById('instanceWorkspace'),
  summaryTitle: document.getElementById('summaryTitle'),
  summarySubtitle: document.getElementById('summarySubtitle'),
  summaryCards: document.getElementById('summaryCards'),
  activityFeed: document.getElementById('activityFeed'),
  missionList: document.getElementById('missionList'),
  voyageList: document.getElementById('voyageList'),
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
  captainList: document.getElementById('captainList'),
  fleetList: document.getElementById('fleetList'),
  vesselList: document.getElementById('vesselList'),
  detailTitle: document.getElementById('detailTitle'),
  detailSubtitle: document.getElementById('detailSubtitle'),
  detailBody: document.getElementById('detailBody'),
  entityCardTemplate: document.getElementById('entityCardTemplate'),
  fleetForm: document.getElementById('fleetForm'),
  fleetName: document.getElementById('fleetName'),
  fleetDescription: document.getElementById('fleetDescription'),
  fleetDefaultPipelineId: document.getElementById('fleetDefaultPipelineId'),
  fleetActive: document.getElementById('fleetActive'),
  fleetResetButton: document.getElementById('fleetResetButton'),
  fleetFormStatus: document.getElementById('fleetFormStatus'),
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
  dispatchForm: document.getElementById('dispatchForm'),
  dispatchVesselId: document.getElementById('dispatchVesselId'),
  dispatchTitle: document.getElementById('dispatchTitle'),
  dispatchDescription: document.getElementById('dispatchDescription'),
  dispatchPipelineId: document.getElementById('dispatchPipelineId'),
  dispatchPipeline: document.getElementById('dispatchPipeline'),
  dispatchMissions: document.getElementById('dispatchMissions'),
  dispatchFormStatus: document.getElementById('dispatchFormStatus'),
  missionForm: document.getElementById('missionForm'),
  missionTitle: document.getElementById('missionTitle'),
  missionDescription: document.getElementById('missionDescription'),
  missionVesselId: document.getElementById('missionVesselId'),
  missionVoyageId: document.getElementById('missionVoyageId'),
  missionPersona: document.getElementById('missionPersona'),
  missionPriority: document.getElementById('missionPriority'),
  missionResetButton: document.getElementById('missionResetButton'),
  missionFormStatus: document.getElementById('missionFormStatus'),
};

function escapeHtml(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function formatTimestamp(value) {
  if (!value) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '-';
  return date.toLocaleString();
}

function badgeClass(value) {
  return String(value || '').toLowerCase().replace(/[^a-z0-9]+/g, '');
}

function renderBadge(text) {
  const value = String(text || 'unknown');
  return `<span class="badge ${badgeClass(value)}">${escapeHtml(value)}</span>`;
}

function setFormStatus(element, message, kind) {
  if (!element) return;
  element.textContent = message || '';
  element.className = 'form-status';
  if (kind) element.classList.add(kind);
}

async function fetchJson(url, options = {}) {
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
    // Ignore storage failures in private/incognito contexts.
  }
}

function getInstanceById(instanceId) {
  return state.instances.find((instance) => instance.instanceId === instanceId) || null;
}

function setLoginStatus(message, kind) {
  setFormStatus(elements.loginStatus, message, kind);
}

function setDeploymentChrome() {
  const instance = getInstanceById(state.selectedInstanceId);
  const summaryHealth = state.summary?.health || {};
  const tunnel = summaryHealth.remoteTunnel || {};
  const statusValue = tunnel.state || instance?.state || 'Offline';

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
  }
}

function resetProxyState() {
  state.summary = null;
  state.detail = null;
  state.fleets = [];
  state.vessels = [];
  resetFleetForm();
  resetVesselForm();
  resetMissionForm();
  renderDetail();
}

async function authenticateInstance(instanceId) {
  const normalized = String(instanceId || '').trim();
  if (!normalized) {
    setLoginStatus('Deployment identifier is required.', 'error');
    return;
  }

  const instance = getInstanceById(normalized);
  if (!instance) {
    setLoginStatus(`No Armada deployment with identifier "${normalized}" is connected to this proxy.`, 'error');
    return;
  }

  state.selectedInstanceId = normalized;
  state.isAuthenticated = true;
  storeDeploymentId(normalized);
  elements.loginInstanceId.value = normalized;
  setLoginStatus('', null);
  resetProxyState();
  renderSessionState();
  await loadSelectedInstance();
}

function logoutToLogin(message = '', prefill = '') {
  state.isAuthenticated = false;
  state.selectedInstanceId = null;
  storeDeploymentId(null);
  resetProxyState();
  renderSessionState();
  elements.loginInstanceId.value = prefill || '';
  if (message) {
    setLoginStatus(message, 'error');
  } else {
    setLoginStatus('', null);
  }
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
  state.instances = data.instances || [];
  elements.instanceCount.textContent = String(data.count || state.instances.length || 0);
  renderInstanceList();

  if (state.isAuthenticated && state.selectedInstanceId) {
    if (!getInstanceById(state.selectedInstanceId)) {
      const missingId = state.selectedInstanceId;
      logoutToLogin(`Deployment "${missingId}" is no longer registered with this proxy.`, missingId);
      return;
    }

    await loadSelectedInstance();
  } else {
    renderSessionState();
  }
}

async function loadSelectedInstance() {
  if (!state.selectedInstanceId) return;

  try {
    const base = instanceBaseUrl();
    const [summary, fleets, vessels] = await Promise.all([
      fetchJson(`${base}/summary`),
      fetchJson(`${base}/fleets?limit=12`),
      fetchJson(`${base}/vessels?limit=12`),
    ]);

    state.summary = summary;
    state.fleets = fleets.fleets || [];
    state.vessels = vessels.vessels || [];
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

function renderInstanceList() {
  elements.instanceList.innerHTML = '';

  if (state.instances.length === 0) {
    elements.instanceList.innerHTML = '<div class="text-muted">No Armada deployments are connected to this proxy yet.</div>';
    return;
  }

  for (const instance of state.instances) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'deployment-card';
    button.innerHTML = `
      <div class="entity-card-top">
        <span class="entity-title">${escapeHtml(instance.instanceId)}</span>
        ${renderBadge(instance.state)}
      </div>
      <div class="entity-meta">${escapeHtml(instance.armadaVersion || 'unknown version')} · ${escapeHtml(instance.protocolVersion || 'unknown protocol')}</div>
      <div class="entity-meta-secondary">${escapeHtml(instance.lastError || instance.remoteAddress || 'No current error')}</div>
    `;

    button.addEventListener('click', async () => {
      elements.loginInstanceId.value = instance.instanceId;
      await authenticateInstance(instance.instanceId);
    });

    if (state.selectedInstanceId === instance.instanceId) {
      button.classList.add('is-selected');
    }

    elements.instanceList.appendChild(button);
  }
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
  elements.summarySubtitle.textContent = `${health.version || 'unknown version'} · tunnel ${health.remoteTunnel?.state || 'unknown'} · generated ${formatTimestamp(summary.generatedUtc)}`;
  setDeploymentChrome();

  elements.summaryCards.innerHTML = [
    makeSummaryCard('Tunnel', health.remoteTunnel?.state || 'unknown', health.remoteTunnel?.tunnelUrl || 'No tunnel URL configured'),
    makeSummaryCard('Latency', health.remoteTunnel?.latencyMs != null ? `${health.remoteTunnel.latencyMs} ms` : '-', `Last heartbeat ${formatTimestamp(health.remoteTunnel?.lastHeartbeatUtc)}`),
    makeSummaryCard('Active Voyages', status.activeVoyages ?? 0, `Working captains ${status.workingCaptains ?? 0}`),
    makeSummaryCard('Mission States', countMissionStates(status.missionsByStatus || {}), 'Snapshot from local Admiral'),
  ].join('');

  renderActivity(summary.recentActivity || []);
  loadRecentMissionList();
  loadRecentVoyageList();
  renderEntityList(elements.captainList, summary.recentCaptains || [], 'captain');
  renderEntityList(elements.fleetList, state.fleets || [], 'fleet');
  renderEntityList(elements.vesselList, state.vessels || [], 'vessel');
  renderDetail();
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

function makeSummaryCard(label, value, detail) {
  return `
    <article class="summary-card">
      <div class="summary-label">${escapeHtml(label)}</div>
      <div class="summary-value">${escapeHtml(String(value))}</div>
      <div class="summary-detail">${escapeHtml(detail)}</div>
    </article>
  `;
}

function countMissionStates(states) {
  const entries = Object.entries(states);
  if (entries.length === 0) return 'none';
  return entries.map(([key, value]) => `${key}: ${value}`).join(' · ');
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
      <div class="feed-meta">${formatTimestamp(item.createdUtc)} · ${escapeHtml(item.entityType || 'system')} ${escapeHtml(item.entityId || '')}</div>
    `;
    elements.activityFeed.appendChild(node);
  }
}

function renderEntityList(container, rows, kind) {
  container.innerHTML = '';
  if (!rows || rows.length === 0) {
    container.innerHTML = '<div class="text-muted">Nothing recent to show.</div>';
    return;
  }

  for (const row of rows) {
    const fragment = elements.entityCardTemplate.content.cloneNode(true);
    const card = fragment.querySelector('.entity-card');
    const title = fragment.querySelector('.entity-title');
    const badge = fragment.querySelector('.badge');
    const meta = fragment.querySelector('.entity-meta');
    const secondary = fragment.querySelector('.entity-meta-secondary');
    const titleValue = row.title || row.name || row.id;
    const badgeValue = row.status || row.state || row.persona || 'detail';

    title.textContent = titleValue;
    badge.textContent = String(badgeValue);
    badge.classList.add(badgeClass(badgeValue));

    if (kind === 'mission') {
      meta.textContent = `${row.persona || 'Worker'} · ${row.id}`;
      secondary.textContent = `Updated ${formatTimestamp(row.lastUpdateUtc)}`;
    } else if (kind === 'captain') {
      meta.textContent = `${row.runtime || 'runtime'} · ${row.id}`;
      secondary.textContent = `Heartbeat ${formatTimestamp(row.lastHeartbeatUtc || row.lastUpdateUtc)}`;
    } else if (kind === 'voyage') {
      meta.textContent = row.id;
      secondary.textContent = `Updated ${formatTimestamp(row.lastUpdateUtc)}`;
    } else if (kind === 'fleet') {
      meta.textContent = row.id;
      secondary.textContent = row.description || 'No description';
    } else if (kind === 'vessel') {
      meta.textContent = row.repoUrl || row.id;
      secondary.textContent = row.workingDirectory || 'No working directory';
    }

    card.addEventListener('click', async () => {
      await loadDetail(kind, row.id);
    });

    container.appendChild(fragment);
  }
}

async function loadDetail(kind, id) {
  if (!state.selectedInstanceId) return;

  const base = instanceBaseUrl();
  let detail;
  if (kind === 'mission') {
    detail = await fetchJson(`${base}/missions/${encodeURIComponent(id)}`);
  } else if (kind === 'voyage') {
    detail = await fetchJson(`${base}/voyages/${encodeURIComponent(id)}`);
  } else if (kind === 'captain') {
    detail = await fetchJson(`${base}/captains/${encodeURIComponent(id)}`);
  } else if (kind === 'fleet') {
    detail = await fetchJson(`${base}/fleets/${encodeURIComponent(id)}`);
  } else if (kind === 'vessel') {
    detail = await fetchJson(`${base}/vessels/${encodeURIComponent(id)}`);
  } else {
    return;
  }

  state.detail = { kind, payload: detail };
  renderDetail();
}

function renderDetail() {
  if (!state.detail) {
    elements.detailTitle.textContent = 'Focused Detail';
    elements.detailSubtitle.textContent = 'Select a mission, voyage, captain, fleet, or vessel card to inspect it here.';
    elements.detailBody.innerHTML = '<div class="detail-empty">No focused entity selected yet.</div>';
    return;
  }

  switch (state.detail.kind) {
    case 'mission':
      renderMissionDetail(state.detail.payload);
      return;
    case 'voyage':
      renderVoyageDetail(state.detail.payload);
      return;
    case 'captain':
      renderCaptainDetail(state.detail.payload);
      return;
    case 'fleet':
      renderFleetDetail(state.detail.payload);
      return;
    case 'vessel':
      renderVesselDetail(state.detail.payload);
      return;
    default:
      elements.detailBody.innerHTML = '<div class="detail-empty">Unsupported detail type.</div>';
  }
}

function renderMissionDetail(payload) {
  const mission = payload.mission || {};
  const captain = payload.captain || {};
  const voyage = payload.voyage || {};
  const vessel = payload.vessel || {};
  const dock = payload.dock || {};

  elements.detailTitle.textContent = mission.title || mission.id || 'Mission Detail';
  elements.detailSubtitle.textContent = `${mission.persona || 'Worker'} · ${mission.id || ''}`;
  elements.detailBody.innerHTML = `
    <div class="detail-grid">
      ${renderKeyValueCard('Mission', [
        ['Status', mission.status],
        ['Branch', mission.branchName],
        ['Captain', captain.name || mission.captainId],
        ['Voyage', voyage.title || mission.voyageId],
        ['Runtime', mission.totalRuntimeMs != null ? `${mission.totalRuntimeMs} ms` : '-'],
      ])}
      ${renderKeyValueCard('Worktree', [
        ['Dock', dock.id || mission.dockId],
        ['Path', dock.worktreePath],
        ['Vessel', vessel.name || mission.vesselId],
        ['Updated', formatTimestamp(mission.lastUpdateUtc)],
        ['Failure', mission.failureReason || '-'],
      ])}
    </div>
    <div class="detail-actions">
      <button class="button" data-detail-action="mission-log" data-id="${escapeHtml(mission.id || '')}">Load Mission Log</button>
      <button class="button" data-detail-action="mission-diff" data-id="${escapeHtml(mission.id || '')}">Load Mission Diff</button>
      <button class="button" data-detail-action="mission-edit" data-id="${escapeHtml(mission.id || '')}">Edit Mission</button>
      <button class="button" data-detail-action="mission-restart" data-id="${escapeHtml(mission.id || '')}">Restart Mission</button>
      <button class="button" data-detail-action="mission-cancel" data-id="${escapeHtml(mission.id || '')}">Cancel Mission</button>
    </div>
    <pre id="detailCodeView" class="code-view">Select a mission action above.</pre>
  `;
  bindDetailActions();
}

function renderVoyageDetail(payload) {
  const voyage = payload.voyage || {};
  const missions = payload.missions || [];

  elements.detailTitle.textContent = voyage.title || voyage.id || 'Voyage Detail';
  elements.detailSubtitle.textContent = `${voyage.status || 'unknown'} · ${voyage.id || ''}`;
  elements.detailBody.innerHTML = `
    <div class="detail-grid">
      ${renderKeyValueCard('Voyage', [
        ['Status', voyage.status],
        ['Created', formatTimestamp(voyage.createdUtc)],
        ['Updated', formatTimestamp(voyage.lastUpdateUtc)],
        ['Completed', formatTimestamp(voyage.completedUtc)],
        ['Missions', missions.length],
      ])}
      ${renderKeyValueCard('Description', [['Text', voyage.description || '-']])}
    </div>
    <div class="detail-actions">
      <button class="button" data-detail-action="voyage-cancel" data-id="${escapeHtml(voyage.id || '')}">Cancel Voyage</button>
    </div>
    <div class="detail-card">
      <h3>Mission Chain</h3>
      ${missions.length === 0 ? '<div class="text-muted">No missions associated with this voyage.</div>' : missions.map((mission) => `
        <div class="detail-row">
          <span class="detail-key">${escapeHtml(mission.title || mission.id)}</span>
          <span class="detail-value">${escapeHtml(String(mission.status || 'unknown'))}</span>
        </div>`).join('')}
    </div>
  `;
  bindDetailActions();
}

function renderCaptainDetail(payload) {
  const captain = payload.captain || {};
  const currentMission = payload.currentMission || {};
  const currentDock = payload.currentDock || {};
  const recentMissions = payload.recentMissions || [];

  elements.detailTitle.textContent = captain.name || captain.id || 'Captain Detail';
  elements.detailSubtitle.textContent = `${captain.runtime || 'runtime'} · ${captain.id || ''}`;
  elements.detailBody.innerHTML = `
    <div class="detail-grid">
      ${renderKeyValueCard('Captain', [
        ['State', captain.state],
        ['Model', captain.model || 'auto'],
        ['Heartbeat', formatTimestamp(captain.lastHeartbeatUtc)],
        ['Current Mission', currentMission.title || captain.currentMissionId],
        ['Current Dock', currentDock.id || captain.currentDockId],
      ])}
      ${renderKeyValueCard('Recent Work', recentMissions.slice(0, 6).map((mission) => [mission.title || mission.id, mission.status || 'unknown']))}
    </div>
    <div class="detail-actions">
      <button class="button" data-detail-action="captain-log" data-id="${escapeHtml(captain.id || '')}">Load Captain Log</button>
      <button class="button" data-detail-action="captain-stop" data-id="${escapeHtml(captain.id || '')}">Stop Captain</button>
    </div>
    <pre id="detailCodeView" class="code-view">Select a captain action above.</pre>
  `;
  bindDetailActions();
}

function renderFleetDetail(payload) {
  const fleet = payload.fleet || {};
  const vessels = payload.vessels || [];

  elements.detailTitle.textContent = fleet.name || fleet.id || 'Fleet Detail';
  elements.detailSubtitle.textContent = `${fleet.id || ''}`;
  elements.detailBody.innerHTML = `
    <div class="detail-grid">
      ${renderKeyValueCard('Fleet', [
        ['Name', fleet.name],
        ['Description', fleet.description || '-'],
        ['Default Pipeline', fleet.defaultPipelineId || '-'],
        ['Active', fleet.active ? 'true' : 'false'],
        ['Updated', formatTimestamp(fleet.lastUpdateUtc)],
      ])}
      ${renderKeyValueCard('Vessels', vessels.slice(0, 8).map((vessel) => [vessel.name || vessel.id, vessel.id || '-']))}
    </div>
    <div class="detail-actions">
      <button class="button" data-detail-action="fleet-edit" data-id="${escapeHtml(fleet.id || '')}">Edit Fleet</button>
    </div>
  `;
  bindDetailActions();
}

function renderVesselDetail(payload) {
  const vessel = payload.vessel || {};
  const recentMissions = payload.recentMissions || [];

  elements.detailTitle.textContent = vessel.name || vessel.id || 'Vessel Detail';
  elements.detailSubtitle.textContent = `${vessel.id || ''}`;
  elements.detailBody.innerHTML = `
    <div class="detail-grid">
      ${renderKeyValueCard('Vessel', [
        ['Fleet', vessel.fleetId || '-'],
        ['Repo', vessel.repoUrl || '-'],
        ['Working Directory', vessel.workingDirectory || '-'],
        ['Default Branch', vessel.defaultBranch || '-'],
        ['Concurrent Missions', vessel.allowConcurrentMissions ? 'true' : 'false'],
      ])}
      ${renderKeyValueCard('Recent Missions', recentMissions.slice(0, 8).map((mission) => [mission.title || mission.id, mission.status || '-']))}
    </div>
    <div class="detail-actions">
      <button class="button" data-detail-action="vessel-edit" data-id="${escapeHtml(vessel.id || '')}">Edit Vessel</button>
    </div>
  `;
  bindDetailActions();
}

function renderKeyValueCard(title, rows) {
  const safeRows = rows && rows.length > 0 ? rows : [['Value', '-']];
  return `
    <section class="detail-card">
      <h3>${escapeHtml(title)}</h3>
      ${safeRows.map(([key, value]) => `
        <div class="detail-row">
          <span class="detail-key">${escapeHtml(String(key))}</span>
          <span class="detail-value mono">${escapeHtml(String(value || '-'))}</span>
        </div>`).join('')}
    </section>
  `;
}

function bindDetailActions() {
  document.querySelectorAll('[data-detail-action]').forEach((button) => {
    button.addEventListener('click', async (event) => {
      const target = event.currentTarget;
      const action = target.getAttribute('data-detail-action');
      const id = target.getAttribute('data-id');
      if (!action || !id || !state.selectedInstanceId) return;
      await performDetailAction(action, id);
    });
  });
}

async function performDetailAction(action, id) {
  const codeView = document.getElementById('detailCodeView');
  const base = instanceBaseUrl();
  const writeCodeView = (message) => {
    if (codeView) codeView.textContent = message;
  };

  try {
    if (action === 'mission-log') {
      writeCodeView('Loading...');
      const data = await fetchJson(`${base}/missions/${encodeURIComponent(id)}/log?lines=200&offset=0`);
      writeCodeView(data.log || 'No log content available.');
      return;
    }

    if (action === 'mission-diff') {
      writeCodeView('Loading...');
      const data = await fetchJson(`${base}/missions/${encodeURIComponent(id)}/diff`);
      writeCodeView(data.diff || 'No diff content available.');
      return;
    }

    if (action === 'captain-log') {
      writeCodeView('Loading...');
      const data = await fetchJson(`${base}/captains/${encodeURIComponent(id)}/log?lines=80&offset=0`);
      writeCodeView(data.log || 'No log content available.');
      return;
    }

    if (action === 'fleet-edit') {
      if (state.detail?.payload?.fleet) populateFleetForm(state.detail.payload.fleet);
      return;
    }

    if (action === 'vessel-edit') {
      if (state.detail?.payload?.vessel) populateVesselForm(state.detail.payload.vessel);
      return;
    }

    if (action === 'mission-edit') {
      if (state.detail?.payload?.mission) populateMissionForm(state.detail.payload.mission);
      return;
    }

    if (action === 'mission-cancel') {
      if (!confirm('Cancel this mission?')) return;
      await fetchJson(`${base}/missions/${encodeURIComponent(id)}`, { method: 'DELETE' });
      writeCodeView('Mission cancelled.');
      await refreshAfterMutation('mission', id);
      return;
    }

    if (action === 'mission-restart') {
      if (!confirm('Restart this mission?')) return;
      await fetchJson(`${base}/missions/${encodeURIComponent(id)}/restart`, { method: 'POST', body: {} });
      writeCodeView('Mission restarted.');
      await refreshAfterMutation('mission', id);
      return;
    }

    if (action === 'voyage-cancel') {
      if (!confirm('Cancel this voyage and its active work?')) return;
      await fetchJson(`${base}/voyages/${encodeURIComponent(id)}`, { method: 'DELETE' });
      await refreshAfterMutation('voyage', id);
      return;
    }

    if (action === 'captain-stop') {
      if (!confirm('Stop this captain?')) return;
      await fetchJson(`${base}/captains/${encodeURIComponent(id)}/stop`, { method: 'POST' });
      writeCodeView('Captain stop requested.');
      await refreshAfterMutation('captain', id);
    }
  } catch (error) {
    writeCodeView(error instanceof Error ? error.message : 'Action failed.');
  }
}

async function refreshAfterMutation(kind, id) {
  await loadSelectedInstance();
  if (!kind || !id) {
    renderDetail();
    return;
  }

  try {
    await loadDetail(kind, id);
  } catch {
    renderDetail();
  }
}

function populateFleetForm(fleet) {
  state.selectedFleetId = fleet.id || null;
  elements.fleetName.value = fleet.name || '';
  elements.fleetDescription.value = fleet.description || '';
  elements.fleetDefaultPipelineId.value = fleet.defaultPipelineId || '';
  elements.fleetActive.checked = fleet.active !== false;
  setFormStatus(elements.fleetFormStatus, `Editing ${fleet.name || fleet.id}`, null);
}

function resetFleetForm() {
  state.selectedFleetId = null;
  elements.fleetForm.reset();
  elements.fleetActive.checked = true;
  setFormStatus(elements.fleetFormStatus, '', null);
}

function populateVesselForm(vessel) {
  state.selectedVesselId = vessel.id || null;
  elements.vesselFleetId.value = vessel.fleetId || '';
  elements.vesselName.value = vessel.name || '';
  elements.vesselRepoUrl.value = vessel.repoUrl || '';
  elements.vesselWorkingDirectory.value = vessel.workingDirectory || '';
  elements.vesselDefaultBranch.value = vessel.defaultBranch || 'main';
  elements.vesselDefaultPipelineId.value = vessel.defaultPipelineId || '';
  elements.vesselAllowConcurrentMissions.checked = Boolean(vessel.allowConcurrentMissions);
  elements.vesselActive.checked = vessel.active !== false;
  setFormStatus(elements.vesselFormStatus, `Editing ${vessel.name || vessel.id}`, null);
}

function resetVesselForm() {
  state.selectedVesselId = null;
  elements.vesselForm.reset();
  elements.vesselDefaultBranch.value = 'main';
  elements.vesselActive.checked = true;
  elements.vesselAllowConcurrentMissions.checked = false;
  setFormStatus(elements.vesselFormStatus, '', null);
}

function populateMissionForm(mission) {
  state.selectedMissionId = mission.id || null;
  elements.missionTitle.value = mission.title || '';
  elements.missionDescription.value = mission.description || '';
  elements.missionVesselId.value = mission.vesselId || '';
  elements.missionVoyageId.value = mission.voyageId || '';
  elements.missionPersona.value = mission.persona || '';
  elements.missionPriority.value = mission.priority != null ? mission.priority : 100;
  setFormStatus(elements.missionFormStatus, `Editing ${mission.title || mission.id}`, null);
}

function resetMissionForm() {
  state.selectedMissionId = null;
  elements.missionForm.reset();
  elements.missionPriority.value = '100';
  setFormStatus(elements.missionFormStatus, '', null);
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
  } catch (error) {
    setFormStatus(elements.vesselFormStatus, error instanceof Error ? error.message : 'Vessel save failed.', 'error');
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
    pipeline: elements.dispatchPipeline.value.trim() || null,
    missions,
  };

  if (!payload.title) {
    setFormStatus(elements.dispatchFormStatus, 'Voyage title is required.', 'error');
    return;
  }

  if (!payload.vesselId) {
    setFormStatus(elements.dispatchFormStatus, 'A vessel id is required for dispatch.', 'error');
    return;
  }

  if (missions.length === 0) {
    setFormStatus(elements.dispatchFormStatus, 'Provide at least one mission line.', 'error');
    return;
  }

  try {
    const base = instanceBaseUrl();
    const voyage = await fetchJson(`${base}/voyages/dispatch`, { method: 'POST', body: payload });
    setFormStatus(elements.dispatchFormStatus, `Voyage dispatched: ${voyage.id || voyage.title || 'created'}`, 'success');
    await loadSelectedInstance();
  } catch (error) {
    setFormStatus(elements.dispatchFormStatus, error instanceof Error ? error.message : 'Dispatch failed.', 'error');
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

    await loadSelectedInstance();
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
    setFormStatus(elements.missionBrowseStatusText, `Loaded ${rows.length} mission${rows.length === 1 ? '' : 's'} from the instance.`, 'success');
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
    setFormStatus(elements.voyageBrowseStatusText, `Loaded ${rows.length} voyage${rows.length === 1 ? '' : 's'} from the instance.`, 'success');
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

  try {
    await loadInstances();

    renderSessionState();
    if (storedDeploymentId) {
      elements.loginInstanceId.value = storedDeploymentId;
      if (!getInstanceById(storedDeploymentId)) {
        setLoginStatus(`Deployment "${storedDeploymentId}" is not currently connected to this proxy.`, 'error');
      }
    }
  } catch (error) {
    renderSessionState();
    setLoginStatus('Unable to load connected deployments right now.', 'error');
  }
}

elements.loginForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  await authenticateInstance(elements.loginInstanceId.value);
});

elements.loginRefreshButton.addEventListener('click', async () => {
  try {
    setLoginStatus('Refreshing proxy registry...', null);
    await loadInstances();
    setLoginStatus('Registry refreshed.', 'success');
  } catch (error) {
    setLoginStatus(error instanceof Error ? error.message : 'Failed to refresh proxy registry.', 'error');
  }
});

elements.refreshButton.addEventListener('click', async () => {
  await loadInstances();
});

elements.refreshSummaryButton.addEventListener('click', async () => {
  await loadSelectedInstance();
});

elements.fleetForm.addEventListener('submit', submitFleetForm);
elements.fleetResetButton.addEventListener('click', resetFleetForm);
elements.vesselForm.addEventListener('submit', submitVesselForm);
elements.vesselResetButton.addEventListener('click', resetVesselForm);
elements.missionBrowseForm.addEventListener('submit', submitMissionBrowseForm);
elements.missionBrowseRecentButton.addEventListener('click', loadRecentMissionList);
elements.voyageBrowseForm.addEventListener('submit', submitVoyageBrowseForm);
elements.voyageBrowseRecentButton.addEventListener('click', loadRecentVoyageList);
elements.dispatchForm.addEventListener('submit', submitDispatchForm);
elements.missionForm.addEventListener('submit', submitMissionForm);
elements.missionResetButton.addEventListener('click', resetMissionForm);

elements.switchDeploymentButton.addEventListener('click', () => {
  logoutToLogin('', state.selectedInstanceId || '');
});

elements.sidebarSwitchDeploymentButton.addEventListener('click', () => {
  logoutToLogin('', state.selectedInstanceId || '');
});

elements.mobileMenuButton.addEventListener('click', () => {
  if (state.sidebarOpen) {
    closeSidebar();
  } else {
    openSidebar();
  }
});

elements.sidebarOverlay.addEventListener('click', closeSidebar);

bindSidebarNavigation();
renderSessionState();
initializeProxyShell().catch((error) => {
  renderSessionState();
  elements.instanceList.innerHTML = '<div class="text-muted">Unable to load connected deployments right now.</div>';
  setLoginStatus('Unable to load connected deployments right now.', 'error');
});
