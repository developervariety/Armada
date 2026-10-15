import type { BacklogGroupDefinition, BacklogGroupKey } from './backlogUtils';

interface BacklogGroupPillsProps {
  groups: BacklogGroupDefinition[];
  activeGroup: BacklogGroupKey;
  counts: Record<BacklogGroupKey, number>;
  onChange: (group: BacklogGroupKey) => void;
}

/** Hover text per group: what the pill shows and which items count toward it. */
const GROUP_TOOLTIPS: Record<BacklogGroupKey, string> = {
  all: 'Show every backlog item, whatever its stage.',
  inbox: 'Show items still being shaped: the Inbox, Triaged and Refining stages, not blocked.',
  planning: 'Show items in the ReadyForPlanning stage, scoped well enough to start a planning session.',
  dispatch: 'Show items in the ReadyForDispatch or Dispatched stages, ready for or already sent to captains.',
  blocked: 'Show items whose status is Blocked or that are waiting on other backlog items.',
};

export default function BacklogGroupPills({
  groups,
  activeGroup,
  counts,
  onChange,
}: BacklogGroupPillsProps) {
  return (
    <div className="backlog-group-pills" role="group" aria-label="Backlog group views" title="Quick views of the backlog by stage. The number is how many items are in each view.">
      {groups.map((group) => (
        <button
          key={group.key}
          type="button"
          className={`backlog-group-pill${activeGroup === group.key ? ' active' : ''}`}
          title={GROUP_TOOLTIPS[group.key] ?? group.description}
          aria-pressed={activeGroup === group.key}
          onClick={() => onChange(group.key)}
        >
          <span>{group.label}</span>
          <strong>{counts[group.key] || 0}</strong>
        </button>
      ))}
    </div>
  );
}
