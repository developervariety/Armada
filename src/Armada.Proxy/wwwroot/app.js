const LOGIN_PURPOSE = 'proxy-browser-login';
const LOGIN_SUBJECT = 'proxy';

const state = {
  context: null,
  instances: [],
  busy: false,
};

const elements = {
  loginCard: document.getElementById('loginCard'),
  portalCard: document.getElementById('portalCard'),
  loginForm: document.getElementById('loginForm'),
  passwordInput: document.getElementById('passwordInput'),
  loginButton: document.getElementById('loginButton'),
  loginStatus: document.getElementById('loginStatus'),
  refreshButton: document.getElementById('refreshButton'),
  instanceList: document.getElementById('instanceList'),
  selectedInstanceId: document.getElementById('selectedInstanceId'),
  selectedInstanceMeta: document.getElementById('selectedInstanceMeta'),
  openDashboardButton: document.getElementById('openDashboardButton'),
  clearSelectionButton: document.getElementById('clearSelectionButton'),
  logoutButton: document.getElementById('logoutButton'),
  portalStatus: document.getElementById('portalStatus'),
};

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

function setStatus(element, message, kind = null) {
  element.textContent = message || '';
  element.className = `form-status${kind ? ` ${kind}` : ''}`;
}

function escapeHtml(value) {
  return String(value ?? '')
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

function formatState(state) {
  const value = String(state || 'offline').toLowerCase();
  return value.charAt(0).toUpperCase() + value.slice(1);
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, {
    method: options.method || 'GET',
    headers: {
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...(options.headers || {}),
    },
    body: options.body ? JSON.stringify(options.body) : undefined,
    credentials: 'same-origin',
  });

  const contentType = response.headers.get('content-type') || '';
  const payload = contentType.includes('application/json')
    ? await response.json()
    : await response.text();

  if (!response.ok) {
    const message = typeof payload === 'string'
      ? payload
      : payload?.error || payload?.message || `${response.status}`;
    throw new Error(message);
  }

  return payload;
}

async function computeBrowserLoginProof(password, nonce) {
  const passwordHash = await sha256Hex(String(password || '').trim());
  return sha256Hex(`${LOGIN_PURPOSE}:${LOGIN_SUBJECT}:${String(nonce || '').trim().toLowerCase()}:${passwordHash}`);
}

async function loginToProxy(password) {
  const challenge = await fetchJson('/proxy-api/v1/auth/challenge');
  const nonce = challenge?.nonce || '';
  if (!nonce) {
    throw new Error('Proxy did not return a login challenge.');
  }

  const proofSha256 = await computeBrowserLoginProof(password, nonce);
  await fetchJson('/proxy-api/v1/auth/login', {
    method: 'POST',
    body: { nonce, proofSha256 },
  });
}

async function loadSessionContext() {
  state.context = await fetchJson('/proxy-api/v1/session/context');
  return state.context;
}

async function loadInstances() {
  const data = await fetchJson('/proxy-api/v1/instances');
  state.instances = Array.isArray(data?.instances) ? data.instances : [];
  return state.instances;
}

async function refreshPortal() {
  if (!state.context) {
    return;
  }

  await Promise.all([loadSessionContext(), loadInstances()]);
  render();
}

function renderInstances() {
  if (!state.instances.length) {
    elements.instanceList.innerHTML = '<div class="selection-meta">No connected deployments are currently available from this proxy.</div>';
    return;
  }

  const selectedInstanceId = state.context?.selectedInstanceId || '';
  elements.instanceList.innerHTML = state.instances.map((instance) => {
    const stateClass = String(instance.state || 'offline').toLowerCase();
    const selectedClass = instance.instanceId === selectedInstanceId ? ' selected' : '';
    return `
      <button type="button" class="instance-card${selectedClass}" data-instance-id="${escapeHtml(instance.instanceId)}">
        <div class="instance-row">
          <div class="instance-id">${escapeHtml(instance.instanceId)}</div>
          <span class="state-pill ${escapeHtml(stateClass)}">${escapeHtml(formatState(instance.state))}</span>
        </div>
        <div class="instance-meta">
          Armada ${escapeHtml(instance.armadaVersion || '?')} · Last seen ${escapeHtml(formatTimestamp(instance.lastSeenUtc))}
        </div>
      </button>
    `;
  }).join('');
}

