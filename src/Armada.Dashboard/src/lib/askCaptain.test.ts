import { askCaptainAccess, instructionsDocUrl } from './askCaptain';
import type { CaptainToolAccessResult } from '../types/models';

describe('askCaptain', () => {
  it('links the pi setup instructions for a pi captain', () => {
    expect(instructionsDocUrl('Pi')).toBe('https://github.com/jchristn/Armada/blob/main/docs/INSTRUCTIONS_FOR_PI.md');
    expect(instructionsDocUrl('OpenCode')).toMatch(/INSTRUCTIONS_FOR_OPENCODE\.md$/);
    expect(instructionsDocUrl('Unknown')).toMatch(/MCP_API\.md$/);
  });

  it('treats a server-gated pi captain as approval-gated', () => {
    const tools = { runtime: 'Pi', armadaToolCount: 0, askApprovalGated: true } as unknown as CaptainToolAccessResult;
    expect(askCaptainAccess('cpt_pi', tools)).toEqual({ noCaptain: false, mcpMissing: false, ungated: false });
  });
});
