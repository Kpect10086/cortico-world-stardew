import { mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { describeTools, type WorldDefinition } from 'cortico/world.ts';
import { STARDEW_DEFAULTS, type StardewConfigSection } from './config.ts';
import { STARDEW_TOOL_DECLS, StardewWorld } from './world.ts';

export const STARDEW: WorldDefinition<StardewConfigSection> = {
  id: 'stardew', label: '星露谷联机', defaults: () => ({ ...STARDEW_DEFAULTS }),
  create(ctx) {
    const directory = join(ctx.botDir, 'data', 'stardew');
    const path = join(directory, 'bridge.json');
    mkdirSync(directory, { recursive: true, mode: 0o700 });
    if (!existsSync(path)) writeFileSync(path, JSON.stringify({ token: randomBytes(32).toString('hex') }), { flag: 'wx', mode: 0o600 });
    const { token } = JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));
    if (typeof token !== 'string' || token.length < 32) throw new Error('星露谷桥接凭据无效，请检查部署 data/stardew/bridge.json。');
    return new StardewWorld(ctx.cfg, ctx.timezone, token);
  },
  describe: {
    tools: describeTools(STARDEW_TOOL_DECLS, {
      stardew_observe: { zh: '查看独立联机角色和附近状态。', en: 'Observe the farmhand and nearby game state.' },
      stardew_act: { zh: '执行一个联机角色动作并等待回执。', en: 'Perform one farmhand action and await its result.' },
      stardew_stop: { zh: '请求停止角色当前动作。', en: 'Request cancellation of the current action.' },
    }),
    events: [{ type: 'stardew.state', description: { zh: '联机角色公开状态发生变化。', en: 'The farmhand public state changed.' } }],
  },
};
