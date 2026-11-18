// Copies non-TypeScript runtime assets (EJS views, CSS, client JS) next to the compiled output.
import { cp, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
for (const dir of ['views', 'public']) {
  const target = path.join(root, 'dist', dir);
  await rm(target, { recursive: true, force: true });
  await cp(path.join(root, 'src', dir), target, { recursive: true });
}
console.log('Copied views and public assets to dist/');
