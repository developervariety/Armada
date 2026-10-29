import { PlanningRedirect } from '@/navigation/legacyRedirects';

/** /planning/:id: now /dispatch?tab=planning&session=:id (other query parameters such as ?prompt= are kept). */
export default function PlanningSessionRoute() {
  return <PlanningRedirect />;
}
