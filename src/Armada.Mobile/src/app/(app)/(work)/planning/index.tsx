import { PlanningRedirect } from '@/navigation/legacyRedirects';

/** /planning: now the Dispatch hub's Planning tab; prefill params (`?new=1`, vesselId, prompt, ...) are kept. */
export default function PlanningRoute() {
  return <PlanningRedirect />;
}
