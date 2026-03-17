interface StatusBadgeProps {
  status: string;
  className?: string;
}

const tooltips: Record<string, string> = {
  pending: 'Mission created, waiting to be assigned to a captain',
  assigned: 'Mission has been assigned to a captain but work has not started yet',
  inprogress: 'A captain is actively working on this mission',
  workproduced: 'Code exists on a branch, awaiting landing to main',
  testing: 'Mission work is being tested or validated',
  review: 'Mission work is ready for code review',
  complete: 'Mission completed successfully and work has been integrated',
  completed: 'Completed successfully',
  failed: 'Encountered an error and did not complete successfully',
  landingfailed: 'Code was produced but landing failed (merge conflict, push error, etc.)',
  cancelled: 'Cancelled before completion',
  open: 'Voyage is open and missions are being dispatched',
  idle: 'Available and waiting for a mission assignment',
  working: 'Actively executing a mission',
  stalled: 'Process appears unresponsive -- may need intervention',
  stopping: 'In the process of being stopped',
  queued: 'Queued and waiting to be processed',
  passed: 'Tests passed -- ready to land',
  landed: 'Branch has been successfully merged into the target branch',
  active: 'Active and may be in use',
  inactive: 'No longer active',
  assignment: 'Captain was assigned a new mission',
  progress: 'Progress update from a captain or the system',
  completion: 'A captain has finished its work',
  error: 'An error occurred during mission execution',
  heartbeat: 'Periodic heartbeat indicating the captain is alive',
  nudge: 'A nudge signal sent to prompt action',
  mail: 'A message sent between the admiral and a captain',
  pass: 'Health check passed -- no issues detected',
  warn: 'Health check has warnings -- review recommended',
  fail: 'Health check failed -- action required',
};

export default function StatusBadge({ status, className = '' }: StatusBadgeProps) {
  const normalized = (status || '').toLowerCase();
  const tooltip = tooltips[normalized] || '';

  return (
    <span
      className={`tag ${normalized} ${className}`.trim()}
      title={tooltip}
    >
      {status}
    </span>
  );
}
