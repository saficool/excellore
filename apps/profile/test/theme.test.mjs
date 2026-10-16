import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { applyTheme } from '../app/theme.ts';

const originalDocument = globalThis.document;
after(() => {
  globalThis.document = originalDocument;
});

test('theme preference overrides and clears the page theme', () => {
  globalThis.document = { documentElement: { dataset: {} } };

  applyTheme('dark');
  assert.equal(document.documentElement.dataset.theme, 'dark');

  applyTheme('light');
  assert.equal(document.documentElement.dataset.theme, 'light');

  applyTheme('system');
  assert.equal('theme' in document.documentElement.dataset, false);
});
