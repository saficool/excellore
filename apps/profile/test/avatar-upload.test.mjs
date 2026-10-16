import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import {
  AvatarInputError,
  prepareAvatar,
} from '../app/account/prepare-avatar.ts';

const originalBitmap = globalThis.createImageBitmap;
const originalDocument = globalThis.document;
after(() => {
  globalThis.createImageBitmap = originalBitmap;
  globalThis.document = originalDocument;
});

test('a small JPEG is resized and converted before upload', async () => {
  let closed = false;
  let drawn = false;
  let canvas;
  globalThis.createImageBitmap = async () => ({
    width: 1920,
    height: 1080,
    close: () => {
      closed = true;
    },
  });
  globalThis.document = {
    createElement: () => {
      canvas = {
        width: 0,
        height: 0,
        getContext: () => ({ drawImage: () => (drawn = true) }),
        toBlob: (resolve) =>
          resolve(new Blob([new Uint8Array(100)], { type: 'image/webp' })),
      };
      return canvas;
    },
  };

  const input = new File([new Uint8Array(100)], 'picture.jpg', {
    type: 'image/jpeg',
  });
  const prepared = await prepareAvatar(input);

  assert.equal(prepared.type, 'image/webp');
  assert.equal(prepared.size, 100);
  assert.equal(canvas.width, 512);
  assert.equal(canvas.height, 288);
  assert.equal(drawn, true);
  assert.equal(closed, true);
});

test('unsupported input is rejected before decoding', async () => {
  globalThis.createImageBitmap = async () => {
    throw new Error('Unexpected decode');
  };
  await assert.rejects(
    prepareAvatar(
      new File(['image bytes'], 'picture.gif', { type: 'image/gif' }),
    ),
    AvatarInputError,
  );
});
