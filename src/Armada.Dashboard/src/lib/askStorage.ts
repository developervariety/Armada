// Browser-local Ask Armada state: one unsent draft per saved conversation, the conversation last open, and the
// prompts sent from this browser. Every read tolerates a missing, corrupt or unavailable store.

/** localStorage key prefix of the unsent drafts; the signed-in user id completes the key. */
export const ASK_DRAFTS_STORAGE_KEY = 'armada.ask.drafts';
/** localStorage key prefix of the conversation last open; the signed-in user id completes the key. */
export const ASK_LAST_THREAD_STORAGE_KEY = 'armada.ask.lastThread';
/** localStorage key of the prompts sent from this browser, oldest first. */
export const ASK_PROMPT_HISTORY_KEY = 'armada.ask.promptHistory';
/** The most prompts kept in the history. */
export const ASK_PROMPT_HISTORY_LIMIT = 100;
/** The most drafts kept per user. The oldest drafts go first. */
export const ASK_DRAFT_LIMIT = 50;

function userKey(prefix: string, userId: string): string {
  return `${prefix}:${userId || 'anonymous'}`;
}

function readDrafts(userId: string): Record<string, string> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(userKey(ASK_DRAFTS_STORAGE_KEY, userId)) ?? '{}');
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
    const drafts: Record<string, string> = {};
    for (const [threadId, text] of Object.entries(parsed)) if (typeof text === 'string' && text.length > 0) drafts[threadId] = text;
    return drafts;
  } catch { return {}; }
}

/** The unsent draft of a saved conversation, or an empty string. */
export function loadAskDraft(userId: string, threadId: string): string {
  return readDrafts(userId)[threadId] ?? '';
}

/** Keeps the unsent draft of a saved conversation. Empty text removes the draft. */
export function saveAskDraft(userId: string, threadId: string, text: string): void {
  if (!threadId) return;
  const drafts = readDrafts(userId);
  delete drafts[threadId];
  if (text.length > 0) drafts[threadId] = text;
  const kept = Object.entries(drafts).slice(-ASK_DRAFT_LIMIT);
  try {
    localStorage.setItem(userKey(ASK_DRAFTS_STORAGE_KEY, userId), JSON.stringify(Object.fromEntries(kept)));
  } catch { /* storage is full or unavailable; a draft is a convenience */ }
}

/** The id of the conversation last open, or an empty string. */
export function loadLastAskThread(userId: string): string {
  try { return localStorage.getItem(userKey(ASK_LAST_THREAD_STORAGE_KEY, userId)) ?? ''; } catch { return ''; }
}

/** Remembers the conversation last open. */
export function saveLastAskThread(userId: string, threadId: string): void {
  try {
    if (threadId) localStorage.setItem(userKey(ASK_LAST_THREAD_STORAGE_KEY, userId), threadId);
    else localStorage.removeItem(userKey(ASK_LAST_THREAD_STORAGE_KEY, userId));
  } catch { /* storage is unavailable */ }
}

/** The prompts sent from this browser, oldest first. */
export function loadPromptHistory(): string[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(ASK_PROMPT_HISTORY_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((value): value is string => typeof value === 'string').slice(-ASK_PROMPT_HISTORY_LIMIT) : [];
  } catch { return []; }
}

/** Adds a sent prompt to the history, skipping blank text and a repeat of the newest entry. */
export function recordPrompt(text: string): void {
  if (!text.trim()) return;
  const history = loadPromptHistory();
  if (history[history.length - 1] === text) return;
  history.push(text);
  try {
    localStorage.setItem(ASK_PROMPT_HISTORY_KEY, JSON.stringify(history.slice(-ASK_PROMPT_HISTORY_LIMIT)));
  } catch { /* storage is full or unavailable; history is a convenience */ }
}
