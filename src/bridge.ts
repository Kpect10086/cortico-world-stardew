import { createServer, type Server, type Socket } from 'node:net';
import { randomUUID, timingSafeEqual } from 'node:crypto';

export interface GameState {
  version: string;
  ready: boolean;
  farmhand: boolean;
  busy: boolean;
  onboarding?: { canJoin: boolean; stage: string };
  [key: string]: unknown;
}
export interface ActionResult {
  status: 'changed' | 'unchanged' | 'rejected' | 'cancelled';
  detail: string;
  state: GameState;
}
export const ACTION_KINDS = ['join', 'move', 'open_chest', 'face', 'select', 'use_tool', 'interact', 'sleep', 'ship', 'menu_click', 'menu_key', 'wait'] as const;
export function validateAction(args: Record<string, unknown>): void {
  if (Object.keys(args).some(key => !['version','kind','x','y','direction','slot','count','button','key','ticks','name','favoriteThing','gender'].includes(key))) throw new Error('动作包含未知字段。');
  if (typeof args.version !== 'string' || !args.version) throw new Error('先读取 stardew_observe 的 version。');
  if (!ACTION_KINDS.includes(args.kind as typeof ACTION_KINDS[number])) throw new Error('未知动作。');
  const integer = (key: string, min: number, max: number) => {
    if (!Number.isInteger(args[key]) || Number(args[key]) < min || Number(args[key]) > max) throw new Error(`${key} 超出范围。`);
  };
  if (args.kind === 'move') { integer('x', -1, 10000); integer('y', -1, 10000); }
  if (args.kind === 'open_chest') { integer('x', 0, 10000); integer('y', 0, 10000); }
  if (args.kind === 'use_tool' && args.slot !== undefined) integer('slot', 0, 35);
  if (['face', 'use_tool', 'interact'].includes(String(args.kind))) integer('direction', 0, 3);
  if (args.kind === 'select') integer('slot', 0, 35);
  if (args.kind === 'ship') { integer('slot', 0, 35); integer('count', 1, 99); }
  if (args.kind === 'menu_click') integer('button', 0, 199);
  if (args.kind === 'menu_key' && !['Escape', 'Enter', 'Space', 'Up', 'Down', 'Left', 'Right', 'E'].includes(String(args.key))) throw new Error('不支持此菜单按键。');
  if (args.kind === 'wait') integer('ticks', 1, 600);
  if (args.kind === 'join') {
    for (const key of ['name','favoriteThing']) if (args[key] !== undefined && (typeof args[key] !== 'string' || !String(args[key]).trim() || String(args[key]).length > 20 || /[\[\]\x00-\x1f\x7f]/.test(String(args[key])))) throw new Error(`${key} 必须为1至20个字符，且不能包含控制字符或方括号。`);
    if (args.gender !== undefined && !['male','female'].includes(String(args.gender))) throw new Error('gender 必须为 male 或 female。');
  }
}

export class StardewBridge {
  state: GameState | null = null;
  private server?: Server;
  private socket?: Socket;
  private sockets = new Set<Socket>();
  private pending?: { id: string; finish: (error?: Error, result?: ActionResult) => void };
  private uncertain = false;

  constructor(private readonly token: string, private readonly onState: (state: GameState | null, actionResult?: boolean) => void,
    private readonly timeoutMs?: number) {}

