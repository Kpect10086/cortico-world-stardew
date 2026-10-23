using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;

namespace CorticoStardew;

public sealed class JoinFlow
{
    private readonly IModHelper helper;
    private JoinProfile? profile;
    private bool creationSubmitted;
    public JoinFlow(IModHelper helper) { this.helper = helper; }
    private static IClickableMenu? Menu => Game1.activeClickableMenu is TitleMenu ? TitleMenu.subMenu ?? Game1.activeClickableMenu : Game1.activeClickableMenu;
    public static bool Creating => Menu is CharacterCustomization { source: CharacterCustomization.Source.NewFarmhand } && Game1.client != null;
    private static bool Playing => Context.IsWorldReady && Context.IsMultiplayer && !Context.IsMainPlayer && !Creating;
    public static string Stage => Context.ScreenId != 0 ? "unsupported_split_screen" : Context.IsWorldReady && Context.IsMainPlayer ? "host_loaded" :
        Creating ? "create_character" : Playing ? "in_game" : Menu is FarmhandMenu farmhands ? (farmhands.gettingFarmhands || farmhands.approvingFarmhand ? "connecting" : "choose_farmer") :
        !Context.IsWorldReady && Game1.activeClickableMenu is TitleMenu && Menu is TitleMenu or CoopMenu ? "title" : "blocked";
    public static bool CanJoin => Stage is "title" or "connecting" or "choose_farmer" or "create_character";

    private static List<FarmhandChoice> Choices(FarmhandMenu menu) => menu.MenuSlots.Select((slot, index) => (slot, index))
        .Where(entry => entry.slot is FarmhandMenu.FarmhandSlot)
        .Select(entry => { var slot = (FarmhandMenu.FarmhandSlot)entry.slot; return new FarmhandChoice(entry.index, slot.Farmer.Name, slot.Farmer.isCustomized.Value, slot.BelongsToAnotherPlayer()); }).ToList();

    public static object Observe() => new {
        stage = Stage, canJoin = CanJoin, host = "127.0.0.1",
        menu = Menu?.GetType().Name,
        message = Stage switch {
            "title" => "可以用 join 加入本机房主；默认新角色名bot、喜好星露谷、女性、游戏默认外观。",
            "connecting" => "游戏正在连接房主或等待角色批准。",
            "choose_farmer" => "join 会优先选择同名角色，否则选择空闲小屋；不接管其他已创建角色。",
            "create_character" => "可以用 join 完成当前新农场工创建，不修改房主角色。",
            "host_loaded" => "这是房主角色，插件拒绝控制；请在独立客户端加入。",
            "in_game" => "已进入联机农场。",
            _ => "等待标题/联机界面。分屏手柄加入提示属于房主窗口，应取消该提示后使用独立客户端。",
        },
        slots = Menu is FarmhandMenu menu ? Choices(menu) : new(),
        connectionMessage = Menu is FarmhandMenu farmhands ? farmhands.client?.connectionMessage : null,
    };

    public void Start(JsonElement message)
    {
        if (!CanJoin) throw new InvalidOperationException("当前界面不允许加入农场，且不能控制房主。");
        profile = JoinProfile.Parse(message);
        creationSubmitted = false;
        if (Stage == "title" || Menu is FarmhandMenu { gettingFarmhands: false, approvingFarmhand: false })
        {
            if (Menu is FarmhandMenu previous) previous.client?.disconnect(false);
            var multiplayer = helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
            var menu = new FarmhandMenu(multiplayer.InitClient(new LidgrenClient("127.0.0.1")));
            if (Game1.activeClickableMenu is TitleMenu) TitleMenu.subMenu = menu;
            else Game1.activeClickableMenu = menu;
        }
    }

    public string? Advance()
    {
        if (profile == null) throw new InvalidOperationException("加入流程已停止。");
        if (Playing)
        {
            if (Game1.player.Name != profile.Name) throw new InvalidOperationException("已连接角色的名字不匹配，停止自动操作。");
            return $"已通过游戏原生联机流程以 {profile.Name} 加入农场。";
        }
        if (Stage is "host_loaded" or "unsupported_split_screen") throw new InvalidOperationException("角色模式不匹配，停止加入。");
        if (Menu is FarmhandMenu menu)
        {
            if (menu.client?.timedOut == true) throw new InvalidOperationException("连接房主失败；请确认农场对局域网开放且有空闲小屋。");
            if (menu.gettingFarmhands || menu.approvingFarmhand) return null;
            int index = JoinProfile.ChooseSlot(Choices(menu), profile.Name);
            ((FarmhandMenu.FarmhandSlot)menu.MenuSlots[index]).Activate();
        }
        else if (Creating && Menu is CharacterCustomization customization && !creationSubmitted)
        {
            helper.Reflection.GetField<TextBox>(customization, "nameBox").GetValue().Text = profile.Name;
            helper.Reflection.GetField<TextBox>(customization, "favThingBox").GetValue().Text = profile.FavoriteThing;
            var gender = customization.genderButtons.Single(button => button.name == (profile.Gender == "male" ? "Male" : "Female"));
            customization.receiveLeftClick(gender.bounds.Center.X, gender.bounds.Center.Y);
            if (!customization.canLeaveMenu()) throw new InvalidOperationException("游戏尚未接受角色信息，未确认创建。");
            creationSubmitted = true;
            customization.receiveLeftClick(customization.okButton.bounds.Center.X, customization.okButton.bounds.Center.Y);
        }
        return null;
    }

    public void Stop()
    {
        profile = null;
        if (!Playing && Menu is FarmhandMenu menu) menu.client?.disconnect(false);
    }
}
