import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, readFile, writeFile, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { STARDEW } from '../src/definition.ts';
import { STARDEW_DEFAULTS } from '../src/config.ts';

test('World loads UTF-8 BOM credentials and creates private credentials on Unix', async () => {
  const botDir = await mkdtemp(join(tmpdir(), 'stardew-credentials-'));
  const ctx = { botDir, cfg: { ...STARDEW_DEFAULTS }, timezone: 'UTC' } as any;
  await STARDEW.create(ctx);
  const path = join(botDir, 'data', 'stardew', 'bridge.json');
  const original = await readFile(path, 'utf8');
  assert.equal(JSON.parse(original).token.length, 64);
  if (process.platform !== 'win32') assert.equal((await stat(path)).mode & 0o777, 0o600);
  await writeFile(path, '\uFEFF' + original);
  await STARDEW.create(ctx);
  assert.equal(await readFile(path, 'utf8'), '\uFEFF' + original);
});
