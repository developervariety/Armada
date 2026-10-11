import { buildMuxRuntimeOptionsJson, muxFormFromCaptain } from './mux';

describe('Mux captain settings', () => {
  it('retains a saved turn cap when the captain form is saved again', () => {
    const fields = muxFormFromCaptain({
      runtime: 'Mux',
      runtimeOptionsJson: JSON.stringify({ endpoint: 'primary', maxTurns: 42 }),
    });
    expect(fields).toMatchObject({ muxMaxTurns: '42' });
    const saved = JSON.parse(buildMuxRuntimeOptionsJson('Mux', fields)!);
    expect(saved.maxTurns).toBe(42);
    expect(saved.endpoint).toBe('primary');
  });
});
