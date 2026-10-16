const maxUploadBytes = 700 * 1024;
const maxSourceBytes = 10 * 1024 * 1024;
const allowedTypes = new Set(['image/png', 'image/jpeg', 'image/webp']);

export class AvatarInputError extends Error {}

export async function prepareAvatar(file: File): Promise<Blob> {
  if (!allowedTypes.has(file.type))
    throw new AvatarInputError('Choose a PNG, JPEG, or WebP image.');
  if (file.size < 16 || file.size > maxSourceBytes)
    throw new AvatarInputError('Choose an image between 16 bytes and 10 MB.');
  if (typeof createImageBitmap !== 'function') {
    if (file.size <= maxUploadBytes) return file;
    throw new AvatarInputError('Choose an image under 700 KB in this browser.');
  }

  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch {
    throw new AvatarInputError('The selected image could not be opened.');
  }
  try {
    if (
      bitmap.width < 1 ||
      bitmap.height < 1 ||
      bitmap.width * bitmap.height > 16_000_000
    )
      throw new AvatarInputError('Choose an image smaller than 16 megapixels.');

    const scale = Math.min(1, 512 / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const context = canvas.getContext('2d');
    if (!context)
      throw new AvatarInputError('This browser could not prepare the image.');
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    const prepared = await new Promise<Blob | null>((resolve) =>
      canvas.toBlob(resolve, 'image/webp', 0.82),
    );
    if (
      !prepared ||
      prepared.type !== 'image/webp' ||
      prepared.size < 16 ||
      prepared.size > maxUploadBytes
    )
      throw new AvatarInputError('This image could not be reduced for upload.');
    return prepared;
  } finally {
    bitmap.close();
  }
}
