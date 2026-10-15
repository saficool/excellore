export interface PickerTenant {
  id: string;
  name: string;
  href: string;
}

export function lastUsedTenant(
  tenants: readonly PickerTenant[],
  storedId: string | null,
) {
  return tenants.find((tenant) => tenant.id === storedId) ?? null;
}
