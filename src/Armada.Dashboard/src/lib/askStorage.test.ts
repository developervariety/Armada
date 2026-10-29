import { beforeEach, describe, expect, it } from 'vitest';
import {
  ASK_DRAFTS_STORAGE_KEY, ASK_DRAFT_LIMIT, ASK_PROMPT_HISTORY_KEY, ASK_PROMPT_HISTORY_LIMIT,
  loadAskDraft, loadLastAskThread, loadPromptHistory, recordPrompt, saveAskDraft, saveLastAskThread,
} from './askStorage';

beforeEach(() => localStorage.clear());

describe('Ask browser-local state', () => {
  it('keeps one draft per conversation and per user, and drops a draft when its text is cleared', () => {
    saveAskDraft('usr_a', 'ath_1', 'first draft');
    saveAskDraft('usr_a', 'ath_2', 'second draft');
    saveAskDraft('usr_b', 'ath_1', 'other user');
    expect(loadAskDraft('usr_a', 'ath_1')).toBe('first draft');
    expect(loadAskDraft('usr_a', 'ath_2')).toBe('second draft');
    expect(loadAskDraft('usr_b', 'ath_1')).toBe('other user');
    saveAskDraft('usr_a', 'ath_1', '');
    expect(loadAskDraft('usr_a', 'ath_1')).toBe('');
    expect(JSON.parse(localStorage.getItem(`${ASK_DRAFTS_STORAGE_KEY}:usr_a`)!)).toEqual({ ath_2: 'second draft' });
  });

  it('keeps only the newest drafts and tolerates a corrupt store', () => {
    for (let index = 0; index < ASK_DRAFT_LIMIT + 5; index += 1) saveAskDraft('usr_a', `ath_${index}`, `draft ${index}`);
    expect(loadAskDraft('usr_a', 'ath_0')).toBe('');
    expect(loadAskDraft('usr_a', `ath_${ASK_DRAFT_LIMIT + 4}`)).toBe(`draft ${ASK_DRAFT_LIMIT + 4}`);
    localStorage.setItem(`${ASK_DRAFTS_STORAGE_KEY}:usr_a`, '{not json');
    expect(loadAskDraft('usr_a', 'ath_1')).toBe('');
    localStorage.setItem(`${ASK_DRAFTS_STORAGE_KEY}:usr_a`, '["list"]');
    expect(loadAskDraft('usr_a', '0')).toBe('');
  });

  it('remembers the last open conversation per user', () => {
    saveLastAskThread('usr_a', 'ath_9');
    expect(loadLastAskThread('usr_a')).toBe('ath_9');
    expect(loadLastAskThread('usr_b')).toBe('');
    saveLastAskThread('usr_a', '');
    expect(loadLastAskThread('usr_a')).toBe('');
  });

  it('records sent prompts oldest first, skipping blanks and a repeat of the newest, up to the limit', () => {
    recordPrompt('one');
    recordPrompt('   ');
    recordPrompt('one');
    recordPrompt('two');
    expect(loadPromptHistory()).toEqual(['one', 'two']);
    for (let index = 0; index < ASK_PROMPT_HISTORY_LIMIT + 3; index += 1) recordPrompt(`prompt ${index}`);
    const history = loadPromptHistory();
    expect(history).toHaveLength(ASK_PROMPT_HISTORY_LIMIT);
    expect(history[history.length - 1]).toBe(`prompt ${ASK_PROMPT_HISTORY_LIMIT + 2}`);
    localStorage.setItem(ASK_PROMPT_HISTORY_KEY, '{broken');
    expect(loadPromptHistory()).toEqual([]);
  });
});
