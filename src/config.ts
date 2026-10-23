import type { ConfigGroup } from 'cortico/core/types.ts';

export interface StardewConfigSection {
  enabled: boolean;
  allowActions: boolean;
  allowShipping: boolean;
  bridgePort: number;
  wakeIntervalSec: number;
  characterName: string;
}

export const STARDEW_DEFAULTS: StardewConfigSection = {
  enabled: false, allowActions: false, allowShipping: false, bridgePort: 24983, wakeIntervalSec: 30, characterName: 'bot',
};

export const STARDEW_CONFIG_GROUP: ConfigGroup = {
  id: 'world:stardew', owner: 'world:stardew',
  schema: {
    type: 'object', title: '星露谷联机',
    properties: {
      'worlds.stardew.allowActions': { type: 'boolean', title: '允许操作独立角色', 'x-hot': true },
      'worlds.stardew.allowShipping': { type: 'boolean', title: '允许保留库存后出售普通收获物', description: '只卖普通品质、单价≤300的水果/蔬菜/花；每叠至少保留5个。', 'x-hot': true },
      'worlds.stardew.characterName': { type: 'string', title: '默认农场工名字', description: 'join 未指定名字时使用；已有角色保留原名和进度。', 'x-hot': true },
      'worlds.stardew.bridgePort': { type: 'integer', minimum: 1024, maximum: 65535, title: '本机桥接端口' },
      'worlds.stardew.wakeIntervalSec': { type: 'integer', minimum: 10, maximum: 600, title: '游戏变化唤醒间隔（秒）', 'x-hot': true },
    },
  },
};
