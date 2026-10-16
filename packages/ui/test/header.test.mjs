import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { SharedHeader } from '@excellore/ui';

const messages = {
  primaryNavigation: 'Main navigation',
  switchTenant: 'Switch tenant',
  switchApp: 'Switch app',
  userMenu: 'User menu',
  myAccount: 'My account',
  signOut: 'Sign out',
};
const props = {
  homeHref: '/',
  currentTenant: { slug: 'example', name: 'Example Co' },
  tenants: [
    { id: 'example', name: 'Example Co', href: '/t/example' },
    { id: 'another', name: 'Another Co', href: '/t/another' },
  ],
  apps: [
    {
      id: 'profile',
      name: 'Profile',
      href: 'https://profile.example.test/t/example',
    },
  ],
  currentAppId: 'profile',
  accountHref: 'https://profile.example.test/account',
  userLabel: 'Signed-in user',
  messages,
};

test('header renders tenant and authorized app navigation with POST sign-out', () => {
  const html = renderToStaticMarkup(createElement(SharedHeader, props));
  assert.match(html, /aria-label="Main navigation"/);
  assert.match(html, /<summary>Switch tenant<\/summary>/);
  assert.match(html, /<summary>Switch app<\/summary>/);
  assert.match(html, /href="\/t\/another"/);
  assert.match(html, /href="https:\/\/profile.example.test\/t\/example"/);
  assert.match(html, /aria-current="page"/);
  assert.match(html, /<form action="\/auth\/logout" method="post">/);
  assert.doesNotMatch(html, /access.example.test/);
});

test('header rejects unsafe navigation URLs', () => {
  assert.throws(() =>
    renderToStaticMarkup(
      createElement(SharedHeader, {
        ...props,
        apps: [{ id: 'access', name: 'Access', href: 'javascript:alert(1)' }],
      }),
    ),
  );
});

test('header renders a same-origin avatar and rejects unsafe image URLs', () => {
  const html = renderToStaticMarkup(
    createElement(SharedHeader, { ...props, avatarSrc: '/api/avatar' }),
  );
  assert.match(html, /src="\/api\/avatar"/);
  assert.match(html, /class="ex-header__avatar"/);
  assert.throws(() =>
    renderToStaticMarkup(
      createElement(SharedHeader, {
        ...props,
        avatarSrc: 'https://other.example.test/avatar',
      }),
    ),
  );
});

test('shared style sheet is exported for app layouts', () => {
  assert.match(
    import.meta.resolve('@excellore/ui/styles.css'),
    /packages\/ui\/src\/styles\.css$/,
  );
});
