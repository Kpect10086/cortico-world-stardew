import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createConnection } from 'node:net';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { StardewBridge, validateAction, type GameState } from '../src/bridge.ts';
import { StardewWorld } from '../src/world.ts';
import { STARDEW_DEFAULTS } from '../src/config.ts';
import type { WorldHost } from 'cortico/core/types.ts';

const token = 'local-test-token-that-is-at-least-32-characters';
const state = (version = 'game-session:1', busy = false): GameState => ({ version, busy, ready: true, farmhand: true, player: { x: 1, y: 1 } });
const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
async function until(check: () => boolean) {
  const end = Date.now() + 1500;
  while (!check()) { assert.ok(Date.now() < end, 'condition timed out'); await sleep(5); }
}
function peer(port: number) {
  const socket = createConnection({ host: '127.0.0.1', port });
  const lines: any[] = [];
  const reader = createInterface({ input: socket });
  reader.on('line', line => lines.push(JSON.parse(line)));
  const send = (message: unknown) => socket.write(JSON.stringify(message) + '\n');
  return { socket, lines, send };
}
async function setup(t: { after: (fn: () => unknown) => void }, timeoutMs = 1000) {
  const observations: (GameState | null)[] = [];
  const bridge = new StardewBridge(token, s => observations.push(s), timeoutMs);
  const port = await bridge.start(0);
  const game = peer(port);
  t.after(async () => { game.socket.destroy(); await bridge.stop(); });
  await once(game.socket, 'connect');
  game.send({ type: 'hello', protocol: 1, token });
  await until(() => game.lines.some(m => m.type === 'welcome'));
  game.send({ type: 'state', state: state() });
  await until(() => bridge.state !== null);
  return { bridge, game, port, observations };
}

test('rejects invalid actions and protocol field injection', () => {
  assert.doesNotThrow(() => validateAction({ version: 'v', kind: 'move', x: -1, y: 54 }));
  assert.doesNotThrow(() => validateAction({ version: 'v', kind: 'ship', slot: 2, count: 1 }));
  assert.doesNotThrow(() => validateAction({ version: 'v', kind: 'open_chest', x: 70, y: 17 }));
  assert.doesNotThrow(() => validateAction({ version: 'v', kind: 'use_tool', slot: 1, direction: 0 }));
  for (const action of [
    { version: 'v', kind: 'open_chest', x: -1, y: 17 },
    { version: 'v', kind: 'use_tool', slot: 36, direction: 0 },
    { version: 'v', kind: 'ship', slot: 2, count: 0 },
    { version: 'v', kind: 'ship', slot: 2, count: 100 },
    { version: 'v', kind: 'move', x: -2, y: 54 },
    { version: 'v', kind: 'move', x: 1.5, y: 0 },
    { version: 'v', kind: 'face', direction: 4 },
    { version: 'v', kind: 'select', slot: -1 },
    { version: 'v', kind: 'menu_key', key: 'Delete' },
    { version: 'v', kind: 'wait', ticks: 601 },
    { version: 'v', kind: 'wait', ticks: 2, type: 'stop' },
    { version: 'v', kind: 'join', name: '[74]' },
    { version: 'v', kind: 'join', favoriteThing: '  ' },
    { version: 'v', kind: 'join', gender: 'invalid' },
  ]) assert.throws(() => validateAction(action));
  validateAction({ version: 'v', kind: 'move', x: 10, y: 20 });
  validateAction({ version: 'v', kind: 'join', name: 'bot', favoriteThing: '星露谷', gender: 'female' });
});

test('join is allowed before world readiness, ordinary gameplay remains blocked', async t => {
  const { bridge, game } = await setup(t);
  game.send({ type: 'state', state: { ...state('title:1'), ready: false, farmhand: false, onboarding: { canJoin: true, stage: 'title' } } });
  await until(() => bridge.state?.version === 'title:1');
  await assert.rejects(bridge.action({ version: 'title:1', kind: 'move', x: 1, y: 1 }), /当前阶段/);
  const result = bridge.action({ version: 'title:1', kind: 'join' });
  await until(() => game.lines.some(m => m.type === 'action'));
  const command = game.lines.find(m => m.type === 'action');
  assert.equal(command.kind, 'join');
  game.send({ type: 'result', id: command.id, status: 'changed', detail: 'joined', state: state('farm:1') });
  assert.equal((await result).state.farmhand, true);
  await assert.rejects(bridge.action({ version: 'farm:1', kind: 'join' }), /当前阶段/);
});

