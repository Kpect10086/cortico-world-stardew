type WorldHost = FrameworkWorldHost & { isPaused?(): boolean };
import { fileURLToPath } from 'node:url';
import type { ToolDef, World, WorldHost as FrameworkWorldHost, WorldWebUiDecl } from 'cortico/core/types.ts';
import { nowIso } from 'cortico/core/util.ts';
import { StardewBridge, type GameState, ACTION_KINDS } from './bridge.ts';
import { STARDEW_CONFIG_GROUP, type StardewConfigSection } from './config.ts';

export const STARDEW_TOOL_DECLS: ReadonlyArray<Omit<ToolDef, 'handler'>> = [
  { name: 'stardew_observe', description: '读取独立客户端的加入/建角阶段、联机角色、附近地图、作物、物品、玩家和菜单；返回最新 version 及实时操作权限。', tags: ['read'],
    parameters: { type: 'object', properties: {}, additionalProperties: false } },
  { name: 'stardew_act', description: '执行一个动作并等待回执，所有动作带最新version。join通过127.0.0.1加入房主，优先同名角色，否则用空闲小屋建角，name默认配置值；favoriteThing/gender仅新建有效。move用当前地图绝对格坐标，原生寻路一次到目标，无需每10格分段；open_chest(x,y)一次走到map.chests指定箱子旁并打开，menu.chest可核对身份；direction上0右1下2左3；select.slot从0开始。interact打开相邻箱子或对话NPC；menu_click.button取可见按钮索引，role区分container箱子和inventory背包；普通NPC对话用menu_key Enter继续/关闭。use_tool可同时指定slot选中并使用工具、种子或食物，无需先select，食物需要弹窗确认。sleep在床sleepTile打开确认，22:00返家、02:00前点击Yes。ship需在普通出货箱菜单中指定slot和count(1..99)，出售上限为物品shippingMax，只卖普通品质、单价≤300的水果/蔬菜/花，每叠留5个，任务/特殊物品/种子/工具/材料不能卖；还需allowShipping权限。',
    tags: ['act'], barrierAfter: true, interruptible: true,
    parameters: { type: 'object', additionalProperties: false, required: ['version','kind'], properties: {
      version: { type: 'string' }, kind: { type: 'string', enum: [...ACTION_KINDS] },
      x: { type: 'integer', minimum: -1 }, y: { type: 'integer', minimum: -1 },
      direction: { type: 'integer', minimum: 0, maximum: 3 }, slot: { type: 'integer', minimum: 0, maximum: 35 },
      button: { type: 'integer', minimum: 0, maximum: 199 },
      count: { type: 'integer', minimum: 1, maximum: 99 },
      key: { type: 'string', enum: ['Escape','Enter','Space','Up','Down','Left','Right','E'] },
      ticks: { type: 'integer', minimum: 1, maximum: 600 },
      name: { type: 'string', minLength: 1, maxLength: 20 }, favoriteThing: { type: 'string', minLength: 1, maxLength: 20 },
      gender: { type: 'string', enum: ['male','female'] },
    } } },
  { name: 'stardew_stop', description: '立即请求停止自己的移动或等待动作。可以在操作权限关闭时使用。', tags: ['act'], barrierAfter: true,
    parameters: { type: 'object', properties: {}, additionalProperties: false } },
];

