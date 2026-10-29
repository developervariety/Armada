import { readFileSync } from 'node:fs';

// Every dashboard table with an ID column shows it directly to the left of the Name (or primary label) column.
// Column order is not persisted (useColumnVisibility only stores hidden keys), so the array order is what users see.
// Each entry lists, per ID column in the file (in source order), the key of the column that must follow it.
const idFollowedBy: Record<string, string[]> = {
  'components/fleetActions/FleetActionsTable.tsx': ['name'],
  'components/fleetActions/FleetActionRunsTable.tsx': ['action'],
  'components/planning/PlanningSessionListCard.tsx': ['title'],
  // No name column: the ID is the first data column.
  'components/vessels/import/ImportHistory.tsx': ['createdUtc'],
  'pages/Captains.tsx': ['name'],
  'pages/CaptainDetail.tsx': ['mission'],
  'pages/CheckRuns.tsx': ['check'],
  'pages/Dashboard.tsx': ['voyage', 'mission'],
  'pages/Deployments.tsx': ['title'],
  'pages/Endpoints.tsx': ['name'],
  'pages/Environments.tsx': ['name'],
  'pages/FleetDetail.tsx': ['name'],
  'pages/Fleets.tsx': ['name'],
  'pages/Harbors.tsx': ['name'],
  'pages/Incidents.tsx': ['title'],
  'pages/Missions.tsx': ['title'],
  'pages/Objectives.tsx': ['title'],
  'pages/Personas.tsx': ['name'],
  'pages/Pipelines.tsx': ['name'],
  'pages/Playbooks.tsx': ['fileName'],
  'pages/ProjectProfiles.tsx': ['name'],
  'pages/Releases.tsx': ['title'],
  'pages/Runbooks.tsx': ['title'],
  'pages/Skills.tsx': ['name'],
  'pages/Vessels.tsx': ['name'],
  'pages/VesselDetail.tsx': ['mission'],
  'pages/VoyageDetail.tsx': ['mission'],
  'pages/Voyages.tsx': ['title'],
  'pages/WorkflowProfiles.tsx': ['name'],
  'pages/admin/Credentials.tsx': ['name'],
  'pages/admin/Tenants.tsx': ['name'],
  // Email is the primary label; the ID sits between it and the Name column.
  'pages/admin/Users.tsx': ['name'],
};

function columnKeys(file: string): string[] {
  const source = readFileSync(`./src/${file}`, 'utf8');
  return [...source.matchAll(/^\s*(?:\{\s*)?key: '([^']+)'/gm)].map((m) => m[1]);
}

describe('ID column order', () => {
  it.each(Object.entries(idFollowedBy))('%s puts each ID column directly before its label column', (file, expected) => {
    const keys = columnKeys(file);
    const following = keys.flatMap((key, i) => (key === 'id' ? [keys[i + 1]] : []));
    expect(following).toEqual(expected);
  });

  it('tables without a name column keep the ID first', () => {
    for (const file of ['pages/Docks.tsx', 'pages/Signals.tsx', 'pages/Events.tsx', 'pages/MergeQueue.tsx', 'components/vessels/import/ImportHistory.tsx']) {
      expect(columnKeys(file)[0]).toBe('id');
    }
  });
});
