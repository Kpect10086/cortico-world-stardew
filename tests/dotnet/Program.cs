using CorticoStardew;
using System.Text.Json;
using System.Text.Json.Nodes;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Rejected(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
using (var empty = JsonDocument.Parse("{}"))
{
    var profile = JoinProfile.Parse(empty.RootElement);
    Check(profile == new JoinProfile("bot", "星露谷", "female"), "Default profile changed");
}
foreach (string badProfile in new[] { "{\"name\":\"[74]\"}", "{\"name\":\"   \"}", "{\"gender\":\"invalid\"}" })
{
    using var invalid = JsonDocument.Parse(badProfile);
    Rejected(() => JoinProfile.Parse(invalid.RootElement));
}
var slots = new[] { new FarmhandChoice(0, "other-player", true, false), new FarmhandChoice(1, "", false, false), new FarmhandChoice(2, "bot", true, false) };
Check(JoinProfile.ChooseSlot(slots, "bot") == 2, "Must reuse own customized character");
Check(JoinProfile.ChooseSlot(slots.Take(2), "bot") == 1, "Must use empty cabin");
Rejected(() => JoinProfile.ChooseSlot(slots.Take(1), "bot"));
Rejected(() => JoinProfile.ChooseSlot(new[] { slots[2] with { Locked = true }, slots[1] }, "bot"));
Rejected(() => JoinProfile.ChooseSlot(new[] { slots[2], slots[2] with { Index = 3 } }, "bot"));
Console.WriteLine("Join profile and safe cabin selection checks passed");
var observed = JsonNode.Parse("""
{"ready":true,"farmhand":true,"player":{"x":2,"y":9},"date":{"time":600},"inventory":[null],"menu":null,"nearby":{"grid":["..."],"players":[{"x":1}],"objects":[],"terrain":[],"furniture":[]}}
""")!.AsObject();
string basis = StateRevision.ActionBasis(observed);
observed["date"]!["time"] = 610;
observed["nearby"]!["players"]![0]!["x"] = 5;
observed["nearby"]!["grid"]![0] = ".#.";
observed["player"]!["stamina"] = 268;
observed["player"]!["money"] = 900;
Check(StateRevision.ActionBasis(observed) == basis, "Background updates must not invalidate an action");
foreach (string change in new[] { "position", "inventory", "menu", "object", "pause" })
{
    var changed = JsonNode.Parse(observed.ToJsonString())!.AsObject();
    if (change == "position") changed["player"]!["x"] = 3;
    if (change == "inventory") changed["inventory"]![0] = "new-item";
    if (change == "menu") changed["menu"] = JsonNode.Parse("{\"kind\":\"DialogueBox\"}");
    if (change == "object") changed["nearby"]!["objects"]!.AsArray().Add("changed-object");
    if (change == "pause") changed["localPaused"] = true;
    Check(StateRevision.ActionBasis(changed) != basis, $"Action must refresh after {change} changes");
}
Console.WriteLine("Action revision background/critical-state checks passed");
Check(ShippingPolicy.Available(-75, 50, 0, false, true, 12) == 7, "Only surplus above five may ship");
foreach (int category in new[] { -74, -99, -8, -28, -25, -4 }) Check(ShippingPolicy.Available(category, 10, 0, false, true, 99) == 0, "Seeds, tools, materials and fish must be retained");
Check(ShippingPolicy.Available(-79, 750, 0, false, true, 99) == 0, "High-value crops must be retained");
Check(ShippingPolicy.Available(-79, 50, 2, false, true, 99) == 0, "Quality crops must be retained");
Check(ShippingPolicy.Available(-75, 50, 0, true, true, 99) == 0, "Quest/special items must be retained");
Check(ShippingPolicy.Available(-75, 50, 0, false, false, 99) == 0, "Unshippable items must be retained");
Check(ShippingPolicy.Available(-75, 50, 0, false, true, 4) == 0, "Low stock must be retained");
Console.WriteLine("Conservative shipping policy checks passed");
Check(ChestColor.Name(0, 0, 0) == "默认材质色", "Native black sentinel is not a black chest");
Check(ChestColor.Name(85, 85, 255) == "蓝色", "Native blue palette entry must be identified");
Check(ChestColor.Name(255, 0, 0) == "红色", "Native red palette entry must be identified");
Check(ChestColor.Name(254, 254, 254) == "白色", "Native white palette entry must be identified");
Check(ChestColor.Name(12, 34, 56) == "自定义色", "Unknown palette must not invent a color name");
Console.WriteLine("Native chest palette checks passed");
Check(ChestAccess.Direction(10, 11, 10, 10, true) == 0, "Chest above");
Check(ChestAccess.Direction(9, 10, 10, 10, true) == 1, "Chest right");
Check(ChestAccess.Direction(10, 9, 10, 10, true) == 2, "Chest below");
Check(ChestAccess.Direction(11, 10, 10, 10, true) == 3, "Chest left");
Rejected(() => ChestAccess.Direction(10, 11, 10, 10, false));
Rejected(() => ChestAccess.Direction(10, 10, 10, 10, true));
Rejected(() => ChestAccess.Direction(9, 9, 10, 10, true));
Console.WriteLine("Chest identity and adjacency checks passed");

if (args.Length != 2) throw new ArgumentException("port and token required");
using var transport = new Transport(new ModConfig { Port = int.Parse(args[0]), Token = args[1] });
var running = transport.Run();
var end = DateTime.UtcNow.AddSeconds(10);
while (!transport.Connected && DateTime.UtcNow < end) await Task.Delay(5);
if (!transport.Connected) throw new Exception("Handshake failed");
var state = new { version = "csharp-session:1", ready = true, farmhand = true, busy = false };
transport.Send(new { type = "state", state });
while (DateTime.UtcNow < end)
{
    if (transport.Incoming.TryDequeue(out var inbound))
    {
        var message = inbound.Message;
        if (message.GetProperty("type").GetString() != "action") continue;
        if (message.GetProperty("version").GetString() != state.version) throw new Exception("Wrong action version");
        var id = message.GetProperty("id").GetString();
        transport.Send(new { type = "state", state = new { version = "csharp-session:2", ready = true, farmhand = true, busy = true } });
        await Task.Delay(30);
        transport.Send(new { type = "result", id, status = "unchanged", detail = "Actual C# transport round trip", state = new { version = "csharp-session:3", ready = true, farmhand = true, busy = false } });
        await Task.Delay(100);
        Console.WriteLine("C# transport round trip passed");
        transport.Dispose();
        await running;
        return;
    }
    await Task.Delay(5);
}
throw new Exception("No action received");