test('join cannot run in a host game or a split-screen prompt', async t => {
  const { bridge, game } = await setup(t);
  for (const stage of ['host_loaded','unsupported_split_screen']) {
    game.send({ type: 'state', state: { ...state(stage), ready: false, farmhand: false, onboarding: { canJoin: true, stage } } });
    await until(() => bridge.state?.version === stage);
    await assert.rejects(bridge.action({ version: stage, kind: 'join' }), /房主/);
  }
  assert.equal(game.lines.filter(m => m.type === 'action').length, 0);
});
test('loopback listener authenticates and rejects a second client', async t => {
  const { bridge, port } = await setup(t);
  const bad = peer(port); t.after(() => bad.socket.destroy());
  const closed = once(bad.socket, 'close');
  await once(bad.socket, 'connect');
  bad.send({ type: 'hello', protocol: 1, token: 'wrong' });
  await closed;
  assert.ok(bridge.state);
  const duplicate = peer(port); t.after(() => duplicate.socket.destroy());
  const duplicateClosed = once(duplicate.socket, 'close');
  await once(duplicate.socket, 'connect');
  duplicate.send({ type: 'hello', protocol: 1, token });
  await duplicateClosed;
  assert.equal(duplicate.lines.length, 0);
});
test('one action waits for matching result, intermediate state cannot complete it', async t => {
  const { bridge, game, observations } = await setup(t);
  let completed = false;
  const result = bridge.action({ version: state().version, kind: 'wait', ticks: 3 }).then(value => { completed = true; return value; });
  await until(() => game.lines.some(m => m.type === 'action'));
  const command = game.lines.find(m => m.type === 'action');
  game.send({ type: 'state', state: { ...state('game-session:2', true), ready: false } });
  await until(() => bridge.state?.busy === true);
  assert.equal(completed, false);
  await assert.rejects(bridge.action({ version: 'game-session:2', kind: 'wait', ticks: 1 }), /已有动作/);
  game.send({ type: 'result', id: 'unrelated', status: 'unchanged', detail: 'unrelated', state: state('game-session:3') });
  await until(() => bridge.state?.version === 'game-session:3');
  assert.equal(completed, false);
  game.send({ type: 'result', id: command.id, status: 'changed', detail: 'done', state: state('game-session:4') });
  assert.equal((await result).state.version, 'game-session:4');
  assert.equal(observations.at(-1)?.version, 'game-session:4');
});
test('rejects stale observations and host control before sending', async t => {
  const { bridge, game } = await setup(t);
  await assert.rejects(bridge.action({ version: 'stale', kind: 'wait', ticks: 1 }), /过期/);
  game.send({ type: 'state', state: { ...state('host:1'), farmhand: false } });
  await until(() => bridge.state?.farmhand === false);
  await assert.rejects(bridge.action({ version: 'host:1', kind: 'wait', ticks: 1 }), /房主/);
  assert.equal(game.lines.filter(m => m.type === 'action').length, 0);
});
test('disconnect reports unknown outcome and permits a fresh authenticated connection', async t => {
  const { bridge, game, port } = await setup(t);
  const result = assert.rejects(bridge.action({ version: state().version, kind: 'wait', ticks: 3 }), /结果未知/);
  await until(() => game.lines.some(m => m.type === 'action'));
  game.socket.destroy();
  await result;
  assert.equal(bridge.state, null);
  const reconnected = peer(port); t.after(() => reconnected.socket.destroy());
  await once(reconnected.socket, 'connect');
  reconnected.send({ type: 'hello', protocol: 1, token });
  await until(() => reconnected.lines.some(m => m.type === 'welcome'));
  reconnected.send({ type: 'state', state: state('new-session:1') });
  await until(() => bridge.state?.version === 'new-session:1');
  assert.equal(reconnected.lines.filter(m => m.type === 'action').length, 0);
});
test('timeout sends cancellation without retry, busy state still blocks the next action', async t => {
  const { bridge, game } = await setup(t, 80);
  const result = assert.rejects(bridge.action({ version: state().version, kind: 'wait', ticks: 3 }), /结果未知/);
  await until(() => game.lines.some(m => m.type === 'action'));
  game.send({ type: 'state', state: state('game-session:2', true) });
  await result;
  await until(() => game.lines.some(m => m.type === 'cancel'));
  await assert.rejects(bridge.action({ version: 'game-session:2', kind: 'wait', ticks: 1 }), /结果未知/);
  assert.equal(game.lines.filter(m => m.type === 'action').length, 1);
});
test('AbortSignal sends matching cancellation and awaits the stable receipt', async t => {
  const { bridge, game } = await setup(t);
  const controller = new AbortController();
  const result = bridge.action({ version: state().version, kind: 'wait', ticks: 3 }, controller.signal);
  await until(() => game.lines.some(m => m.type === 'action'));
  const command = game.lines.find(m => m.type === 'action');
  controller.abort();
  await until(() => game.lines.some(m => m.type === 'cancel' && m.id === command.id));
  game.send({ type: 'result', id: command.id, status: 'cancelled', detail: 'stopped', state: state('game-session:2') });
  assert.equal((await result).status, 'cancelled');
});
test('malformed and oversized frames close the bridge', async t => {
  for (const payload of ['not-json\n', 'x'.repeat(262145)]) {
    const { bridge, game } = await setup(t);
    game.socket.write(payload);
    await until(() => bridge.state === null);
  }
});
test('World checks live permissions and pause, publishes post-action state, cancels on permission withdrawal', async t => {
  const cfg = { ...STARDEW_DEFAULTS, bridgePort: 0, allowActions: false };
  let paused = false;
  const events: any[] = [];
  const host = { isPaused: () => paused, pushEvent: async (event: unknown, options: unknown) => { events.push({ event, options }); return event; }, log: { warn: () => {} } } as unknown as WorldHost;
  const world = new StardewWorld(cfg, 'Asia/Singapore', token);
  // The World lifecycle opens a real port; this test uses the same bridge on a reserved port.
  const reservation = new StardewBridge(token, () => {});
  cfg.bridgePort = await reservation.start(0); await reservation.stop();
  await world.start(host); t.after(() => world.stop());
  const game = peer(cfg.bridgePort); t.after(() => game.socket.destroy());
  await once(game.socket, 'connect'); game.send({ type: 'hello', protocol: 1, token });
  await until(() => game.lines.some(m => m.type === 'welcome'));
  game.send({ type: 'state', state: { ...state(), inventory: [{ slot: 1, item: { name: 'tool' } }], map: { chests: Array.from({ length: 50 }, (_, i) => ({ chestId: `farm:${i},1`, x: i, y: 1 })) } } });
  await until(() => world.bridge.state !== null);
  const tools = world.tools();
  const act = tools.find(tool => tool.name === 'stardew_act')!;
  const observe = tools.find(tool => tool.name === 'stardew_observe')!;
  const fullObservation = JSON.parse(await observe.handler({}, {} as any) as string);
  const eventObservation = JSON.parse(events[0].event.text);
  assert.equal(fullObservation.state.map.chests.length, 50, 'observe retains full map data');
  assert.equal(eventObservation.summaryOnly, true);
  assert.equal(eventObservation.state.version, fullObservation.state.version);
  assert.equal(eventObservation.state.map, undefined, 'events do not repeat the full map');
  assert.equal(eventObservation.state.inventory, undefined, 'events do not repeat the full inventory');
  assert.ok(events[0].event.text.length < JSON.stringify(fullObservation).length / 2, 'event compaction is measurable');
  await assert.rejects(act.handler({ version: state().version, kind: 'wait', ticks: 3 }, {} as any), /权限关闭/);
  cfg.allowActions = true;
  await assert.rejects(act.handler({ version: state().version, kind: 'ship', slot: 2, count: 1 }, {} as any), /出售权限/);
  assert.equal(JSON.parse(await observe.handler({}, {} as any) as string).allowActions, true);
  paused = true;
  await assert.rejects(act.handler({ version: state().version, kind: 'wait', ticks: 3 }, {} as any), /暂停/);
  paused = false;
  await sleep(550);
  const result = act.handler({ version: state().version, kind: 'wait', ticks: 3 }, {} as any);
  await until(() => game.lines.some(m => m.type === 'action'));
  const command = game.lines.find(m => m.type === 'action');
  cfg.allowActions = false;
  await until(() => game.lines.some(m => m.type === 'cancel' && m.id === command.id));
  game.send({ type: 'result', id: command.id, status: 'cancelled', detail: 'stopped', state: { ...state('game-session:2'), player: { x: 2, y: 1 } } });
  assert.equal(JSON.parse(await result as string).status, 'cancelled');
  await until(() => events.length === 2);
  assert.equal(events[1].event.type, 'stardew.state');
  assert.equal(events[1].options.trigger, 'piggyback');
  assert.equal(events[0].options.trigger, 'piggyback');
  cfg.allowActions = true;
  cfg.characterName = 'chosen-farmhand';
  game.send({ type: 'state', state: { ...state('title:1'), ready: false, farmhand: false, onboarding: { stage: 'title', canJoin: true } } });
  await until(() => world.bridge.state?.version === 'title:1');
  await assert.rejects(act.handler({ version: 'title:1', kind: 'join' }, {} as any), /完整观察/);
  await observe.handler({}, {} as any);
  const joining = act.handler({ version: 'title:1', kind: 'join' }, {} as any);
  await until(() => game.lines.some(m => m.type === 'action' && m.kind === 'join'));
  const join = game.lines.find(m => m.type === 'action' && m.kind === 'join');
  assert.equal(join.name, 'chosen-farmhand');
  game.send({ type: 'result', id: join.id, status: 'changed', detail: 'joined', state: state('farm:1') });
  assert.equal(JSON.parse(await joining as string).state.ready, true);
  const beforeNight = events.length;
  game.send({ type: 'state', state: { ...state('farm:2'), date: { year: 1, season: 'spring', day: 1, time: 2200 } } });
  await until(() => events.length === beforeNight + 1);
  assert.match(JSON.parse(events.at(-1).event.text).reminder, /22:00/);
  game.send({ type: 'state', state: { ...state('farm:3'), date: { year: 1, season: 'spring', day: 1, time: 2210 } } });
  await sleep(30);
  assert.equal(events.length, beforeNight + 1, 'same night stage remains throttled');
  game.send({ type: 'state', state: { ...state('farm:4'), date: { year: 1, season: 'spring', day: 1, time: 2400 } } });
  await until(() => events.length === beforeNight + 2);
  game.send({ type: 'state', state: { ...state('farm:5'), player: { timeWentToBed: 2410 }, date: { year: 1, season: 'spring', day: 1, time: 2500 } } });
  await sleep(30);
  assert.equal(events.length, beforeNight + 2, 'confirmed sleeper gets no urgent return-home wake');
});
