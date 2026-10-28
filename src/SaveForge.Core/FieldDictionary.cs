namespace SaveForge.Core;

/// <summary>
/// 字段名 → 中文说明。
///
/// 存档里的键名是引擎内部变量（_gold、_steps、_hp…），普通用户看不懂。
/// 这张表覆盖 RPG Maker 各版本（MV/MZ 用 _ 前缀，XP/VX 用 @ 前缀）的常见字段，
/// 把他们翻译成游戏内的说法。
///
/// 查不到时不硬猜：保留原始键名，界面上照常显示。
/// </summary>
public static class FieldDictionary
{
    /// <summary>键名（已去掉 _ / @ 前缀并转小写）→ 中文名 + 说明。</summary>
    private static readonly Dictionary<string, (string Name, string Hint)> Entries = new()
    {
        // ---------- 队伍 / 库存 ----------
        ["gold"] = ("金钱", "队伍当前持有的金币"),
        ["steps"] = ("步数", "累计行走的步数"),
        ["items"] = ("物品", "背包里的消耗品与素材，键是物品 ID"),
        ["weapons"] = ("武器", "背包里的武器"),
        ["armors"] = ("防具", "背包里的防具"),
        ["actors"] = ("角色", "角色的运行时数据；队伍里的 _actors 表示参战成员"),
        ["inbattle"] = ("战斗中", "是否处于战斗状态"),
        ["lastitem"] = ("最后用的物品", "最近一次使用的物品"),
        ["menuactorid"] = ("菜单角色", "菜单中当前选中的角色 ID"),
        ["targetactorid"] = ("目标角色", "技能/物品的作用目标 ID"),

        // ---------- 角色属性 ----------
        ["actorid"] = ("角色 ID", "该角色在数据库中的编号"),
        ["name"] = ("名称", "显示名称"),
        ["nickname"] = ("称号", "角色称号"),
        ["profile"] = ("简介", "角色介绍文本"),
        ["classid"] = ("职业 ID", "该角色所属职业"),
        ["level"] = ("等级", "角色当前等级"),
        ["exp"] = ("经验值", "累计经验"),
        ["hp"] = ("当前 HP", "生命值"),
        ["mp"] = ("当前 MP", "魔法值"),
        ["tp"] = ("TP", "战术点数"),
        ["mhp"] = ("最大 HP", "生命值上限"),
        ["mmp"] = ("最大 MP", "魔法值上限"),
        ["atk"] = ("攻击力", "物理攻击"),
        ["def"] = ("防御力", "物理防御"),
        ["mat"] = ("魔法攻击", "魔法攻击力"),
        ["mdf"] = ("魔法防御", "魔法防御力"),
        ["agi"] = ("敏捷", "影响行动顺序"),
        ["luk"] = ("幸运", "影响暴击与状态命中"),
        ["skills"] = ("已学技能", "角色掌握的技能 ID 列表"),
        ["equips"] = ("装备", "已装备的武器/防具槽位"),
        ["states"] = ("状态", "当前附加的状态（中毒、加速等）"),
        ["actions"] = ("战斗行动", "AI 控制的行动配置"),

        // ---------- 系统 ----------
        ["savecount"] = ("存档次数", "读档界面显示的存档序号"),
        ["battlecount"] = ("战斗次数", "累计进入战斗的次数"),
        ["wincount"] = ("胜利次数", "战斗获胜次数"),
        ["escapecount"] = ("逃跑次数", "战斗逃跑次数"),
        ["framespn"] = ("游戏帧数", "存档时的累计帧数，可换算游戏时长"),
        ["framesonsave"] = ("游戏帧数", "存档时的累计帧数，可换算游戏时长"),
        ["versionid"] = ("版本 ID", "存档版本标识，用于校验兼容性"),
        ["switches"] = ("开关", "全局开关，键是开关 ID（开/关）"),
        ["variables"] = ("变量", "全局变量，键是变量 ID"),
        ["selfswitches"] = ("独立开关", "按地图/事件区分的局部开关"),
        ["saveenabled"] = ("允许存档", "是否开放存档菜单"),
        ["menuenabled"] = ("允许菜单", "是否开放主菜单"),
        ["encounterenabled"] = ("允许遇敌", "是否随机遇敌"),
        ["formationenabled"] = ("允许编队", "是否可调整队形"),
        ["battlebgm"] = ("战斗音乐", "进入战斗时播放的 BGM"),
        ["victoryme"] = ("胜利音效", "战斗胜利时播放的音效"),
        ["defeatme"] = ("失败音效", "战斗失败时播放的音效"),
        ["savedbgm"] = ("已保存的音乐", "上一次存档时正在播放的 BGM"),
        ["bgmonsave"] = ("存档时音乐", "存档时正在播放的 BGM"),
        ["windowtone"] = ("窗口色调", "对话框的 RGB 着色"),

        // ---------- 画面 / 地图 ----------
        ["brightness"] = ("亮度", "画面亮度（-255 ~ 255）"),
        ["tone"] = ("画面色调", "整体 RGB 着色"),
        ["flashcolor"] = ("闪烁颜色", "屏幕闪光效果的颜色"),
        ["shake"] = ("震动", "画面震动强度"),
        ["weathertype"] = ("天气类型", "0 无 / 1 雨 / 2 暴风 / 3 雪"),
        ["weatherpower"] = ("天气强度", "天气效果的强度"),
        ["mapid"] = ("地图 ID", "当前所在地图的编号"),
        ["displayx"] = ("屏幕 X", "画面上的横向坐标"),
        ["displayy"] = ("屏幕 Y", "画面上的纵向坐标"),
        ["interpreter"] = ("事件解释器", "正在运行的事件指令状态"),
        ["events"] = ("地图事件", "地图上各事件的运行时状态"),
        ["fogofwar"] = ("战争迷雾", "迷雾探索进度"),
        ["x"] = ("X 坐标", "在地图上的横向位置（格）"),
        ["y"] = ("Y 坐标", "在地图上的纵向位置（格）"),
        ["realx"] = ("实际 X", "像素级的横向位置"),
        ["realy"] = ("实际 Y", "像素级的纵向位置"),
        ["direction"] = ("朝向", "2 下 / 4 左 / 6 右 / 8 上"),
        ["movespeed"] = ("移动速度", "角色行走速度等级"),
        ["moveroute"] = ("移动路线", "自动移动的路线配置"),
        ["followers"] = ("跟随者", "队伍跟随角色的状态"),
        ["transferring"] = ("正在传送", "是否处于地图切换过程"),
        ["newmapid"] = ("目标地图", "传送目标地图 ID"),
        ["depth"] = ("深度", "事件所在的层"),
        ["branch"] = ("分支", "条件分支的判定结果"),
        ["params"] = ("参数", "指令参数列表"),
        ["list"] = ("指令列表", "事件指令的完整列表"),
        ["index"] = ("当前指令", "正在执行的指令序号"),
        ["waitcount"] = ("等待计数", "剩余等待帧数"),
        ["waitmode"] = ("等待模式", "等待的触发条件"),

        // ---------- 通用 ----------
        // 注意：data / id / class 这类词在不同上下文含义完全不同
        //（actors 下的 _data 是对象，玩家自定义字段 data 可能是一段 JSON 文本），
        // 宁可保留原始键名，也不做容易误导的翻译。
        ["timer"] = ("计时器", "倒计时状态"),
        ["system"] = ("系统", "全局系统状态"),
        ["party"] = ("队伍", "队伍与库存"),
        ["screen"] = ("画面", "画面效果状态"),
        ["map"] = ("地图", "当前地图运行时状态"),
        ["player"] = ("玩家", "玩家角色的位置与状态"),
        ["name"] = ("名称", "显示名称"),
    };

    /// <summary>尝试取中文名与说明。</summary>
    public static bool TryDescribe(string key, out string name, out string? hint)
    {
        name = key;
        hint = null;

        if (string.IsNullOrEmpty(key)) return false;

        string normalized = Normalize(key);
        if (!Entries.TryGetValue(normalized, out var entry)) return false;

        name = entry.Name;
        hint = entry.Hint;
        return true;
    }

    /// <summary>取中文名；没有对应项时返回原始键。</summary>
    public static string DescribeName(string key) =>
        TryDescribe(key, out string name, out _) ? name : key;

    /// <summary>取说明文本；没有时返回 null。</summary>
    public static string? DescribeHint(string key) =>
        TryDescribe(key, out _, out string? hint) ? hint : null;

    /// <summary>去掉 _ / @ 前缀并转小写，让 MV/MZ 与 XP/VX 走同一套表。</summary>
    private static string Normalize(string key)
    {
        string value = key.Trim();

        while (value.Length > 0 && (value[0] == '_' || value[0] == '@'))
            value = value[1..];

        return value.ToLowerInvariant();
    }
}
