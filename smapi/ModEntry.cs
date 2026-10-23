using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Pathfinding;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace CorticoStardew;

public sealed class ModEntry : Mod
{
    private Transport? transport;
    private JoinFlow? joinFlow;
    private readonly string session = Guid.NewGuid().ToString("N");
    private int revision;
    private string version = "";
    private string signature = "";
    private string actionSignature = "";
    private string before = "";
    private string? actionId;
    private string kind = "";
    private int generation;
    private DateTime deadline;
    private int remainingTicks;
    private Point destination;
    private string location = "";
    private long farmerId;
    private string actionLocation = "";
    private long actionFarmerId;
    private Point observedTile;
    private Vector2 movementPosition;
    private IClickableMenu? observedMenu;
    private string observedInventory = "";
    private string observedMenuState = "";
    private readonly List<ClickableComponent> buttons = new();
    private PathFindController? controller;
    private Point chestTile;
    private Chest? targetChest;
    private bool openingChest;
    private bool previouslyConnected;
    private int pulse;
    private bool emergencyPaused;
    private bool observedEmergencyPaused;

    private bool Farmhand => Context.IsWorldReady && Context.IsMultiplayer && !Context.IsMainPlayer && Context.ScreenId == 0;

    public override void Entry(IModHelper helper)
    {
        var config = helper.ReadConfig<ModConfig>();
        if (config.Token.Length < 32 || config.Port is < 1024 or > 65535)
        {
            Monitor.Log("桥接凭据未配置。请用插件的专用客户端启动入口，不要把本 Mod 装进房主 Mods。", LogLevel.Error);
            return;
        }
        transport = new Transport(config);
        joinFlow = new JoinFlow(helper);
        _ = transport.Run();
        helper.Events.GameLoop.UpdateTicked += Update;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => Cancel("返回标题界面，动作停止。");
        helper.Events.Input.ButtonPressed += (_, e) =>
        {
            if (e.Button != SButton.F8) return;
            emergencyPaused = !emergencyPaused;
            if (emergencyPaused) Cancel("F8 本机暂停。");
            Monitor.Log(emergencyPaused ? "角色操作已暂停，F8 恢复。" : "角色操作已恢复。", LogLevel.Info);
        };
        Monitor.Log("本机桥接已就绪。加入房主农场后可控制客机角色；F8 暂停/恢复。", LogLevel.Info);
    }