export class StardewWorld implements World {
  readonly id = 'stardew';
  private host?: WorldHost;
  readonly bridge: StardewBridge;
  private lastState = '';
  private lastWakeAt = 0;
  private lastNightStage = '';
  private timer?: ReturnType<typeof setInterval>;
  private lastAllowed = false;
  private stopped = false;
  private observedAt = 0;
  private presentedVersion = '';
  constructor(private readonly cfg: StardewConfigSection, private readonly timezone: string, token: string) {
    this.bridge = new StardewBridge(token, (state, actionResult) => this.onState(state, actionResult));
  }
  envPromptVars() { return { 'stardew.characterName': this.cfg.characterName || 'bot' }; }
  tools(): ToolDef[] {
    return STARDEW_TOOL_DECLS.map(decl => ({ ...decl, handler: async (args, ctx) => {
      if (decl.name === 'stardew_observe') {
        this.presentedVersion = this.bridge.state?.version ?? '';
        return JSON.stringify({ connected: !!this.bridge.state, allowActions: this.allowed(), allowShipping: !!this.cfg.allowShipping, observedAt: this.observedAt ? new Date(this.observedAt).toISOString() : null, state: this.bridge.state });
      }
      if (decl.name === 'stardew_stop') { this.bridge.cancel(); return '已请求停止；以随后观察和回执确认结果。'; }
      if (!this.allowed()) throw new Error('当前动作权限关闭或 Cortico 已暂停。请查看 stardew_observe。');
      if (args.kind === 'ship' && !this.cfg.allowShipping) throw new Error('出售权限关闭，只能查看出货箱。');
      if (Date.now() - this.observedAt > 12000) throw new Error('游戏状态超过12秒未更新；等待客户端恢复，不提交动作。');
      if (args.version !== this.presentedVersion) throw new Error('此版本尚未返回完整观察；先stardew_observe，不要把摘要版本与旧物品/按钮索引拼接。');
      this.lastAllowed = true;
      const action = args.kind === 'join' ? { ...args, name: args.name ?? this.cfg.characterName ?? 'bot' } : args;
      const result = await this.bridge.action(action, ctx.signal);
      this.presentedVersion = result.state.version;
      return JSON.stringify(result);
    } }));
  }
  webUi(): WorldWebUiDecl {
    return { label: '星露谷联机', config: [STARDEW_CONFIG_GROUP],
      lamps: [{ label: 'bot客户端', state: this.bridge.state?.ready && this.bridge.state.farmhand ? 'online' : this.bridge.state ? 'loading' : 'offline', hint: this.bridge.state ? '仅控制联机客机角色' : '等待专用 SMAPI 客户端连接' }],
      badges: [{ label: '操作', value: this.allowed() ? '允许' : '暂停', tone: this.allowed() ? 'on' : 'off' }],
      promptDocs: [{ key: 'worlds.stardew.envPrompt', title: '星露谷环境提示词', description: '独立客机角色、公开状态及动作回执的使用约束。', path: fileURLToPath(new URL('./ENV_PROMPT.md', import.meta.url)), role: 'envPrompt', vars: [{ name: 'stardew.characterName', description: '配置中要使用的农场工名字。' }] }],
    };
  }
  async start(host: WorldHost): Promise<void> {
    this.host = host;
    this.stopped = false;
    await this.bridge.start(this.cfg.bridgePort);
    this.timer = setInterval(() => {
      const allowed = this.allowed();
      if (this.lastAllowed && !allowed) this.bridge.cancel();
      this.lastAllowed = allowed;
      this.publish();
    }, 500);
    this.timer.unref();
  }
  async stop(): Promise<void> {
    this.stopped = true;
    clearInterval(this.timer);
    await this.bridge.stop();
    this.host = undefined;
  }
  private allowed() { return this.cfg.allowActions && !this.host?.isPaused?.() && !this.stopped && !this.bridge.state?.localPaused; }
  private onState(state: GameState | null, actionResult = false) {
    this.observedAt = state ? Date.now() : 0;
    if (!state) { this.lastState = ''; this.lastWakeAt = 0; this.lastNightStage = ''; this.presentedVersion = ''; }
    this.publish(actionResult);
  }
  private publish(actionResult = false) {
    const state = this.bridge.state;
    if (!this.host || this.stopped || !state || state.busy || (!(state.ready && state.farmhand) && !state.onboarding?.canJoin)) return;
    const { version, ...publicState } = state;
    const signature = JSON.stringify(publicState);
    if (signature === this.lastState) return;
    const date = state.date as { year?: number; season?: string; day?: number; time?: number } | undefined;
    const player = state.player as { timeWentToBed?: number } | undefined;
    const time = date?.time ?? 0;
    const nightStage = time >= 2200 && (player?.timeWentToBed ?? 0) <= 0 ? `${date?.year}/${date?.season}/${date?.day}/${time >= 2500 ? 2500 : time >= 2400 ? 2400 : 2200}` : '';
    const returnHomeDue = nightStage !== '' && nightStage !== this.lastNightStage;
    const interval = Math.max(10, this.cfg.wakeIntervalSec) * 1000;
    if (!actionResult && !returnHomeDue && Date.now() - this.lastWakeAt < interval) return;
    this.lastState = signature;
    this.lastNightStage = nightStage;
    this.lastWakeAt = Date.now();
    // 完整状态由观察/动作回执提供；事件不重复灌入地图、背包和菜单按钮。
    const menu = state.menu as Record<string, unknown> | null | undefined;
    const eventState = { version, ready: state.ready, farmhand: state.farmhand, busy: state.busy, localPaused: state.localPaused,
      date: state.date, player: state.player, onboarding: state.onboarding,
      menu: menu ? { kind: menu.kind, text: menu.text, speaker: menu.speaker, transitioning: menu.transitioning, sleepReady: menu.sleepReady, chest: menu.chest } : null };
    void this.host.pushEvent({ type: 'stardew.state', source: this.id, senderKey: this.id, ts: nowIso(this.timezone),
      text: JSON.stringify({ allowActions: this.allowed(), allowShipping: !!this.cfg.allowShipping, summaryOnly: true,
        ...(returnHomeDue ? { reminder: '晚上22:00起停止工作并立即返家，凌晨02:00前点击Yes确认睡觉；inBed本身不算完成。' } : {}), state: eventState }) }, { trigger: !actionResult && this.allowed() ? 'debounce' : 'piggyback' })
      .catch(error => this.host?.log.warn(String(error)));
  }
}

