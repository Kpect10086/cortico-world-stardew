namespace CorticoStardew;

public static class ChestColor
{
    public static string Name(byte r, byte g, byte b) => $"{r:X2}{g:X2}{b:X2}" switch
    {
        "000000" => "默认材质色", "5555FF" => "蓝色", "77BFFF" => "浅蓝色", "00AAAA" => "青色",
        "00EAAF" => "薄荷绿", "00AA00" => "绿色", "9FEC00" => "黄绿色", "FFEA12" => "黄色",
        "FFA712" => "橙黄色", "FF6912" => "橙色", "FF0000" => "红色", "870023" => "暗红色",
        "FFADC7" => "浅粉色", "FF75C3" => "粉色", "AC00C6" => "紫红色", "8F00FF" => "紫色",
        "590B8E" => "深紫色", "404040" => "深灰色", "646464" => "灰色", "C8C8C8" => "浅灰色",
        "FEFEFE" => "白色", _ => "自定义色",
    };
}