    private void Update(object? sender, UpdateTickedEventArgs e)
    {
        if (transport == null) return;
        try
        {
            if (!transport.Connected)
            {
                if (previouslyConnected || actionId != null) Cancel("桥接断线，停止当前动作。");
                previouslyConnected = false;
                return;
            }
            if (!previouslyConnected)
            {
                if (Farmhand && Game1.currentLocation?.Map == null) return;
                previouslyConnected = true;
                SendState();
            }
            if (Farmhand && Game1.currentLocation?.Map == null) return;
            if (Farmhand || JoinFlow.CanJoin) Game1.options.pauseWhenOutOfFocus = false;
            while (transport.Incoming.TryDequeue(out var inbound))
            {
                if (!transport.Connected || inbound.Generation != transport.Generation) continue;
                var message = inbound.Message;
                switch (message.GetProperty("type").GetString())
                {
                    case "stop": Cancel("收到停止请求。"); break;
                    case "cancel":
                        if (message.GetProperty("id").GetString() == actionId) Cancel("动作已取消。");
                        break;
                    case "action": Begin(message, inbound.Generation); break;
                }
            }
            if (actionId != null)
            {
                if (emergencyPaused || generation != transport.Generation || (kind != "join" && (!Farmhand || Game1.player.UniqueMultiplayerID != actionFarmerId)))
                    Cancel("角色或连接已改变，停止动作。");
                else if (DateTime.UtcNow > deadline) Finish("cancelled", "动作超时，已停止；核对当前状态后再决定。");
                else if (kind == "join")
                {
                    string? result = joinFlow!.Advance();
                    if (result != null) Finish("changed", result);
                }
                else if (kind == "open_chest" && openingChest)
                {
                    if (Game1.activeClickableMenu is ItemGrabMenu grab && ReferenceEquals(grab.sourceItem, targetChest)) Finish("changed", "指定箱子已打开，请核对箱子标识与物品。");
                    else if (Game1.activeClickableMenu != null || Game1.eventUp) Finish("cancelled", "其他菜单或事件打断开箱。");
                }
                else if (kind is "move" or "open_chest")
                {
                    if (Game1.player.Position != movementPosition) { Game1.player.behaviorOnMovement(Game1.player.FacingDirection); movementPosition = Game1.player.Position; }
                    if (Game1.activeClickableMenu != null || Game1.eventUp) Finish(Game1.player.TilePoint == destination ? "changed" : "cancelled", Game1.player.TilePoint == destination ? "已到达目标格并触发原生菜单或事件。" : "菜单或事件打断了寻路。");
                    else if (Game1.fadeToBlack || !Game1.player.CanMove)
                    {
                        // 转场开始就解除寻路，防止淡出期间重复触发同一个出口。
                        if (Game1.player.controller == controller && controller != null) { Game1.player.controller = null; Game1.player.Halt(); }
                    }
                    else if (Game1.currentLocation.NameOrUniqueName != actionLocation) Finish(kind == "move" ? "changed" : "cancelled", "通过原生出入口进入了另一地图。");
                    else if (Game1.player.controller != controller && !Game1.fadeToBlack && Game1.player.CanMove)
                    {
                        if (kind == "open_chest" && Game1.player.TilePoint == destination) OpenTargetChest();
                        else Finish(Game1.player.TilePoint == destination ? null : "cancelled", Game1.player.TilePoint == destination ? "已到达目标格。" : "原生寻路已结束，未到达目标格。");
                    }
                }
                else if (--remainingTicks <= 0 && !Game1.player.UsingTool && !Game1.player.isEating && !Game1.fadeToBlack)
                    Finish(null, "动作已结束，请核对返回的游戏状态。");
            }
            if (++pulse >= 120) { pulse = 0; SendState(); }
        }
        catch (Exception ex)
        {
            Monitor.Log($"桥接动作失败：{ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            if (actionId != null) Finish("cancelled", $"{ex.GetType().Name}: {ex.Message}；自动流程已停止，请核对返回状态。");
        }
    }

    private void Begin(JsonElement message, int connectionGeneration)
    {
        var id = message.GetProperty("id").GetString() ?? "";
        if (actionId != null) { Reject(id, "已有动作执行中。"); return; }
        try
        {
            if (emergencyPaused) throw new InvalidOperationException("F8 本机操作已暂停。");
            if (message.GetProperty("version").GetString() != version) throw new InvalidOperationException("观察已过期，请重新观察。");
            kind = message.GetProperty("kind").GetString() ?? "";
            if (kind == "join")
            {
                if (!JoinFlow.CanJoin) throw new InvalidOperationException("当前界面不允许加入农场，且不能控制房主。");
                before = signature;
                actionId = id;
                generation = connectionGeneration;
                deadline = DateTime.UtcNow.AddSeconds(20);
                joinFlow!.Start(message);
                SendState();
                return;
            }
            if (!Farmhand || JoinFlow.Creating) throw new InvalidOperationException("只允许控制已进入农场的联机客机角色；首次进入请用 join。");
            if (Game1.player.TilePoint != observedTile || Game1.currentLocation.NameOrUniqueName != location || Game1.player.UniqueMultiplayerID != farmerId || Game1.activeClickableMenu != observedMenu || emergencyPaused != observedEmergencyPaused)
                throw new InvalidOperationException("位置、角色或菜单已改变，请重新观察。");
            if (InventoryFingerprint() != observedInventory || MenuFingerprint() != observedMenuState)
                throw new InvalidOperationException("背包或菜单内容已改变，请重新观察。");
            if (Game1.eventUp || Game1.fadeToBlack || Game1.player.UsingTool || Game1.player.isEating) throw new InvalidOperationException("游戏事件、转场、进食或工具动画进行中。");
            if (Game1.activeClickableMenu != null && kind is not ("menu_click" or "menu_key" or "wait" or "ship")) throw new InvalidOperationException("先处理当前菜单。");
            if (Game1.activeClickableMenu == null && !Game1.player.CanMove && kind != "wait") throw new InvalidOperationException("角色当前无法操作，请等待游戏恢复。");
            before = signature;
            actionId = id;
            actionLocation = location;
            actionFarmerId = farmerId;
            openingChest = false;
            targetChest = null;
            generation = connectionGeneration;
            deadline = DateTime.UtcNow.AddSeconds(kind is "move" or "open_chest" ? 55 : 20);
            switch (kind)
            {
                case "move":
                    destination = new Point(Integer(message, "x", -1, 10000), Integer(message, "y", -1, 10000));
                    if (!Walkable(destination.X, destination.Y) || !TryWalk(destination)) throw new InvalidOperationException("原生寻路未找到可走路径，角色可能已在目标格。");
                    break;
                case "open_chest":
                    chestTile = new Point(Integer(message, "x", 0, 10000), Integer(message, "y", 0, 10000));
                    if (!Game1.currentLocation.Objects.TryGetValue(new Vector2(chestTile.X, chestTile.Y), out var chestObject) || chestObject is not Chest chest || !chest.playerChest.Value || chest.SpecialChestType == Chest.SpecialChestTypes.MiniShippingBin)
                        throw new InvalidOperationException("该坐标不是当前地图的普通储物箱；请核对map.chests。");
                    targetChest = chest;
                    var approaches = Offsets.Select(offset => chestTile + offset).Where(point => Walkable(point.X, point.Y)).OrderBy(point => Math.Abs(point.X - observedTile.X) + Math.Abs(point.Y - observedTile.Y));
                    bool started = false;
                    foreach (var approach in approaches)
                    {
                        destination = approach;
                        if (approach == observedTile) { OpenTargetChest(); started = true; break; }
                        if (TryWalk(approach)) { started = true; break; }
                    }
                    if (!started) throw new InvalidOperationException("找不到箱子相邻的可达格，未移动或打开箱子。");
                    break;
                case "face": Game1.player.faceDirection(Integer(message, "direction", 0, 3)); break;
                case "select":
                    int slot = Integer(message, "slot", 0, Game1.player.Items.Count - 1);
                    Game1.player.CurrentToolIndex = slot;
                    break;
                case "use_tool":
                    if (message.TryGetProperty("slot", out _)) Game1.player.CurrentToolIndex = Integer(message, "slot", 0, Game1.player.Items.Count - 1);
                    Game1.player.faceDirection(Integer(message, "direction", 0, 3));
                    Game1.wasMouseVisibleThisFrame = false;
                    var toolPoint = Game1.player.GetToolLocation();
                    if (Game1.currentLocation.buildings.OfType<StardewValley.Buildings.ShippingBin>().Any(bin => bin.CanLeftClick((int)toolPoint.X, (int)toolPoint.Y)))
                        throw new InvalidOperationException("出货箱旁不能直接使用物品；请interact打开，再用ship按保留规则出售。");
                    if (Game1.player.ActiveObject is { Edibility: not -300 })
                        WithActionInput(() => Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(), Game1.input.GetGamePadState()));
                    else Game1.pressUseToolButton();
                    break;
                case "interact":
                    int direction = Integer(message, "direction", 0, 3);
                    Game1.player.faceDirection(direction);
                    Point target = Game1.player.TilePoint + Offsets[direction];
                    WithActionInput(() => Game1.currentLocation.checkAction(new xTile.Dimensions.Location(target.X, target.Y), Game1.viewport, Game1.player));
                    break;
                case "sleep":
                    if (!BedFurniture.IsBedHere(Game1.currentLocation, Game1.player.TilePoint.X, Game1.player.TilePoint.Y)) throw new InvalidOperationException("请先走到观察中床的 sleepTile，再请求睡觉。");
                    if (!Game1.player.hasMoved) throw new InvalidOperationException("角色刚登录尚未活动，请先正常走下床再上床。");
                    Game1.currentLocation.performTouchAction("Sleep", Game1.player.Tile);
                    break;
                case "menu_click":
                    if (Game1.activeClickableMenu == null) throw new InvalidOperationException("没有打开菜单。");
                    int index = Integer(message, "button", 0, buttons.Count - 1);
                    var component = buttons[index];
                    if (!component.visible) throw new InvalidOperationException("按钮不再可见。");
                    var role = ButtonRole(Game1.activeClickableMenu, null, component);
                    if ((IsShippingMenu(Game1.activeClickableMenu) || Game1.activeClickableMenu is ShopMenu) && role.Role == "inventory")
                        throw new InvalidOperationException("不能用普通点击出售背包物品；出货箱请使用ship，商店出售需另行确认。");
                    Game1.activeClickableMenu.performHoverAction(component.bounds.Center.X, component.bounds.Center.Y);
                    Game1.activeClickableMenu.receiveLeftClick(component.bounds.Center.X, component.bounds.Center.Y);
                    break;
                case "menu_key":
                    string key = message.GetProperty("key").GetString() ?? "";
                    if (!MenuKeys.TryGetValue(key, out var parsed)) throw new InvalidOperationException("不支持此菜单按键。");
                    if (IsShippingMenu(Game1.activeClickableMenu) && key != "Escape") throw new InvalidOperationException("出货菜单仅允许Escape退出；出售使用ship。");
                    if (Game1.activeClickableMenu is DialogueBox dialogue && !dialogue.isQuestion && key is "E" or "Enter" or "Space") dialogue.receiveLeftClick(0, 0);
                    else if (Game1.activeClickableMenu != null) Game1.activeClickableMenu.receiveKeyPress(parsed);
                    else if (key == "E") Game1.activeClickableMenu = new GameMenu();
                    else throw new InvalidOperationException("没有打开菜单；E 可打开背包。");
                    break;
                case "ship":
                    if (Game1.activeClickableMenu is not ItemGrabMenu shipping || !shipping.shippingBin) throw new InvalidOperationException("请先到普通出货箱旁interact打开出货菜单。");
                    int shipSlot = Integer(message, "slot", 0, Game1.player.Items.Count - 1);
                    int count = Integer(message, "count", 1, 99);
                    if (shipping.heldItem != null || ShippingAvailable(Game1.player.Items[shipSlot]) < count) throw new InvalidOperationException("仅可卖普通品质、单价不超过300的水果/蔬菜/花；每叠至少留5个，任务、特殊和材料不卖。");
                    var shipButton = shipping.inventory.inventory[shipSlot];
                    for (int i = 0; i < count; i++)
                    {
                        int previous = Game1.player.Items[shipSlot]?.Stack ?? 0;
                        WithActionInput(() => shipping.receiveRightClick(shipButton.bounds.Center.X, shipButton.bounds.Center.Y));
                        if ((Game1.player.Items[shipSlot]?.Stack ?? 0) != previous - 1 || shipping.heldItem != null) throw new InvalidOperationException("原生出售数量不符，已停止；核对背包和出货箱，不能重发。");
                    }
                    break;
                case "wait": Integer(message, "ticks", 1, 600); break;
                default: throw new InvalidOperationException("未知动作。");
            }
            remainingTicks = kind == "wait" ? message.GetProperty("ticks").GetInt32() : kind == "interact" ? 60 : kind is "menu_click" or "menu_key" ? 30 : 3;
            SendState();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            if (actionId == id) Finish("cancelled", $"动作未完成或结果不确定：{ex.Message}；先核对返回状态。");
            else Reject(id, ex.Message);
        }
    }