  async start(port: number): Promise<number> {
    if (this.server) throw new Error('桥接已启动。');
    const server = createServer(socket => this.accept(socket));
    this.server = server;
    try {
      await new Promise<void>((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, '127.0.0.1', () => { server.off('error', reject); resolve(); });
      });
      return (server.address() as { port: number }).port;
    } catch (error) {
      this.server = undefined;
      server.close();
      throw error;
    }
  }

  async stop(): Promise<void> {
    this.socket?.write(JSON.stringify({ type: 'stop' }) + '\n');
    this.pending?.finish(new Error('World 已停止；已提交动作的结果可能未知，重新观察后再决定。'));
    for (const socket of this.sockets) socket.destroy();
    if (this.server) await new Promise<void>(resolve => this.server!.close(() => resolve()));
    this.server = undefined;
    this.state = null;
  }

  cancel(): void {
    if (this.pending) this.socket?.write(JSON.stringify({ type: 'cancel', id: this.pending.id }) + '\n');
    else this.socket?.write(JSON.stringify({ type: 'stop' }) + '\n');
  }

  async action(args: Record<string, unknown>, signal?: AbortSignal): Promise<ActionResult> {
    validateAction(args);
    if (!this.socket || !this.state) throw new Error('bot客户端未连接。请用专用入口启动 SMAPI，并加入你的农场。');
    if (this.pending || this.uncertain || this.state.busy) throw new Error('已有动作执行中或结果未知；等候新观察，不要重发。');
    const canJoin = !this.state.ready && this.state.onboarding?.canJoin === true && ['title','connecting','choose_farmer','create_character'].includes(this.state.onboarding.stage);
    if (args.kind === 'join' ? !canJoin : !this.state.ready || !this.state.farmhand) throw new Error('当前阶段不允许此动作；join 仅用于客户端加入流程，游戏动作仅允许已加入的客机角色，禁止控制房主。');
    if (args.version !== this.state.version) throw new Error('观察已过期；重新 stardew_observe 后决定。');
    signal?.throwIfAborted();
    const id = randomUUID();
    return new Promise<ActionResult>((resolve, reject) => {
      const abort = () => this.socket?.write(JSON.stringify({ type: 'cancel', id }) + '\n');
      const timer = setTimeout(() => {
        this.uncertain = true;
        abort();
        this.pending?.finish(new Error('动作回执超时，结果未知；已请求停止，不自动重发。'));
      }, this.timeoutMs ?? (['move', 'open_chest'].includes(String(args.kind)) ? 65000 : 25000));
      this.pending = { id, finish: (error, result) => {
        clearTimeout(timer);
        signal?.removeEventListener('abort', abort);
        this.pending = undefined;
        if (error) reject(error); else resolve(result!);
      } };
      signal?.addEventListener('abort', abort, { once: true });
      this.socket!.write(JSON.stringify({ type: 'action', id, ...args }) + '\n', error => {
        if (error && this.pending?.id === id) this.pending.finish(new Error('提交时连接中断，结果未知；不自动重发。'));
      });
    });
  }

  private accept(socket: Socket): void {
    this.sockets.add(socket);
    socket.setEncoding('utf8');
    socket.setNoDelay(true);
    let authenticated = false;
    let buffer = '';
    const timer = setTimeout(() => socket.destroy(), 5000);
    socket.on('error', () => socket.destroy());
    socket.on('close', () => {
      clearTimeout(timer);
      this.sockets.delete(socket);
      if (this.socket !== socket) return;
      this.socket = undefined;
      this.state = null;
      this.uncertain = false;
      this.pending?.finish(new Error('桥接断线，已提交动作结果未知；重新连接并观察后再决定，不自动重发。'));
      this.onState(null);
    });
    socket.on('data', chunk => {
      buffer += chunk;
      while (buffer.includes('\n')) {
        const index = buffer.indexOf('\n');
        const line = buffer.slice(0, index); buffer = buffer.slice(index + 1);
        if (Buffer.byteLength(line, 'utf8') > 262144) { socket.destroy(); return; }
        try {
          const message = JSON.parse(line);
          if (!authenticated) {
            const supplied = Buffer.from(typeof message.token === 'string' ? message.token : '');
            const expected = Buffer.from(this.token);
            if (message.type !== 'hello' || message.protocol !== 1 || supplied.length !== expected.length || !timingSafeEqual(supplied, expected) || this.socket) { socket.destroy(); return; }
            authenticated = true;
            clearTimeout(timer);
            this.socket = socket;
            socket.write('{"type":"welcome","protocol":1}\n');
            continue;
          }
          if (message.type !== 'state' && message.type !== 'result') throw new Error('Unknown message');
          const state = message.state;
          if (!state || typeof state.version !== 'string' || typeof state.ready !== 'boolean' || typeof state.farmhand !== 'boolean' || typeof state.busy !== 'boolean') throw new Error('Invalid state');
          if (message.type === 'result' && (!['changed','unchanged','rejected','cancelled'].includes(message.status) || typeof message.detail !== 'string')) throw new Error('Invalid result');
          this.state = state;
          if (!state.busy) this.uncertain = false;
          if (message.type === 'result' && state.busy) throw new Error('Result is not stable');
          if (message.type === 'result' && message.id === this.pending?.id) this.pending!.finish(undefined, { status: message.status, detail: message.detail, state });
          this.onState(state, message.type === 'result');
        } catch { socket.destroy(); return; }
      }
      // TCP can coalesce multiple valid frames into one data event. Limit each
      // frame (including an unfinished one), not the size of the whole batch.
      if (Buffer.byteLength(buffer, 'utf8') > 262144) socket.destroy();
    });
  }
}
