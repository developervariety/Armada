import { CliPermissionsRedirect } from '@/navigation/legacyRedirects';

/** /cli-permissions: now the Settings hub's CLI Tool Permissions tab; ?tab= becomes ?section=, ?request= is kept. */
export default function Screen() {
  return <CliPermissionsRedirect />;
}
