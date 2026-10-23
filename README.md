Owner: `src/definition.ts`, `src/world.ts`, `smapi/ModEntry.cs`, `scripts/install.ps1`.

# 星露谷联机 World

Cortico 使用独立的 SMAPI 客户端作为农场工，加入用户主持的农场。首版面向 Windows、星露谷 1.6.15、SMAPI 4.5.2、Cortico World API 6。插件不修改存档格式，不生成物品、金钱或体力。

支持附近地图与碰撞格、农作物、物品、玩家、体力、背包和当前菜单的观察；支持游戏原生寻路、朝向、选物品、工具使用/种植、原生交互、菜单按钮和按键。Cortico 负责决定分工和策略。首版不提供自动钓鱼反应控制，也不保证处理所有特殊活动和第三方 Mod 菜单。

连续自主游玩需要多次模型调用，会增加 API 用量；30秒只限制无动作时的游戏变化唤醒间隔，连续工具决策不受这个间隔限制。

`home` 读取游戏原生住所、床位和农场门口，`map` 包含当前地图尺寸及出口，`nearby.grid` 是附近10格的碰撞图。小屋出入口可在地图边界外一格，移动只允许真实原生出口。背包和箱子按钮的 `role/slot` 区分物品来源；原生菜单负责物品转移，插件不改写物品数量。

游戏规则要求22:00停止工作并返家，02:00前确认睡觉；22:00、00:00和01:00未确认睡觉的阶段变化会绕过普通事件节流。走到床上可能直接触发确认，否则使用 `sleep`。点击 Yes 后用 `player.timeWentToBed`、`ReadyCheckDialog` 和 `menu.sleepReady` 验证，躺在床上本身不算完成。此日程由模型执行；断线、模型未响应或路线不通仍可能错过，插件没有瞬移或强制入睡兜底。

食物使用 `select` + `use_tool` 发起原生确认。NPC 使用相邻 `interact`，普通对话按menu_key Enter继续。确认弹窗文字展开前可能返回 `button=-1`，应等待再观察。只有核对回执中的物品、体力、对话或睡觉状态才能判断完成。

普通NPC对话的 `menu_key Enter/Space/E` 转为原生继续点击，不依赖游戏绑定；`menu.speaker/text/transitioning` 用于读对话与判断展开状态。`map.doors/characters` 提供当前地图的房门和人物位置。

出售默认关闭，需要开启“允许保留库存后出售普通收获物”。到普通出货箱旁 `interact`，看到 `menu.shipping=true` 后用 `ship(slot,count)`；每次最多99个，实际数量不得超过物品 `shippingMax`。只允许普通品质、基础单价≤300的蔬菜/水果/花，每叠保留至少5个，任务、特殊、配方及带Mod数据的物品禁止出售。种子、工具、材料、鱼和其他类别不在自动出售范围。普通菜单点击不能出售背包物品，出货箱前的工具调用也不能直接投物。此保护不覆盖用户自己的操作及第三方Mod自行执行的行为；不提供自动商店卖货或迷你出货箱出售。

## 安装

1. `pwsh -NoProfile -File scripts/install.ps1 -GameDir "<游戏目录>" -DeploymentDir "<Cortico部署目录>"` 安装专用 Mod 配置。游戏目录、部署目录和运行目录可以通过同名参数指定。
2. Cortico 扩展页选择本机目录，填此包目录，安装后重启 Cortico。新扩展需要重新加载进程；只重启 World 不会加载新包。
3. 确认 World「星露谷联机」已启用，并开启「允许操作独立角色」。安装脚本会为首次安装写入这两个开关；已有配置保持不变。若 Persona 按工具标签过滤，还需允许 `stardew_observe`、`stardew_act`、`stardew_stop`。
4. 正常用你的游戏客户端在“合作模式”主持农场。为bot准备一间空闲小屋。
5. 双击 `scripts/start-client.cmd`。bot的独立窗口停在标题即可，对 Cortico 说“用 join 加入我的星露谷农场”。插件会通过127.0.0.1加入房主，优先复用同名角色，否则选择空闲小屋并完成新农场工创建。默认名bot、喜好星露谷、女性、游戏默认外观，可在 join 参数中指定 name/favoriteThing/gender。插件不会修改已创建角色。
6. `stardew_observe` 返回 `ready=true`、`farmhand=true` 后开始分工。游戏选项里的“开始本地合作”是手柄分屏，会出现“按下开始键加入游戏”，应取消该提示；此插件使用独立客户端，通过 IP 联机。

若用户选择复用已有角色，在 World 配置的“默认农场工名字”填写其原名，或安装时传 `-CharacterName "原名"`。join 不传 name 时使用该配置。已有角色直接复用，不会应用新角色的名字、性别或喜好设置。

