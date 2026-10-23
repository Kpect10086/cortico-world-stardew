using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorticoStardew;

public static class StateRevision
{
    // 时间、人物移动和床上恢复体力不改变索引；原生动作仍检查当前资源和碰撞。
    public static string ActionBasis(JsonObject state) => JsonSerializer.Serialize(new {
        ready = state["ready"], farmhand = state["farmhand"], localPaused = state["localPaused"], world = state["world"],
        player = new { id = state["player"]?["id"], location = state["player"]?["location"], x = state["player"]?["x"], y = state["player"]?["y"], facing = state["player"]?["facing"],
            selectedSlot = state["player"]?["selectedSlot"], usingTool = state["player"]?["usingTool"], canMove = state["player"]?["canMove"], inBed = state["player"]?["inBed"] },
        inventory = state["inventory"], menu = state["menu"], onboarding = state["onboarding"],
        objects = state["nearby"]?["objects"], terrain = state["nearby"]?["terrain"], furniture = state["nearby"]?["furniture"],
    });
}
