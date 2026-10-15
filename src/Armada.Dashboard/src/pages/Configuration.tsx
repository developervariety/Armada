import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';

const WorkflowProfiles = lazy(() => import('./WorkflowProfiles'));
const ProjectProfiles = lazy(() => import('./ProjectProfiles'));
const Skills = lazy(() => import('./Skills'));
const Personas = lazy(() => import('./Personas'));
const Pipelines = lazy(() => import('./Pipelines'));
const PromptTemplates = lazy(() => import('./PromptTemplates'));
const Playbooks = lazy(() => import('./Playbooks'));
const Endpoints = lazy(() => import('./Endpoints'));
const Harbors = lazy(() => import('./Harbors'));
const Memories = lazy(() => import('./Memories'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Configuration hub. Folds the seven "how work gets done" setup surfaces
 * (Workflow Profiles, Project Profiles, Skills, Personas, Pipelines, Prompts,
 * Playbooks) into one tabbed page so rarely-touched configuration stops
 * competing for nav slots with daily-driver work. Each tab lazy-mounts the
 * existing page component; their detail routes are unchanged.
 */
export default function Configuration() {
  const tabs: TabDef[] = [
    { key: 'workflow-profiles', label: 'Workflow Profiles', tooltip: 'The lint, build, test, package, and deploy commands for a vessel, fleet, or every vessel. Checks and deployments run these.', render: () => panel(<WorkflowProfiles />) },
    { key: 'project-profiles', label: 'Project Profiles', tooltip: 'Per-project bundles of a pipeline, workflow profile, persona prompt overrides, and skills, applied to every vessel, a fleet, or one vessel.', render: () => panel(<ProjectProfiles />) },
    { key: 'skills', label: 'Skills', tooltip: 'Reusable capability snippets attached to projects and added to mission prompts.', render: () => panel(<Skills />) },
    { key: 'personas', label: 'Personas', tooltip: 'Roles a captain plays in a pipeline (for example worker or reviewer), with their prompt and which captains may take them.', render: () => panel(<Personas />) },
    { key: 'pipelines', label: 'Pipelines', tooltip: 'Ordered stages of personas a mission passes through, such as implement then review.', render: () => panel(<Pipelines />) },
    { key: 'prompts', label: 'Prompts', tooltip: 'The prompt templates Armada sends to captains. Edit one to change what every captain is told for that step.', render: () => panel(<PromptTemplates />) },
    { key: 'playbooks', label: 'Playbooks', tooltip: 'Written guidance attached to missions so captains follow your team\'s conventions.', render: () => panel(<Playbooks />) },
    { key: 'endpoints', label: 'Endpoints', tooltip: 'Model endpoints (API URL, model, and credentials) that API-based captains call.', render: () => panel(<Endpoints />) },
    { key: 'harbors', label: 'Harbors', tooltip: 'Machines running Armada Harbor, which run captains, git, and worktrees on a developer\'s computer.', render: () => panel(<Harbors />) },
    { key: 'memory', label: 'Memory', tooltip: 'Durable memories distilled from voyages (what happened, facts, and procedures) that later missions can draw on.', render: () => panel(<Memories />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="workflow-profiles" ariaLabel="Configuration sections" />;
}