    private static int Integer(JsonElement message, string key, int min, int max)
    {
        if (!message.TryGetProperty(key, out var value) || !value.TryGetInt32(out int number) || number < min || number > max) throw new InvalidOperationException($"{key} 超出范围。");
        return number;
    }
    private static readonly Point[] Offsets = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };
    private bool TryWalk(Point point)
    {
        if (!Game1.currentLocation.farmers.Contains(Game1.player)) throw new InvalidOperationException("角色尚未登记在当前地图，不能启动寻路。");
        int cells = Game1.currentLocation.Map.Layers[0].LayerWidth * Game1.currentLocation.Map.Layers[0].LayerHeight;
        var path = new PathFindController(Game1.player, Game1.currentLocation, point, -1, null, Math.Clamp(cells, 2000, 20000));
        if (path.pathToEndPoint == null || path.pathToEndPoint.Count == 0) return false;
        controller = path;
        Game1.player.controller = path;
        movementPosition = Game1.player.Position;
        return true;
    }
    private void OpenTargetChest()
    {
        bool sameChest = Game1.currentLocation.Objects.TryGetValue(new Vector2(chestTile.X, chestTile.Y), out var currentChest) && ReferenceEquals(currentChest, targetChest);
        int direction = ChestAccess.Direction(Game1.player.TilePoint.X, Game1.player.TilePoint.Y, chestTile.X, chestTile.Y, sameChest);
        Game1.player.faceDirection(direction);
        WithActionInput(() => Game1.currentLocation.checkAction(new xTile.Dimensions.Location(chestTile.X, chestTile.Y), Game1.viewport, Game1.player));
        openingChest = true;
        deadline = DateTime.UtcNow.AddSeconds(4);
    }
    // 箱子和进食会检查原生右键沿；只在本次同步调用内提供输入，不发送系统按键。
    private static void WithActionInput(Action action)
    {
        var input = Game1.input;
        var oldMouse = Game1.oldMouseState;
        var oldKeyboard = Game1.oldKBState;
        Game1.input = new ActionInput(input);
        Game1.oldKBState = default;
        Game1.oldMouseState = new MouseState(oldMouse.X, oldMouse.Y, oldMouse.ScrollWheelValue, oldMouse.LeftButton, oldMouse.MiddleButton, ButtonState.Released, oldMouse.XButton1, oldMouse.XButton2);
        Game1.wasMouseVisibleThisFrame = false;
        try { action(); }
        finally { Game1.input = input; Game1.oldMouseState = oldMouse; Game1.oldKBState = oldKeyboard; }
    }
    private sealed class ActionInput : InputState
    {
        private readonly InputState original;
        public ActionInput(InputState original) { this.original = original; }
        public override KeyboardState GetKeyboardState() => default;
        public override GamePadState GetGamePadState() => default;
        public override MouseState GetMouseState()
        {
            var mouse = original.GetMouseState();
            return new MouseState(mouse.X, mouse.Y, mouse.ScrollWheelValue, mouse.LeftButton, mouse.MiddleButton, ButtonState.Pressed, mouse.XButton1, mouse.XButton2);
        }
    }
    private static readonly Dictionary<string, Keys> MenuKeys = new()
    {
        ["Escape"] = Keys.Escape, ["Enter"] = Keys.Enter, ["Space"] = Keys.Space, ["E"] = Keys.E,
        ["Up"] = Keys.Up, ["Down"] = Keys.Down, ["Left"] = Keys.Left, ["Right"] = Keys.Right,
    };

    private void Cancel(string detail)
    {
        if (actionId != null) Finish("cancelled", detail);
        else StopMovement();
    }
    private void StopMovement()
    {
        if (kind == "join") joinFlow?.Stop();
        if (Context.IsWorldReady && Game1.player.controller == controller && controller != null)
        {
            Game1.player.controller = null;
            Game1.player.Halt();
        }
        controller = null;
    }
    private void Finish(string? status, string detail)
    {
        string? id = actionId;
        var state = Snapshot();
        StopMovement();
        actionId = null;
        state["busy"] = false;
        status ??= signature == before ? "unchanged" : "changed";
        transport?.Send(new { type = "result", id, status, detail, state });
    }
    private void Reject(string id, string detail) => transport?.Send(new { type = "result", id, status = "rejected", detail, state = Snapshot() });
    private void SendState() => transport?.Send(new { type = "state", state = Snapshot() });

    private bool Walkable(int x, int y)
    {
        var map = Game1.currentLocation.Map;
        if (map == null) return false;
        // 小屋出口可能在地图外一格；仅允许原生 warp，仍由原生寻路处理碰撞和转场。
        if (x < 0 || y < 0 || x >= map.Layers[0].LayerWidth || y >= map.Layers[0].LayerHeight)
            return Game1.currentLocation.warps.Any(warp => warp.X == x && warp.Y == y);
        var box = Game1.player.GetBoundingBox();
        box.Offset(x * 64 + 32 - box.Center.X, y * 64 + 32 - box.Center.Y);
        return !Game1.currentLocation.isCollidingPosition(box, Game1.viewport, false, 0, false, Game1.player);
    }
    private static bool IsShippingMenu(IClickableMenu? menu) => menu is ItemGrabMenu grab && (grab.shippingBin || grab.sourceItem is Chest chest && chest.SpecialChestType == Chest.SpecialChestTypes.MiniShippingBin);
    private static int ShippingAvailable(Item? item) => item is StardewValley.Object obj
        ? ShippingPolicy.Available(obj.Category, obj.Price, obj.Quality, obj.questItem.Value || obj.specialItem || obj.IsRecipe || obj.ItemId == "890" || obj.modData.Count() > 0, obj.canBeShipped(), obj.Stack) : 0;
    private static object? ItemState(Item? item) => item == null ? null : new { id = item.QualifiedItemId, name = item.DisplayName, count = item.Stack, kind = item.GetType().Name, shippingMax = ShippingAvailable(item) };
    private static object ChestState(Chest chest, string location, int x, int y)
    {
        var color = chest.playerChoiceColor.Value;
        return new { chestId = $"{location}:{x},{y}", x, y, name = chest.DisplayName, kind = chest.SpecialChestType.ToString(),
            color = new { name = ChestColor.Name(color.R, color.G, color.B), hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}", isDefault = color == Color.Black } };
    }

    private static List<ClickableComponent> CurrentButtons()
    {
        var menu = Game1.activeClickableMenu;
        if (menu == null) return new();
        menu.populateClickableComponentList();
        var components = menu is DialogueBox dialogue && dialogue.responses?.Length > 0 ? dialogue.responseCC : menu.allClickableComponents;
        var result = components?.ToList() ?? new();
        if (menu is GameMenu gameMenu)
        {
            var page = gameMenu.GetCurrentPage();
            page.populateClickableComponentList();
            if (page.allClickableComponents != null) result.AddRange(page.allClickableComponents);
        }
        var viewport = new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
        return result.Distinct().Where(button => button.visible && button.bounds.Width > 0 && button.bounds.Height > 0 && button.bounds.Intersects(viewport)).Take(200).ToList();
    }
    private static string InventoryFingerprint() => JsonSerializer.Serialize(new { slot = Game1.player.CurrentToolIndex, items = Game1.player.Items.Select(ItemState), cursor = ItemState(Game1.player.CursorSlotItem) });

    private static (string Role, int? Slot) ButtonRole(IClickableMenu menu, IClickableMenu? page, ClickableComponent button)
    {
        if (menu is ItemGrabMenu grab)
        {
            int containerSlot = grab.ItemsToGrabMenu.inventory.IndexOf(button);
            if (containerSlot >= 0) return ("container", containerSlot);
        }
        var inventory = menu is MenuWithInventory withInventory ? withInventory.inventory : menu is ShopMenu shop ? shop.inventory : page is InventoryPage inventoryPage ? inventoryPage.inventory : null;
        int slot = inventory?.inventory.IndexOf(button) ?? -1;
        return slot >= 0 ? ("inventory", slot) : ("menu", null);
    }
    private static string MenuFingerprint() => JsonSerializer.Serialize(new {
        buttons = CurrentButtons().Select(button => new { button.name, button.label, x = button.bounds.X, y = button.bounds.Y, width = button.bounds.Width, height = button.bounds.Height }),
        stock = Game1.activeClickableMenu is ShopMenu shop ? shop.forSale.Skip(shop.currentItemIndex).Take(shop.forSaleButtons.Count).Select(item => new { item.QualifiedItemId, item.Stack, price = shop.itemPriceAndStock.TryGetValue(item, out var value) ? value.Price : (int?)null }).ToArray() : null,
        container = Game1.activeClickableMenu is ItemGrabMenu grab ? grab.ItemsToGrabMenu.actualInventory.Select(ItemState).ToArray() : null,
        dialogue = Game1.activeClickableMenu is DialogueBox box ? box.getCurrentString() : null,
    });

    private JsonObject Snapshot()
    {
        buttons.Clear();
        object publicState;
        if (!Farmhand || JoinFlow.Creating)
        {
            publicState = new { ready = false, farmhand = Farmhand, localPaused = emergencyPaused, onboarding = JoinFlow.Observe() };
        }
        else
        {
            var player = Game1.player;
            var current = Game1.currentLocation;
            var home = Utility.getHomeOfFarmer(player);
            var homeBed = home.GetPlayerBedSpot();
            var homeBuilding = Game1.getFarm().buildings.FirstOrDefault(building => building.HasIndoorsName(home.NameOrUniqueName));
            var tile = player.TilePoint;
            location = current.NameOrUniqueName;
            farmerId = player.UniqueMultiplayerID;
            observedTile = tile;
            observedMenu = Game1.activeClickableMenu;
            observedEmergencyPaused = emergencyPaused;
            observedInventory = InventoryFingerprint();
            observedMenuState = MenuFingerprint();
            const int radius = 10;
            int left = Math.Max(0, tile.X - radius), top = Math.Max(0, tile.Y - radius);
            int right = Math.Min(current.Map.Layers[0].LayerWidth - 1, tile.X + radius), bottom = Math.Min(current.Map.Layers[0].LayerHeight - 1, tile.Y + radius);
            bool Nearby(Vector2 point) => Math.Abs(point.X - tile.X) <= radius && Math.Abs(point.Y - tile.Y) <= radius;
            var rows = new List<string>();
            for (int y = top; y <= bottom; y++)
            {
                var row = new char[right - left + 1];
                for (int x = left; x <= right; x++) row[x - left] = Walkable(x, y) ? '.' : '#';
                rows.Add(new string(row));
            }
            var objects = current.Objects.Pairs.Where(pair => Nearby(pair.Key)).Take(160)
                .Select(pair => new { x = (int)pair.Key.X, y = (int)pair.Key.Y, name = pair.Value.DisplayName, id = pair.Value.QualifiedItemId, ready = pair.Value.readyForHarvest.Value,
                    chest = pair.Value is Chest nearbyChest ? ChestState(nearbyChest, location, (int)pair.Key.X, (int)pair.Key.Y) : null }).ToArray();
            var terrain = current.terrainFeatures.Pairs.Where(pair => Nearby(pair.Key)).Take(160).Select(pair =>
            {
                var soil = pair.Value as HoeDirt;
                return new { x = (int)pair.Key.X, y = (int)pair.Key.Y, kind = pair.Value.GetType().Name, watered = soil?.state.Value == 1,
                    crop = soil?.crop == null ? null : new { name = soil.crop.indexOfHarvest.Value, phase = soil.crop.currentPhase.Value, dead = soil.crop.dead.Value,
                        harvestable = soil.crop.currentPhase.Value >= soil.crop.phaseDays.Count - 1 && (!soil.crop.fullyGrown.Value || soil.crop.dayOfCurrentPhase.Value <= 0) } };
            }).ToArray();
            var menu = Game1.activeClickableMenu;
            var page = menu is GameMenu gameMenu ? gameMenu.GetCurrentPage() : menu;
            buttons.AddRange(CurrentButtons());
            publicState = new {
                ready = true, farmhand = true, localPaused = emergencyPaused,
                world = new { farmName = Game1.MasterPlayer.farmName.Value },
                home = new { location = home.NameOrUniqueName, bedX = homeBed.X, bedY = homeBed.Y,
                    entrance = homeBuilding == null ? null : new { location = "Farm", x = homeBuilding.tileX.Value + homeBuilding.humanDoor.Value.X, y = homeBuilding.tileY.Value + homeBuilding.humanDoor.Value.Y },
                    exits = home.warps.Select(warp => new { x = warp.X, y = warp.Y, destination = warp.TargetName, destinationX = warp.TargetX, destinationY = warp.TargetY }).ToArray() },
                map = new { width = current.Map.Layers[0].LayerWidth, height = current.Map.Layers[0].LayerHeight,
                    chests = current.Objects.Pairs.Where(pair => pair.Value is Chest).Select(pair => ChestState((Chest)pair.Value, location, (int)pair.Key.X, (int)pair.Key.Y)).ToArray(),
                    doors = current.doors.Pairs.Select(door => new { x = door.Key.X, y = door.Key.Y, destination = door.Value }).ToArray(),
                    characters = current.characters.Take(80).Select(character => new { name = character.displayName, kind = character.GetType().Name, x = character.TilePoint.X, y = character.TilePoint.Y }).ToArray(),
                    exits = current.warps.Select(warp => new { x = warp.X, y = warp.Y, destination = warp.TargetName, destinationX = warp.TargetX, destinationY = warp.TargetY }).ToArray() },
                date = new { year = Game1.year, season = Game1.currentSeason, day = Game1.dayOfMonth, time = Game1.timeOfDay },
                player = new { name = player.Name, id = farmerId.ToString(), location, x = tile.X, y = tile.Y, facing = player.FacingDirection,
                    health = player.health, stamina = Math.Round(player.Stamina, 1), money = player.Money, selectedSlot = player.CurrentToolIndex, usingTool = player.UsingTool, eating = player.isEating, canMove = player.CanMove, inBed = player.isInBed.Value, timeWentToBed = player.timeWentToBed.Value },
                inventory = player.Items.Select((item, slot) => new { slot, item = ItemState(item) }).Where(entry => entry.item != null).ToArray(),
                inventoryCapacity = player.Items.Count,
                emptySlots = player.Items.Select((item, slot) => new { item, slot }).Where(entry => entry.item == null).Select(entry => entry.slot).ToArray(),
                nearby = new { originX = left, originY = top, grid = rows, objects, terrain,
                    buildings = current.buildings.Where(building => Nearby(new Vector2(building.tileX.Value, building.tileY.Value))).Select(building => new { name = building.buildingType.Value, kind = building.GetType().Name, x = building.tileX.Value, y = building.tileY.Value, width = building.tilesWide.Value, height = building.tilesHigh.Value,
                        doorX = building.humanDoor.Value.X < 0 ? (int?)null : building.tileX.Value + building.humanDoor.Value.X, doorY = building.humanDoor.Value.Y < 0 ? (int?)null : building.tileY.Value + building.humanDoor.Value.Y }).ToArray(),
                    furniture = current.furniture.Where(item => Nearby(item.TileLocation)).Take(80).Select(item => new { name = item.DisplayName, kind = item.GetType().Name, x = (int)item.TileLocation.X, y = (int)item.TileLocation.Y,
                        sleepTile = item is BedFurniture bed ? new { x = bed.GetBedSpot().X, y = bed.GetBedSpot().Y } : null }).ToArray(),
                    characters = current.characters.Where(character => Nearby(character.Tile)).Take(40).Select(character => new { name = character.displayName, kind = character.GetType().Name, x = character.TilePoint.X, y = character.TilePoint.Y }).ToArray(),
                    players = Game1.getOnlineFarmers().Where(farmer => farmer.currentLocation == current && Nearby(farmer.Tile)).Select(farmer => new { name = farmer.Name, x = farmer.TilePoint.X, y = farmer.TilePoint.Y, isMe = farmer == player }).ToArray(),
                    warps = current.warps.Where(warp => Math.Abs(warp.X - tile.X) <= radius && Math.Abs(warp.Y - tile.Y) <= radius).Select(warp => new { x = warp.X, y = warp.Y, destination = warp.TargetName, destinationX = warp.TargetX, destinationY = warp.TargetY }).ToArray(),
                },
                menu = menu == null ? null : new { kind = menu.GetType().Name, page = page?.GetType().Name,
                    text = menu is DialogueBox textDialogue ? textDialogue.getCurrentString() : "",
                    speaker = menu is DialogueBox npcDialogue ? npcDialogue.characterDialogue?.speaker?.displayName : null,
                    isQuestion = menu is DialogueBox question && question.isQuestion,
                    transitioning = menu is DialogueBox transition && transition.transitioning,
                    shipping = IsShippingMenu(menu),
                    chest = menu is ItemGrabMenu chestMenu && chestMenu.sourceItem is Chest menuChest ? ChestState(menuChest, location, (int)menuChest.TileLocation.X, (int)menuChest.TileLocation.Y) : null,
                    shipments = IsShippingMenu(menu) ? Game1.getFarm().getShippingBin(player).Select(ItemState).ToArray() : null,
                    sleepReady = menu is ReadyCheckDialog readyCheck && readyCheck.checkName == "sleep" ? new { ready = Game1.netReady.GetNumberReady("sleep"), required = Game1.netReady.GetNumberRequired("sleep") } : null,
                    responses = menu is DialogueBox responseDialogue && responseDialogue.responses != null ? responseDialogue.responses.Select((response, index) => new { key = response.responseKey, text = response.responseText, button = responseDialogue.responseCC != null && index < responseDialogue.responseCC.Count ? buttons.IndexOf(responseDialogue.responseCC[index]) : -1 }).ToArray() : null,
                    heldItem = menu is MenuWithInventory inventoryMenu ? ItemState(inventoryMenu.heldItem) : ItemState(player.CursorSlotItem),
                    container = menu is ItemGrabMenu grab ? grab.ItemsToGrabMenu.actualInventory.Select((item, slot) => new { slot, item = ItemState(item) }).ToArray() : null,
                    shop = menu is ShopMenu shop ? shop.forSale.Skip(shop.currentItemIndex).Take(shop.forSaleButtons.Count).Select(item => new { name = item.DisplayName, price = shop.itemPriceAndStock.TryGetValue(item, out var stock) ? stock.Price : (int?)null, description = item.getDescription() }).ToArray() : null,
                    buttons = buttons.Select((button, index) => { var role = ButtonRole(menu, page, button); return new { button = index, name = button.name, label = button.label, role = role.Role, slot = role.Slot }; }).ToArray() },
            };
        }
        string next = JsonSerializer.Serialize(publicState);
        var state = JsonNode.Parse(next)!.AsObject();
        signature = next;
        string basis = StateRevision.ActionBasis(state);
        if (basis != actionSignature) { actionSignature = basis; version = $"{session}:{++revision}"; }
        state["version"] = version;
        state["busy"] = actionId != null;
        return state;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopMovement(); transport?.Dispose(); }
        base.Dispose(disposing);
    }
}
