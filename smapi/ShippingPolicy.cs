namespace CorticoStardew;

public static class ShippingPolicy
{
    public const int Keep = 5;
    // 原生水果、蔬菜和花；高价、品质、任务及特殊物品留给用户决定。
    public static int Available(int category, int price, int quality, bool protectedItem, bool shippable, int stack) =>
        shippable && !protectedItem && quality == 0 && price is >= 0 and <= 300 && category is -75 or -79 or -80
            ? Math.Max(0, stack - Keep) : 0;
}
