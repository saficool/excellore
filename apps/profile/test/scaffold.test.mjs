import assert from 'node:assert/strict';
import { test } from 'node:test';
import { id } from '../dist/index.js';

test('workspace package builds and exports its identity', () => {
  assert.equal(id, '@excellore/profile');
});
