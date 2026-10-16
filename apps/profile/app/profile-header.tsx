'use client';

import { useEffect, useState } from 'react';
import { SharedHeader, type SharedHeaderProps } from '@excellore/ui';

export function ProfileHeader(props: SharedHeaderProps) {
  const [hasAvatar, setHasAvatar] = useState(false);
  const [avatarRevision, setAvatarRevision] = useState(0);

  useEffect(() => {
    let active = true;
    async function refreshAvatar() {
      try {
        const response = await fetch('/api/me/profile', {
          credentials: 'same-origin',
          cache: 'no-store',
        });
        if (!response.ok) return;
        const profile = (await response.json()) as { hasAvatar?: boolean };
        if (active) {
          setHasAvatar(profile.hasAvatar === true);
          setAvatarRevision((current) => current + 1);
        }
      } catch {
        // The avatar is optional; the account menu remains available.
      }
    }

    void refreshAvatar();
    window.addEventListener('profile-avatar-changed', refreshAvatar);
    return () => {
      active = false;
      window.removeEventListener('profile-avatar-changed', refreshAvatar);
    };
  }, []);

  return (
    <SharedHeader
      {...props}
      avatarSrc={hasAvatar ? '/api/avatar' : undefined}
      avatarRevision={avatarRevision}
    />
  );
}
