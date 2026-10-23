import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { once } from 'node:events';
import { StardewBridge } from '../src/bridge.ts';

const token = 'cross-language-local-test-token-32-characters';
const bridge = new StardewBridge(token, () => {});
const port = await bridge.start(0);
const child = spawn('dotnet', [fileURLToPath(new URL('./dotnet/bin/Release/net8.0/TransportCheck.dll', import.meta.url)), String(port), token], { windowsHide: true });
let output = '';
child.stdout.on('data', chunk => { output += chunk; });
child.stderr.on('data', chunk => { output += chunk; });
const done = once(child, 'exit');
try {
  const deadline = Date.now() + 8000;
  while (!bridge.state) {
    assert.ok(Date.now() < deadline && child.exitCode === null, output || 'C# handshake timeout');
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  const result = await bridge.action({ version: bridge.state.version, kind: 'wait', ticks: 2 });
  assert.equal(result.state.version, 'csharp-session:3');
  assert.equal(result.status, 'unchanged');
  const [code] = await done;
  assert.equal(code, 0, output);
  console.log(output.trim());
} finally {
  if (child.exitCode === null) child.kill();
  await bridge.stop();
}