专用客户端默认目录为 `%LOCALAPPDATA%\Cortico\Stardew`，继承游戏原有 Mods 的目录联接，桥接 Mod 只安装在这里。你的默认 Mods 不增加控制插件。两端使用相同的游戏与 Mod 文件；以后新加 Mod 后再次运行安装脚本以补齐联接。

运行专用客户端时，插件只在内存中关闭该客户端失去焦点暂停的选项。游戏本身可能在正常保存设置时持久化此选项。房主仍需正常准备小屋；插件通过原生界面创建农场工，不编辑存档来跳过这些步骤。

## 暂停与诊断

- bot窗口按 F8 暂停/恢复，或关闭 Cortico 的动作开关。Cortico 暂停、World 停止和桥接断线会取消插件的寻路/等待；已经开始的游戏原生动画不能回滚。
- `stardew_observe` 的 `connected=true` 代表认证桥接已连接，`ready=true` 和 `farmhand=true` 才代表已经进入联机农场。`localPaused` 为游戏 F8 开关。
- 动作携带最新 `version`，一条连接只允许一个动作。动作结果返回新状态，变化结果也写入 Cortico 事件队列。被拒绝、超时或断线后先重新观察，不自动重发。
- `nearby.grid` 的 `.` 表示通过原生碰撞检查，`#` 表示有碰撞。它是瞬时观察，人物和怪物移动会改变结果。move可一次寻路到同地图目标；附近碰撞图仍为10格，远处可按map中的已知门口、出口和箱子位置行动。
- 桥接只绑定 `127.0.0.1`，端口默认 24983。凭据分别保存在部署 `data/stardew/bridge.json` 和专用 Mod `config.json`，不要分享这些文件。
- 客户端停在标题页时可观察 onboarding 并执行 join；其他游戏动作仍要求进入农场。房主身份始终被拒绝。无新状态超过12秒时 Cortico 拒绝提交动作。
- 无连接：确认 Cortico 已重启加载扩展、World 已启用、专用 SMAPI 日志里有 `Cortico Stardew Farmhand`。端口变更后重启 World 并重跑安装脚本同步客户端配置。

## 开发与验证

开发依赖安装后运行 `npm test`、`npm run typecheck`。C# 用 `dotnet build smapi/CorticoStardew.csproj -c Release -p:GameDir="游戏目录"` 编译。Cortico 根目录用 `pnpm describe:extension <包目录>` 和 `pnpm check:extension <包目录>` 核对契约。

离线协议测试使用真实本机 TCP 连接，覆盖身份验证、房主保护、旧观察、并发、回执、断线、超时取消、AbortSignal 和权限热切换。它们不证明已在游戏里完成种田或联机；实机验收需两个角色进入同一农场，再确认一次合法移动、工具使用和用户暂停。

## 卸载

先停用 Cortico 的星露谷 World，再卸载此扩展。关闭专用客户端即可停止其控制；保留源包和运行目录不会自动启动客户端。需要删除专用运行目录时先检查 Mods 子目录中的目录联接，不要通过联接递归清理房主的 Mods。游戏内角色和农场存档由星露谷正常管理。


## 0.1.3 操作与观察

- `move(x,y)` 一次走到同地图目标，不必每10格分段；最长55秒，转场后重新核对地图。它仍使用原生寻路，不穿墙或传送。
- `open_chest(x,y)` 校验当前地图的储物箱，寻路到可达相邻格后原生打开；箱子被替换、路径中断、锁被占用或取消会停止，不自动重试。
- `map.chests` 列出当前地图的全部箱子元数据，包含位置、chestId、原生染色名称和HEX。默认材质色的原生值为黑色哨兵，不能当成黑色外观。自定义色保留HEX，不猜颜色名称；材质包另行改色时原生染色不一定等于最终贴图。
- `menu.chest` 标识实际打开的箱子。库存仍须打开后读取，颜色不证明用途或内容；同色箱子用位置区分。
- `use_tool(slot,direction)` 可同时选择并使用工具；interact/use_tool无需预先face。
- inventory只列非空格，slot是实际格号；emptySlots和inventoryCapacity保留空格及容量信息。按钮只提供菜单索引和角色，不重复传送像素矩形。
- 游戏事件为summaryOnly摘要；观察和动作回执保留完整状态。摘要版本不能配旧物品/按钮索引使用，需要时先重新观察。正常动作后直接使用回执，不重复观察。
- 识图可用于装饰、未知Mod界面或贴图变化的按需辅助；此World不依赖额外视觉插件，不会自动开启持续截图。

公开 Cortico 0.2.6 未提供 WorldHost.isPaused 查询；需要明确停手时关闭允许操作开关或在专用客户端按 F8，进行中的工具也会响应 AbortSignal 取消。
