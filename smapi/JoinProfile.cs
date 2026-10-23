using System.Text.Json;

namespace CorticoStardew;

public sealed record JoinProfile(string Name, string FavoriteThing, string Gender)
{
    public static JoinProfile Parse(JsonElement message)
    {
        string Text(string key, string fallback)
        {
            string text = message.TryGetProperty(key, out var value) ? value.GetString()?.Trim() ?? "" : fallback;
            if (text.Length is < 1 or > 20 || text.Any(character => char.IsControl(character) || character is '[' or ']'))
                throw new InvalidOperationException($"{key} 必须为1至20个字符，且不能包含控制字符或方括号。");
            return text;
        }
        string gender = message.TryGetProperty("gender", out var field) ? field.GetString() ?? "" : "female";
        if (gender is not ("male" or "female")) throw new InvalidOperationException("gender 必须为 male 或 female。");
        return new JoinProfile(Text("name", "bot"), Text("favoriteThing", "星露谷"), gender);
    }

    public static int ChooseSlot(IEnumerable<FarmhandChoice> choices, string name)
    {
        var slots = choices.ToList();
        var matching = slots.Where(slot => slot.Customized && slot.Name == name).ToList();
        if (matching.Count > 1) throw new InvalidOperationException("存在多个同名角色，需要用户先在游戏中选择。");
        if (matching.Count == 1)
        {
            if (matching[0].Locked) throw new InvalidOperationException("同名角色属于其他玩家，不能接管。");
            return matching[0].Index;
        }
        var empty = slots.FirstOrDefault(slot => !slot.Customized && !slot.Locked);
        return empty?.Index ?? throw new InvalidOperationException("没有自己的角色或空闲小屋，请房主准备一间空闲小屋。");
    }
}

public sealed record FarmhandChoice(int Index, string Name, bool Customized, bool Locked);
