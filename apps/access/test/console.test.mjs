import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sectionAllowed, visibleSections } from '../lib/console.ts';

test('console navigation contains only granted sections', () => {
  const granted = visibleSections(['access.app.access', 'access.role.read']);
  assert.deepEqual(
    granted.map((item) => item.id),
    ['my-access', 'roles', 'ownership'],
  );
  assert.equal(sectionAllowed('members', ['access.app.access']), false);
  assert.equal(sectionAllowed('support-grants', ['access.app.access']), false);
  assert.equal(sectionAllowed('my-access', ['access.app.access']), true);
});
