import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  createWorkflowProfile,
  deleteWorkflowProfile,
  getWorkflowProfile,
  listFleets,
  listVessels,
  updateWorkflowProfile,
  validateWorkflowProfile,
} from '../api/client';
import type { Fleet, Vessel, WorkflowEnvironmentProfile, WorkflowProfile, WorkflowProfileValidationResult } from '../types/models';
import { useAuth } from '../context/AuthContext';
import { useLocale } from '../context/LocaleContext';
import { useNotifications } from '../context/NotificationContext';
import ConfirmDialog from '../components/shared/ConfirmDialog';
import CopyButton from '../components/shared/CopyButton';
import ErrorModal from '../components/shared/ErrorModal';
import JsonViewer from '../components/shared/JsonViewer';
import StatusBadge from '../components/shared/StatusBadge';

function splitList(value: string): string[] {
  return value
    .split(/\r?\n|,/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function joinList(values: string[] | null | undefined): string {
  return (values || []).join('\n');
}

function blankEnvironment(): WorkflowEnvironmentProfile {
  return {
    environmentName: 'dev',
    deployCommand: null,
    rollbackCommand: null,
    smokeTestCommand: null,
    healthCheckCommand: null,
  };
}

export default function WorkflowProfileDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { isAdmin, isTenantAdmin } = useAuth();
  const { t, formatDateTime } = useLocale();
  const { pushToast } = useNotifications();

  const createMode = id === 'new';
  const canManage = isAdmin || isTenantAdmin;

  const [profile, setProfile] = useState<WorkflowProfile | null>(null);
  const [fleets, setFleets] = useState<Fleet[]>([]);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [name, setName] = useState('Default Workflow');
  const [description, setDescription] = useState('');
  const [scope, setScope] = useState<'Global' | 'Fleet' | 'Vessel'>('Global');
  const [fleetId, setFleetId] = useState('');
  const [vesselId, setVesselId] = useState('');
  const [isDefault, setIsDefault] = useState(false);
  const [active, setActive] = useState(true);
  const [languageHints, setLanguageHints] = useState('');
  const [requiredSecrets, setRequiredSecrets] = useState('');
  const [expectedArtifacts, setExpectedArtifacts] = useState('');
  const [lintCommand, setLintCommand] = useState('');
  const [buildCommand, setBuildCommand] = useState('');
  const [unitTestCommand, setUnitTestCommand] = useState('');
  const [integrationTestCommand, setIntegrationTestCommand] = useState('');
  const [e2eTestCommand, setE2ETestCommand] = useState('');
  const [packageCommand, setPackageCommand] = useState('');
  const [publishArtifactCommand, setPublishArtifactCommand] = useState('');
  const [releaseVersioningCommand, setReleaseVersioningCommand] = useState('');
  const [changelogGenerationCommand, setChangelogGenerationCommand] = useState('');
  const [environments, setEnvironments] = useState<WorkflowEnvironmentProfile[]>([]);
  const [validation, setValidation] = useState<WorkflowProfileValidationResult | null>(null);
  const [loading, setLoading] = useState(!createMode);
  const [saving, setSaving] = useState(false);
  const [validating, setValidating] = useState(false);
  const [error, setError] = useState('');
  const [jsonData, setJsonData] = useState<{ open: boolean; title: string; data: unknown }>({ open: false, title: '', data: null });
  const [confirm, setConfirm] = useState<{ open: boolean; title: string; message: string; onConfirm: () => void }>({
    open: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  useEffect(() => {
    let cancelled = false;
    Promise.all([listFleets({ pageSize: 9999 }), listVessels({ pageSize: 9999 })])
      .then(([fleetResult, vesselResult]) => {
        if (cancelled) return;
        setFleets(fleetResult.objects || []);
        setVessels(vesselResult.objects || []);
      })
      .catch(() => {
        if (!cancelled) setError(t('Failed to load fleets and vessels.'));
      });

    return () => { cancelled = true; };
  }, [t]);

  useEffect(() => {
    if (createMode || !id) return;
    let mounted = true;
    const profileId = id;

    async function load() {
      try {
        setLoading(true);
        const result = await getWorkflowProfile(profileId);
        if (!mounted) return;
        setProfile(result);
        setName(result.name);
        setDescription(result.description || '');
        setScope(result.scope);
        setFleetId(result.fleetId || '');
        setVesselId(result.vesselId || '');
        setIsDefault(result.isDefault);
        setActive(result.active);
        setLanguageHints(joinList(result.languageHints));
        setRequiredSecrets(joinList(result.requiredSecrets));
        setExpectedArtifacts(joinList(result.expectedArtifacts));
        setLintCommand(result.lintCommand || '');
        setBuildCommand(result.buildCommand || '');
        setUnitTestCommand(result.unitTestCommand || '');
        setIntegrationTestCommand(result.integrationTestCommand || '');
        setE2ETestCommand(result.e2eTestCommand || '');
        setPackageCommand(result.packageCommand || '');
        setPublishArtifactCommand(result.publishArtifactCommand || '');
        setReleaseVersioningCommand(result.releaseVersioningCommand || '');
        setChangelogGenerationCommand(result.changelogGenerationCommand || '');
        setEnvironments(result.environments || []);
        setError('');
      } catch (err: unknown) {
        if (mounted) setError(err instanceof Error ? err.message : t('Failed to load workflow profile.'));
      } finally {
        if (mounted) setLoading(false);
      }
    }

    load();
    return () => { mounted = false; };
  }, [createMode, id, t]);

  const fleetOptions = useMemo(() => fleets.filter((fleet) => fleet.active !== false), [fleets]);
  const vesselOptions = useMemo(() => vessels.filter((vessel) => vessel.active !== false), [vessels]);

  function buildPayload(): Partial<WorkflowProfile> {
    return {
      name: name.trim(),
      description: description.trim() || null,
      scope,
      fleetId: scope === 'Fleet' ? fleetId || null : null,
      vesselId: scope === 'Vessel' ? vesselId || null : null,
      isDefault,
      active,
      languageHints: splitList(languageHints),
      lintCommand: lintCommand.trim() || null,
      buildCommand: buildCommand.trim() || null,
      unitTestCommand: unitTestCommand.trim() || null,
      integrationTestCommand: integrationTestCommand.trim() || null,
      e2eTestCommand: e2eTestCommand.trim() || null,
      packageCommand: packageCommand.trim() || null,
      publishArtifactCommand: publishArtifactCommand.trim() || null,
      releaseVersioningCommand: releaseVersioningCommand.trim() || null,
      changelogGenerationCommand: changelogGenerationCommand.trim() || null,
      requiredSecrets: splitList(requiredSecrets),
      expectedArtifacts: splitList(expectedArtifacts),
      environments: environments.map((environment) => ({
        environmentName: environment.environmentName.trim(),
        deployCommand: environment.deployCommand?.trim() || null,
        rollbackCommand: environment.rollbackCommand?.trim() || null,
        smokeTestCommand: environment.smokeTestCommand?.trim() || null,
        healthCheckCommand: environment.healthCheckCommand?.trim() || null,
      })).filter((environment) => environment.environmentName),
    };
  }

  async function handleValidate() {
    try {
      setValidating(true);
      const result = await validateWorkflowProfile(buildPayload());
      setValidation(result);
      if (result.isValid) {
        pushToast('success', t('Workflow profile is valid.'));
      } else {
        pushToast('warning', t('Workflow profile has validation errors.'));
      }
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Validation failed.'));
    } finally {
      setValidating(false);
    }
  }

  async function handleSave() {
    if (!canManage) return;
    try {
      setSaving(true);
      const payload = buildPayload();
      if (createMode) {
        const created = await createWorkflowProfile(payload);
        pushToast('success', t('Workflow profile "{{name}}" created.', { name: created.name }));
        navigate(`/workflow-profiles/${created.id}`);
        return;
      }

      if (!id) return;
      const updated = await updateWorkflowProfile(id, payload);
      setProfile(updated);
      pushToast('success', t('Workflow profile "{{name}}" saved.', { name: updated.name }));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t('Save failed.'));
    } finally {
      setSaving(false);
    }
  }

  function handleDelete() {
    if (!profile || !canManage) return;
    setConfirm({
      open: true,
      title: t('Delete Workflow Profile'),
      message: t('Delete "{{name}}"? Existing check runs remain, but future runs will not be able to resolve this profile.', { name: profile.name }),
      onConfirm: async () => {
        setConfirm((current) => ({ ...current, open: false }));
        try {
          await deleteWorkflowProfile(profile.id);
          pushToast('warning', t('Workflow profile "{{name}}" deleted.', { name: profile.name }));
          navigate('/workflow-profiles');
        } catch (err: unknown) {
          setError(err instanceof Error ? err.message : t('Delete failed.'));
        }
      },
    });
  }

  function updateEnvironment(index: number, key: keyof WorkflowEnvironmentProfile, value: string) {
    setEnvironments((current) => current.map((environment, currentIndex) => (
      currentIndex === index ? { ...environment, [key]: value || null } : environment
    )));
  }

  if (loading) return <p className="text-dim">{t('Loading...')}</p>;

  return (
    <div>
      <div className="breadcrumb">
        <Link to="/workflow-profiles">{t('Workflow Profiles')}</Link> <span className="breadcrumb-sep">&gt;</span> <span>{createMode ? t('New Workflow Profile') : name}</span>
      </div>

      <div className="detail-header">
        <h2>{createMode ? t('Create Workflow Profile') : name}</h2>
        <div className="inline-actions">
          {!createMode && <StatusBadge status={active ? 'Active' : 'Inactive'} />}
          {!createMode && (
            <button className="btn btn-sm" onClick={() => setJsonData({ open: true, title: name, data: profile })}>
              {t('View JSON')}
            </button>
          )}
          {!createMode && canManage && (
            <button className="btn btn-sm btn-danger" onClick={handleDelete}>
              {t('Delete')}
            </button>
          )}
        </div>
      </div>

      <ErrorModal error={error} onClose={() => setError('')} />
      <JsonViewer open={jsonData.open} title={jsonData.title} data={jsonData.data} onClose={() => setJsonData({ open: false, title: '', data: null })} />
      <ConfirmDialog
        open={confirm.open}
        title={confirm.title}
        message={confirm.message}
        onConfirm={confirm.onConfirm}
        onCancel={() => setConfirm((current) => ({ ...current, open: false }))}
      />

      {!canManage && (
        <div className="alert alert-error" style={{ marginBottom: '1rem' }}>
          {t('You can view workflow profiles, but only tenant administrators can change them.')}
        </div>
      )}

      {!createMode && profile && (
        <div className="detail-grid" style={{ marginBottom: '1rem' }}>
          <div className="detail-field">
            <span className="detail-label">{t('ID')}</span>
            <span className="id-display">
              <span className="mono">{profile.id}</span>
              <CopyButton text={profile.id} />
            </span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Created')}</span>
            <span>{formatDateTime(profile.createdUtc)}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Last Updated')}</span>
            <span>{formatDateTime(profile.lastUpdateUtc)}</span>
          </div>
          <div className="detail-field">
            <span className="detail-label">{t('Scope')}</span>
            <StatusBadge status={profile.scope} />
          </div>
        </div>
      )}

      {validation && (
        <div className="card" style={{ marginBottom: '1rem' }}>
          <div className="detail-header" style={{ marginBottom: '0.75rem' }}>
            <h3>{t('Validation')}</h3>
            <StatusBadge status={validation.isValid ? 'Valid' : 'Needs Attention'} />
          </div>
          <div className="detail-grid">
            <div className="detail-field">
              <span className="detail-label">{t('Available Check Types')}</span>
              <span>{validation.availableCheckTypes.length > 0 ? validation.availableCheckTypes.join(', ') : t('None')}</span>
            </div>
          </div>
          {validation.errors.length > 0 && (
            <div style={{ marginTop: '0.75rem' }}>
              <strong>{t('Errors')}</strong>
              <ul style={{ marginTop: '0.4rem' }}>
                {validation.errors.map((item) => <li key={item}>{item}</li>)}
              </ul>
            </div>
          )}
          {validation.warnings.length > 0 && (
            <div style={{ marginTop: '0.75rem' }}>
              <strong>{t('Warnings')}</strong>
              <ul style={{ marginTop: '0.4rem' }}>
                {validation.warnings.map((item) => <li key={item}>{item}</li>)}
              </ul>
            </div>
          )}
        </div>
      )}

      <div className="card playbook-editor-card">
        <div className="detail-grid" style={{ marginBottom: '1rem' }}>
          <label className="playbook-editor-field">
            <span>{t('Name')}</span>
            <input type="text" value={name} disabled={!canManage} onChange={(event) => setName(event.target.value)} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Scope')}</span>
            <select value={scope} disabled={!canManage} onChange={(event) => setScope(event.target.value as 'Global' | 'Fleet' | 'Vessel')}>
              <option value="Global">{t('Global')}</option>
              <option value="Fleet">{t('Fleet')}</option>
              <option value="Vessel">{t('Vessel')}</option>
            </select>
          </label>
          {scope === 'Fleet' && (
            <label className="playbook-editor-field">
              <span>{t('Fleet')}</span>
              <select value={fleetId} disabled={!canManage} onChange={(event) => setFleetId(event.target.value)}>
                <option value="">{t('Select a fleet...')}</option>
                {fleetOptions.map((fleet) => (
                  <option key={fleet.id} value={fleet.id}>{fleet.name}</option>
                ))}
              </select>
            </label>
          )}
          {scope === 'Vessel' && (
            <label className="playbook-editor-field">
              <span>{t('Vessel')}</span>
              <select value={vesselId} disabled={!canManage} onChange={(event) => setVesselId(event.target.value)}>
                <option value="">{t('Select a vessel...')}</option>
                {vesselOptions.map((vessel) => (
                  <option key={vessel.id} value={vessel.id}>{vessel.name}</option>
                ))}
              </select>
            </label>
          )}
        </div>

        <label className="playbook-editor-field">
          <span>{t('Description')}</span>
          <input type="text" value={description} disabled={!canManage} onChange={(event) => setDescription(event.target.value)} />
        </label>

        <div className="detail-grid" style={{ marginBottom: '1rem' }}>
          <label className="playbook-toggle">
            <input type="checkbox" checked={isDefault} disabled={!canManage} onChange={(event) => setIsDefault(event.target.checked)} />
            <span>{t('Default for this scope')}</span>
          </label>
          <label className="playbook-toggle">
            <input type="checkbox" checked={active} disabled={!canManage} onChange={(event) => setActive(event.target.checked)} />
            <span>{t('Active')}</span>
          </label>
        </div>

        <div className="detail-grid" style={{ marginBottom: '1rem' }}>
          <label className="playbook-editor-field">
            <span>{t('Language / Runtime Hints')}</span>
            <textarea value={languageHints} disabled={!canManage} onChange={(event) => setLanguageHints(event.target.value)} rows={3} placeholder={t('dotnet\nreact\npostgres')} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Required Secrets / Config References')}</span>
            <textarea value={requiredSecrets} disabled={!canManage} onChange={(event) => setRequiredSecrets(event.target.value)} rows={3} placeholder={t('AWS_PROFILE\nKUBECONFIG')} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Expected Artifacts')}</span>
            <textarea value={expectedArtifacts} disabled={!canManage} onChange={(event) => setExpectedArtifacts(event.target.value)} rows={3} placeholder={t('bin/Release/app.zip\ncoverage/summary.xml')} />
          </label>
        </div>

        <div className="detail-grid" style={{ marginBottom: '1rem' }}>
          <label className="playbook-editor-field">
            <span>{t('Lint Command')}</span>
            <textarea value={lintCommand} disabled={!canManage} onChange={(event) => setLintCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Build Command')}</span>
            <textarea value={buildCommand} disabled={!canManage} onChange={(event) => setBuildCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Unit Test Command')}</span>
            <textarea value={unitTestCommand} disabled={!canManage} onChange={(event) => setUnitTestCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Integration Test Command')}</span>
            <textarea value={integrationTestCommand} disabled={!canManage} onChange={(event) => setIntegrationTestCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('E2E Test Command')}</span>
            <textarea value={e2eTestCommand} disabled={!canManage} onChange={(event) => setE2ETestCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Package Command')}</span>
            <textarea value={packageCommand} disabled={!canManage} onChange={(event) => setPackageCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Publish Artifact Command')}</span>
            <textarea value={publishArtifactCommand} disabled={!canManage} onChange={(event) => setPublishArtifactCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Release Versioning Command')}</span>
            <textarea value={releaseVersioningCommand} disabled={!canManage} onChange={(event) => setReleaseVersioningCommand(event.target.value)} rows={3} />
          </label>
          <label className="playbook-editor-field">
            <span>{t('Changelog Generation Command')}</span>
            <textarea value={changelogGenerationCommand} disabled={!canManage} onChange={(event) => setChangelogGenerationCommand(event.target.value)} rows={3} />
          </label>
        </div>

        <div className="card" style={{ marginBottom: '1rem', padding: '1rem' }}>
          <div className="detail-header" style={{ marginBottom: '0.75rem' }}>
            <h3>{t('Environment Commands')}</h3>
            {canManage && (
              <button className="btn btn-sm" type="button" onClick={() => setEnvironments((current) => [...current, blankEnvironment()])}>
                + {t('Environment')}
              </button>
            )}
          </div>

          {environments.length === 0 ? (
            <p className="text-dim">{t('No environment-specific commands configured yet.')}</p>
          ) : (
            <div style={{ display: 'grid', gap: '1rem' }}>
              {environments.map((environment, index) => (
                <div key={`${environment.environmentName}-${index}`} className="card" style={{ padding: '1rem' }}>
                  <div className="detail-header" style={{ marginBottom: '0.75rem' }}>
                    <h4>{environment.environmentName || t('Environment')}</h4>
                    {canManage && (
                      <button className="btn btn-sm btn-danger" type="button" onClick={() => setEnvironments((current) => current.filter((_, currentIndex) => currentIndex !== index))}>
                        {t('Remove')}
                      </button>
                    )}
                  </div>
                  <div className="detail-grid">
                    <label className="playbook-editor-field">
                      <span>{t('Name')}</span>
                      <input type="text" value={environment.environmentName} disabled={!canManage} onChange={(event) => updateEnvironment(index, 'environmentName', event.target.value)} />
                    </label>
                    <label className="playbook-editor-field">
                      <span>{t('Deploy Command')}</span>
                      <textarea value={environment.deployCommand || ''} disabled={!canManage} onChange={(event) => updateEnvironment(index, 'deployCommand', event.target.value)} rows={3} />
                    </label>
                    <label className="playbook-editor-field">
                      <span>{t('Rollback Command')}</span>
                      <textarea value={environment.rollbackCommand || ''} disabled={!canManage} onChange={(event) => updateEnvironment(index, 'rollbackCommand', event.target.value)} rows={3} />
                    </label>
                    <label className="playbook-editor-field">
                      <span>{t('Smoke Test Command')}</span>
                      <textarea value={environment.smokeTestCommand || ''} disabled={!canManage} onChange={(event) => updateEnvironment(index, 'smokeTestCommand', event.target.value)} rows={3} />
                    </label>
                    <label className="playbook-editor-field">
                      <span>{t('Health Check Command')}</span>
                      <textarea value={environment.healthCheckCommand || ''} disabled={!canManage} onChange={(event) => updateEnvironment(index, 'healthCheckCommand', event.target.value)} rows={3} />
                    </label>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="playbook-editor-actions">
          <button className="btn" disabled={validating} onClick={handleValidate}>
            {validating ? t('Validating...') : t('Validate')}
          </button>
          <button className="btn btn-primary" disabled={!canManage || saving} onClick={handleSave}>
            {saving ? t('Saving...') : createMode ? t('Create Workflow Profile') : t('Save Changes')}
          </button>
          <button className="btn" onClick={() => navigate('/workflow-profiles')}>
            {t('Back')}
          </button>
        </div>
      </div>
    </div>
  );
}
