'use client';

import { useEffect } from 'react';
import { lastUsedTenant, type PickerTenant } from '../lib/navigation';

export function TenantPicker({
  tenants,
}: {
  tenants: readonly PickerTenant[];
}) {
  useEffect(() => {
    try {
      const tenant = lastUsedTenant(
        tenants,
        localStorage.getItem('excellore:last-tenant'),
      );
      if (tenant) window.location.replace(tenant.href);
    } catch {
      /* Private browsing can disable local storage. */
    }
  }, [tenants]);
  return (
    <ul className="ex-tenant-list">
      {tenants.map((tenant) => (
        <li key={tenant.id}>
          <a className="ex-tile" href={tenant.href}>
            {tenant.name}
          </a>
        </li>
      ))}
    </ul>
  );
}

export function RememberTenant({ id }: { id: string }) {
  useEffect(() => {
    try {
      localStorage.setItem('excellore:last-tenant', id);
    } catch {
      /* Preference storage is optional. */
    }
  }, [id]);
  return null;
}
