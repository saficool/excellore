'use client';

import { useEffect, useState } from 'react';
import { SharedHeader, type SharedHeaderProps } from '@excellore/ui';

export function ProfileHeader(props: SharedHeaderProps) {
  const [hasAvatar, setHasAvatar] = useState(false);
  const [avatarRevision, setAvatarRevision] = useState(0);
  const [userLabel, setUserLabel] = useState(props.userLabel);

  useEffect(() => {
    let active = true;
    async function refreshAvatar() {
      try {
        const response = await fetch('/api/me/profile', {
          credentials: 'same-origin',
          cache: 'no-store',
        });
        if (!response.ok) return;
        const profile = (await response.json()) as {
          hasAvatar?: boolean;
          displayName?: string | null;
          firstName?: string | null;
        };
        if (active) {
          setHasAvatar(profile.hasAvatar === true);
          setUserLabel(
            profile.displayName?.trim() ||
              profile.firstName?.trim() ||
              props.userLabel,
          );
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
  }, [props.userLabel]);

  return (
    <SharedHeader
      {...props}
      userLabel={userLabel}
      avatarSrc={hasAvatar ? '/api/avatar' : undefined}
      avatarRevision={avatarRevision}
    />
  );
}