function renderSelection() {
  const selectedInstance = state.context?.selectedInstance;
  const selectedInstanceId = state.context?.selectedInstanceId || '';
  const instanceIsAvailable = !!selectedInstance;

  elements.selectedInstanceId.textContent = selectedInstanceId || 'None selected';

  if (!selectedInstanceId) {
    elements.selectedInstanceMeta.textContent = 'Choose a connected deployment to continue.';
  } else if (!instanceIsAvailable) {
    elements.selectedInstanceMeta.textContent = 'The selected deployment is no longer connected to this proxy.';
  } else {
    elements.selectedInstanceMeta.textContent = `State: ${formatState(selectedInstance.state)} · Armada ${selectedInstance.armadaVersion || '?'} · Last seen ${formatTimestamp(selectedInstance.lastSeenUtc)}`;
  }

  elements.openDashboardButton.disabled = !selectedInstanceId;
  elements.clearSelectionButton.disabled = !selectedInstanceId;
}

function render() {
  const authenticated = !!state.context?.isAuthenticated;
  elements.loginCard.classList.toggle('hidden', authenticated);
  elements.portalCard.classList.toggle('hidden', !authenticated);

  if (!authenticated) {
    setStatus(elements.loginStatus, '', null);
    return;
  }

  renderInstances();
  renderSelection();
}

async function bootstrap() {
  try {
    await loadSessionContext();
    await loadInstances();
    setStatus(elements.portalStatus, '', null);
  } catch (error) {
    state.context = null;
    state.instances = [];
    if (error instanceof Error) {
      setStatus(elements.loginStatus, '', null);
      setStatus(elements.portalStatus, '', null);
    }
  }

  render();
}

elements.loginForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  const password = elements.passwordInput.value;
  if (!password) return;

  setStatus(elements.loginStatus, 'Unlocking proxy...', null);
  elements.loginButton.disabled = true;

  try {
    await loginToProxy(password);
    elements.passwordInput.value = '';
    await refreshPortalAfterLogin();
    setStatus(elements.portalStatus, 'Proxy unlocked. Select a deployment to continue.', 'success');
  } catch (error) {
    setStatus(elements.loginStatus, error instanceof Error ? error.message : 'Proxy login failed.', 'error');
  } finally {
    elements.loginButton.disabled = false;
  }
});

async function refreshPortalAfterLogin() {
  await loadSessionContext();
  await loadInstances();
  render();
}

elements.refreshButton.addEventListener('click', async () => {
  elements.refreshButton.disabled = true;
  setStatus(elements.portalStatus, 'Refreshing connected deployments...', null);

  try {
    await refreshPortal();
    setStatus(elements.portalStatus, 'Deployment list refreshed.', 'success');
  } catch (error) {
    setStatus(elements.portalStatus, error instanceof Error ? error.message : 'Unable to refresh deployments.', 'error');
  } finally {
    elements.refreshButton.disabled = false;
  }
});

elements.instanceList.addEventListener('click', async (event) => {
  const button = event.target.closest('[data-instance-id]');
  if (!button) return;

  const instanceId = button.getAttribute('data-instance-id');
  if (!instanceId) return;

  setStatus(elements.portalStatus, `Selecting ${instanceId}...`, null);

  try {
    state.context = await fetchJson('/proxy-api/v1/session/instance', {
      method: 'POST',
      body: { instanceId },
    });
    render();
    setStatus(elements.portalStatus, `${instanceId} selected.`, 'success');
  } catch (error) {
    setStatus(elements.portalStatus, error instanceof Error ? error.message : 'Unable to select deployment.', 'error');
  }
});

elements.clearSelectionButton.addEventListener('click', async () => {
  setStatus(elements.portalStatus, 'Clearing selection...', null);

  try {
    state.context = await fetchJson('/proxy-api/v1/session/logout-instance', { method: 'POST' });
    render();
    setStatus(elements.portalStatus, 'Selection cleared.', 'success');
  } catch (error) {
    setStatus(elements.portalStatus, error instanceof Error ? error.message : 'Unable to clear selection.', 'error');
  }
});

elements.logoutButton.addEventListener('click', async () => {
  elements.logoutButton.disabled = true;

  try {
    await fetchJson('/proxy-api/v1/auth/logout', { method: 'POST' });
  } catch {
    // Best effort.
  } finally {
    state.context = null;
    state.instances = [];
    render();
    setStatus(elements.loginStatus, '', null);
    setStatus(elements.portalStatus, '', null);
    elements.logoutButton.disabled = false;
  }
});

elements.openDashboardButton.addEventListener('click', () => {
  if (!state.context?.selectedInstanceId) {
    setStatus(elements.portalStatus, 'Select a deployment before opening the dashboard.', 'error');
    return;
  }

  window.location.href = '/dashboard';
});

bootstrap().catch((error) => {
  setStatus(elements.loginStatus, error instanceof Error ? error.message : 'Unable to initialize the proxy portal.', 'error');
});
