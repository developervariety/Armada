import type { VesselHealthCriterion } from '../../../types/models';
import type { Translate } from '../../../lib/health/healthText';

/** Hover help for one health criterion: what it measures and how it is graded (see docs/VESSEL_HEALTH.md). */
export function criterionHelp(t: Translate, criterion: VesselHealthCriterion | string): string {
  switch (criterion) {
    case 'GitDivergence': return t('Commits the checkout is behind (or both ahead and behind) its default branch and upstream. Behind warns and then fails at the configured counts; ahead alone passes.');
    case 'WorkingTree': return t('Uncommitted changes in the checkout. Clean passes, untracked files alone warn, modified tracked files fail. Not applicable to a bare clone.');
    case 'Branches': return t('Local branches whose last commit is older than the stale-branch age, plus leftover armada/* captain branches. Warns, then fails as stale branches pile up.');
    case 'CommitRecency': return t('When the last commit was made. Informational only: it always passes when a commit exists and is not scored by default.');
    case 'Dependencies': return t('Outdated NuGet and npm packages. Minor or patch drift warns, any major version behind fails. Unknown when dotnet or npm could not run.');
    case 'Vulnerabilities': return t('Packages with known security advisories. Low or Moderate warns, High or Critical fails. Unknown when dotnet or npm could not run.');
    case 'TestInfrastructure': return t('Whether the repo has tests and the latest Armada test check passed. Tests plus a passing run passes, tests without one warns, no tests fails.');
    case 'ContinuousIntegration': return t('Whether the repo has a CI definition (GitHub Actions, Azure Pipelines, GitLab CI or Jenkins). Also records license and readme files. Not scored by default.');
    case 'ArmadaReadiness': return t('The vessel readiness check: Armada configuration problems that would block dispatch or landing. Warnings warn, any error fails.');
    case 'MissionOutcomes': return t('Failed and landing-failed missions on this vessel within the mission failure window. Warns and then fails at the configured counts.');
    case 'Overall': return t('Worst status among the scored criteria, after manual overrides. Unknown and Not applicable never fail a vessel.');
    default: return '';
  }
}
