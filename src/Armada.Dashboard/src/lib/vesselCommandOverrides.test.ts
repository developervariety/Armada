import type { Vessel } from '../types/models';
import { findUnknownTemplateVariables, renderTemplatePreview } from './fleetActionTemplate';
import { TEMPLATE_VARIABLES } from './fleetActionLabels';
import { buildVesselPayload, emptyVesselForm, vesselToForm } from './vesselForm';

describe('vessel command overrides in the vessel form', () => {
  it('round-trips TestCommand, LintCommand and AuditCommand through the form and payload', () => {
    const vessel = { id: 'vsl_1', name: 'web', testCommand: 'make test', lintCommand: 'npm run lint', auditCommand: null } as unknown as Vessel;
    const form = vesselToForm(vessel);
    expect(form.testCommand).toBe('make test');
    expect(form.lintCommand).toBe('npm run lint');
    expect(form.auditCommand).toBe('');

    const payload = buildVesselPayload({ ...form, lintCommand: '  ruff check  ', auditCommand: 'osv-scanner -r .' }, vessel);
    expect(payload.testCommand).toBe('make test');
    expect(payload.lintCommand).toBe('ruff check');
    expect(payload.auditCommand).toBe('osv-scanner -r .');
  });

  it('sends null for blank overrides so detection applies', () => {
    const payload = buildVesselPayload({ ...emptyVesselForm, name: 'x', testCommand: '   ' }, null);
    expect(payload.testCommand).toBeNull();
    expect(payload.lintCommand).toBeNull();
    expect(payload.auditCommand).toBeNull();
  });
});

describe('ecosystem template variables', () => {
  it('accepts the new variables in any case and still rejects unknown ones', () => {
    expect(findUnknownTemplateVariables('{{vessel.ecosystems}} {{Vessel.TestCommand}} {{vessel.lintCommand}} {{vessel.auditCommand}}')).toEqual([]);
    expect(findUnknownTemplateVariables('{{vessel.formatCommand}}')).toEqual(['vessel.formatCommand']);
  });

  it('lists the new variables for the help table', () => {
    const names = TEMPLATE_VARIABLES.map(v => v.name);
    expect(names).toEqual(expect.arrayContaining(['vessel.ecosystems', 'vessel.testCommand', 'vessel.lintCommand', 'vessel.auditCommand']));
  });

  it('previews explicit overrides and leaves server-resolved values as written', () => {
    const vessel = { id: 'vsl_1', name: 'web', defaultBranch: 'main', workingDirectory: '/w', testCommand: null, definitionOfDoneTestCommand: 'npm test', lintCommand: 'eslint .', auditCommand: null };
    const preview = renderTemplatePreview('{{vessel.testCommand}}|{{vessel.lintCommand}}|{{vessel.auditCommand}}|{{vessel.ecosystems}}', vessel, '(health)');
    expect(preview.text).toBe('npm test|eslint .|{{vessel.auditCommand}}|{{vessel.ecosystems}}');
  });
});
