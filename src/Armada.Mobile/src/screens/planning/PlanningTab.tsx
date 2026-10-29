import { Stack, useLocalSearchParams, useRouter, type Href } from 'expo-router';
import { useState, type ReactNode } from 'react';
import { useLocale } from '../../i18n/LocaleContext';
import { PLANNING_PATH } from '../../navigation/legacyRedirects';
import { useLayout } from '../../navigation/useLayout';
import { PlanningScreen } from './PlanningScreen';
import { PlanningSessionView } from './PlanningSessionView';
import { usePlanningCatalog } from './usePlanningCatalog';

/**
 * The Dispatch hub's Planning tab (/dispatch?tab=planning; /planning and /planning/:id redirect here). `?session=`
 * opens one session: narrow windows show it on its own screen (back returns to the list); windows wide enough for
 * list and detail show the Planning list with this session open beside it, like the dashboard's tab. Decided when the
 * tab opens, so a later resize re-lays out the same screen instead of swapping it (the session's draft and scroll are
 * kept). `?prompt=` drafts the session's first message.
 */
export function PlanningTab({ tabBar }: { tabBar: ReactNode }) {
  const { t } = useLocale();
  const params = useLocalSearchParams<{ session?: string; prompt?: string }>();
  const session = params.session || null;
  const { split } = useLayout();
  const [asList] = useState(split);
  if (session && !asList) {
    return (
      <>
        <Stack.Screen options={{ title: t('Planning') }} />
        <PhoneSession id={session} prompt={params.prompt ?? null} />
      </>
    );
  }
  return (
    <>
      <Stack.Screen options={{ title: t('Dispatch') }} />
      <PlanningScreen key={session ?? ''} initialSessionId={session} embedded tabBar={tabBar} />
    </>
  );
}

function PhoneSession({ id, prompt }: { id: string; prompt: string | null }) {
  const router = useRouter();
  const catalog = usePlanningCatalog();
  const leave = () => {
    if (router.canGoBack()) router.back();
    else router.replace(PLANNING_PATH as Href);
  };
  return <PlanningSessionView key={id} sessionId={id} catalog={catalog} initialComposer={prompt} onClosed={leave} />;
}
