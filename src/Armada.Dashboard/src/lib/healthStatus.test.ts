import { describe, expect, it } from 'vitest';
import { classifyHealth } from './healthStatus';

describe('classifyHealth', () => {
  it('reports healthy when the server is healthy and not behind', () => {
    expect(classifyHealth({ status: 'healthy', behindBy: 0, driftWarning: null })).toEqual({ status: 'healthy', driftWarning: null });
  });

  it('reports a warning when the running build is behind the landed commit', () => {
    const result = classifyHealth({ status: 'healthy', runningCommit: 'aaa', landedCommit: 'bbb', behindBy: 19, driftWarning: null });
    expect(result.status).toBe('warning');
    expect(result.driftWarning).toContain('19');
  });

  it('reports a warning and carries the server drift warning text', () => {
    const result = classifyHealth({ status: 'healthy', behindBy: 0, driftWarning: 'Running commit is unknown.' });
    expect(result).toEqual({ status: 'warning', driftWarning: 'Running commit is unknown.' });
  });

  it('keeps degraded and error statuses', () => {
    expect(classifyHealth({ status: 'degraded' }).status).toBe('warning');
    expect(classifyHealth({ status: 'down' }).status).toBe('error');
  });
});
