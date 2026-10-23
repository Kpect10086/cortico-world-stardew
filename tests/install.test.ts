import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, mkdir, writeFile, readFile, stat, realpath } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

test('installer handles missing and partial worlds, preserves permissions and reuses credentials', async () => {
  const root = await mkdtemp(join(await realpath(tmpdir()), 'cortico-stardew-install-'));
  const game = join(root, 'game with spaces');
  await mkdir(join(game, 'Mods'), { recursive: true });
  await mkdir(join(game, 'Mods', 'Example Mod'));
  await writeFile(join(game, process.platform === 'win32' ? 'StardewModdingAPI.exe' : 'StardewValley'), 'fixture');
  await writeFile(join(game, 'StardewModdingAPI.dll'), 'fixture');
  const script = process.env.STARDEW_INSTALLER_TEST_PATH ?? fileURLToPath(new URL('../scripts/install.ps1', import.meta.url));
  for (const initial of [{}, { worlds: {} }, { worlds: { stardew: { enabled: false, allowActions: false } } }]) {
    const deploy = await mkdtemp(join(root, 'deploy-'));
    const runtime = join(deploy, 'dedicated runtime');
    await writeFile(join(deploy, 'config.json'), JSON.stringify(initial));
    const install = () => spawnSync('pwsh', ['-NoProfile', '-File', script, '-GameDir', game, '-DeploymentDir', deploy, '-RuntimeDir', runtime], { encoding: 'utf8', windowsHide: true, timeout: 15000 });
    const result = install();
    assert.equal(result.status, 0, result.stderr || result.stdout);
    const config = JSON.parse(await readFile(join(deploy, 'config.json'), 'utf8'));
    assert.equal(config.worlds.stardew.bridgePort, 24983);
    assert.equal(config.worlds.stardew.allowShipping, false);
    if (initial.worlds && 'stardew' in initial.worlds) {
      assert.equal(config.worlds.stardew.enabled, false);
      assert.equal(config.worlds.stardew.allowActions, false);
    }
    const credentialsPath = join(deploy, 'data', 'stardew', 'bridge.json');
    const credentials = await readFile(credentialsPath, 'utf8');
    assert.equal(JSON.parse(credentials).token.length, 64);
    assert.equal(install().status, 0, 'installer must be repeatable');
    assert.equal(await readFile(credentialsPath, 'utf8'), credentials);
    if (process.platform !== 'win32') assert.equal((await stat(credentialsPath)).mode & 0o777, 0o600);
    const client = JSON.parse(await readFile(join(runtime, 'client.json'), 'utf8'));
    assert.equal(client.gameDir, game);
    assert.equal(client.modsDir, join(runtime, 'Mods'));
  }
});

