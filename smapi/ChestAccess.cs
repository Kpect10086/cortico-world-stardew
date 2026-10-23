namespace CorticoStardew;

public static class ChestAccess
{
    public static int Direction(int playerX, int playerY, int chestX, int chestY, bool sameChest)
    {
        if (!sameChest || Math.Abs(playerX - chestX) + Math.Abs(playerY - chestY) != 1)
            throw new InvalidOperationException("箱子或相邻位置已改变，停止开箱。");
        return chestY < playerY ? 0 : chestX > playerX ? 1 : chestY > playerY ? 2 : 3;
    }
}
