import { useEffect, useState } from 'react';
import { getProxySessionContext, type ProxySessionContext } from '../api/client';

export function useProxySessionContext() {
  const [proxyContext, setProxyContext] = useState<ProxySessionContext | null>(null);

  useEffect(() => {
    let mounted = true;

    getProxySessionContext()
      .then((context) => {
        if (mounted) setProxyContext(context);
      })
      .catch(() => {
        if (mounted) setProxyContext(null);
      });

    return () => {
      mounted = false;
    };
  }, []);

  return proxyContext;
}
