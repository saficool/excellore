export const sections = [
  { id: 'my-access', label: 'My access', permission: 'access.app.access' },
  { id: 'members', label: 'Members', permission: 'access.member.read' },
  {
    id: 'invitations',
    label: 'Invitations',
    permission: 'access.member.invite',
  },
  { id: 'roles', label: 'Roles', permission: 'access.role.read' },
  {
    id: 'assignments',
    label: 'Assignments',
    permission: 'access.assignment.manage',
  },
  { id: 'apps', label: 'Apps', permission: 'access.apps.read' },
  { id: 'audit', label: 'Audit', permission: 'access.audit.read' },
  {
    id: 'support-grants',
    label: 'Support grants',
    permission: 'access.supportgrant.manage',
  },
  { id: 'settings', label: 'Settings', permission: 'access.settings.read' },
  { id: 'ownership', label: 'Ownership', permission: 'access.app.access' },
  { id: 'deletion', label: 'Deletion', permission: 'access.tenant.delete' },
] as const;

export type SectionId = (typeof sections)[number]['id'];

export function visibleSections(permissions: readonly string[]) {
  const granted = new Set(permissions);
  return sections.filter((section) => granted.has(section.permission));
}

export function sectionAllowed(
  section: string,
  permissions: readonly string[],
) {
  return visibleSections(permissions).some((item) => item.id === section);
}
