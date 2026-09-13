/** A vessel landing mode choice with its label and what the server does for it. */
export interface LandingModeOption {
  /** Server LandingMode value; empty means the vessel inherits the landing mode. */
  value: string;
  label: string;
  help: string;
}

/** Landing modes the server accepts, described as docs/MERGING.md specifies their behavior. */
export const LANDING_MODE_OPTIONS: LandingModeOption[] = [
  {
    value: '',
    label: 'Default',
    help: 'Uses the global landing mode. A voyage landing mode, when set, takes priority over the vessel.',
  },
  {
    value: 'LocalMerge',
    label: 'Local Merge',
    help: "Merges the mission branch into the vessel's managed repository and fast-forwards the configured working checkout. Armada does not push. Without both the local path and the working directory, the mission stays at WorkProduced.",
  },
  {
    value: 'PullRequest',
    label: 'Pull Request',
    help: 'Opens a pull request. The mission completes after the pull request is merged.',
  },
  {
    value: 'MergeQueue',
    label: 'Merge Queue',
    help: "Adds the branch to Armada's merge queue, which tests and lands entries one at a time.",
  },
  {
    value: 'None',
    label: 'None',
    help: 'No automatic landing. The mission stays at WorkProduced for manual handling.',
  },
];

/** Help text for a landing mode value, or an empty string for a value the server does not define. */
export function landingModeHelp(value: string | null | undefined): string {
  const option = LANDING_MODE_OPTIONS.find((item) => item.value === (value ?? ''));
  return option ? option.help : '';
}
