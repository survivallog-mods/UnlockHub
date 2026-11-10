using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using SurvivalLog.ModShared;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.UnlockHub;

/// <summary>
/// 解锁中心（v1.8.0 前名"角色专属解锁 RoleUnlock"）：角色线（藤蔓种子/防御配方/规划/花瓶）
/// + 交易线（货架买后即刷）的通用解锁合集。
///
/// 机制（2026-10-21 反汇编实证，见 DESIGN.md）：
/// 1) 藤蔓线：Config_GlobalSetting["Plant_RareSeedDiscover"] 是四合一条目——
///    Params1=藤蔓种子物品ID / Params2=发现概率 / Params3=角色白名单（';'分隔，空=不限）。
///    IvyPlanManager.IsIvyRoleAllowed、IsInstallTarget、AE_HarvestPlant.TryDiscoverRareSeed
///    三处角色判定全部只读 Params3，且"空字符串=放行"是游戏内置约定（三处空值路径均已验证）。
///    本 mod 把 Params3 清空 → 任何角色收获作物都可能发现藤蔓种子，并可种植/安装/获得护盾。
///    注意：不能改成 "1;2" 多值——IsIvyRoleAllowed 的内联解析器遇非数字即停，会静默丢角色。
/// 2) 防御配方线（燃烧瓶等）：Config_DefenseLv.unlock_recipes_P1~P4 按角色分列，
///    ConfigManager.GetDefenseUnlockRecipes 按当前角色只取对应列（GetDefenseRequiredLevelForRecipe
///    也走它）→ 错角色配方永远查不到解锁等级=工作台不可见。
///    本 mod 把每行 P1~P4 四列互相合并成并集（列列相等）→ 取哪列都拿到全量，
///    解锁仍受各自防御等级门槛约束（不是白送，只是"可解锁"）。
/// 3) 生存规划互通：清功能类专属天赋 PlayerSelectId（家具位黑名单排除）+ 花瓶配方 325 等级化解锁。
///    （2026-10-24 起两线各有独立开关 Plan.ClearPool / Plan.VaseRecipe，默认 true；false=还原官方。）
/// 4) 交易解锁（2026-11-02 起，首个非角色线功能）：postfix ConsumeActiveShelfStock 删货架快照，
///    下次打开交易点走 DrawActiveShelf 官方兜底分支全新上架（官方旧版"买完即补"的现行等价路径）。
///    机制实证见 docs/superpowers/specs/2026-11-01-drone-trade-shelf-refresh-design.md；实现见 TradeShelfRefresher.cs。
/// 5) 探索解锁（2026-11-08 起）：Config_GlobalSetting["Explore_DailyOnceMaps"].Params3 是"每日一次"
///    探索点的地图ID列表（';'分隔；条目缺失时游戏内置默认 "1003"=科技公司）。ExploreManager.IsDailyOnceMap
///    直读该条目且"空=无任何地图受限"（IsNullOrEmpty 早退，2026-11-05 build 26454866 反汇编实证）；
///    进入闸门 OnCheckAndGo→IsDailyOnceUsedToday / 已去标记 MarkDailyOnceEntered / 跨日重置
///    PostDailyOnceForMap 三条消费路径全部以它为第一道判定。清空 Params3 → 每日一次探索点不限次
///    （连"已去过"计数器都不再写，零存档副作用）。官方未来把更多地图加入该列表也一并放开。
/// 6) 重生解锁（2026-11-10 起）：死亡后的"大重生"机会上限=Config_GlobalSetting["RebirthMaxChances"]
///    .Params1，经 GameKey.get_GlobalSetting_RebirthMaxChances 静态读取器分发——死亡页按钮/
///    重生选择页数量与确认/主菜单存档列表重生入口等 12 处调用点全部经由它（公式
///    剩余=max(0,上限−存档已用次数)）。该 getter 有进程级静态缓存（首读后永不回读表）→ 表编辑
///    不可靠，走 postfix 改返回值 999；已用次数照常由官方代码写入存档（知情：关开关后超上限的
///    档回到无机会）。实现见 RebirthChanceUnlocker.cs。
/// 7) 属性上限解锁（2026-11-10 起）：属性"上限值"的封顶=Config_AttributeStandard 行 101~105
///    （MaxSatiety~MaxVitality，Des_Local"饱腹（上限）"等）的 Max=500——AttributeComponent
///    .OnCreate 首建 AttrDict 时快照进 Attr.Max，GetTotalValue 读值时按 [Min,Max] 钳制（上限值
///    本身超 500 一律读成 500，吃/回/增益全链路共用）。本 mod 把其中四行（101/102/103/105=主界面
///    饱腹/心态/精力/生命，RA_Open 反汇编实证 HUD 只插 1/2/3/5）的 Max 500→可配目标（cfg
///    Attribute.CapValue，区间 500~9999 默认 999）→ 上限可堆到目标值（不白送数值，只解除封顶）。
///    104 健康=隐藏属性（无 HUD 条但 Buff 真实运作），作者定案不动。时机：改表落主菜单阶段
///    （Ticker 首轮），早于进档创建组件；运行中关闭=表立即还原，已在场角色的 Attr.Max 缓存要
///    过图重进后回官方 500。
///
/// 表线（1~3、5、7）保持纯内存表编辑（零 Harmony——il2cpp 泛型 List 参数补丁会静默失败）；
/// 交易解锁（4）与重生解锁（6）是两条 Harmony 补丁线（套件有 EndlessGuard/CabinetEverywhere
/// 等成熟先例；逐类挂载+异常隔离，postfix 只碰 int 标量避开泛型坑）。各线正交，互不依赖。
/// 幂等：表线进程内首读值记为原值，重复执行永远按原值推目标态；全部功能有 cfg 开关（关=还原）。
///
/// General.Enabled 总开关（2026-10-24，ModCenter 启停位）：false=全功能目标态切"还原官方"
/// （复用各 Apply 的现役还原路径：Params3 回原白名单/DefenseLv 回分列/Talent 回白名单/
/// 花瓶回官方等级）；下次启动不加载（不创建 Ticker，零日志零改写）。
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class UnlockHubPlugin : BasePlugin
{
    public const string Guid = "com.local.survivallog.unlockhub";
    public const string Name = "UnlockHub";
    // 跟随套件 version.json，发版时与套件同步（版本号由作者定义）
    public const string Version = "1.12.0";

    internal static ManualLogSource Log;

    // General.Enabled 总开关（ModCenter 启停位，2026-10-24）：false=全功能还原官方并停写；
    // 下次启动不加载（不创建 Ticker，零日志零改写）。
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> UnlockVine;
    internal static ConfigEntry<bool> UnlockFlowerSeeds;
    internal static ConfigEntry<bool> ShareDefenseRecipes;
    internal static ConfigEntry<bool> ClearPool;
    internal static ConfigEntry<bool> VaseRecipe;
    // 交易解锁（2026-11-02，本 mod 首个非角色线功能）：postfix 删货架快照，见 TradeShelfRefresher.cs
    internal static ConfigEntry<bool> UnlockTrade;
    // 探索解锁（2026-11-08）：清空 Explore_DailyOnceMaps.Params3，每日一次探索点（科技公司）不限次
    internal static ConfigEntry<bool> UnlockDailyOnce;
    // 重生解锁（2026-11-10）：postfix 改 RebirthMaxChances 读取结果为 999，见 RebirthChanceUnlocker.cs
    internal static ConfigEntry<bool> UnlockChances;
    // 属性上限解锁（2026-11-10）：AttributeStandard 101/102/103/105 封顶 500→CapValue，见 RoleTable.ApplyAttrCap
    internal static ConfigEntry<bool> UnlockCap;
    // 属性上限解锁的目标封顶值（500~9999，默认 999；仅 UnlockCap 开启时生效）
    internal static ConfigEntry<int> CapValue;
    internal static volatile bool Rerun = true;

    public override void Load()
    {
        Log = base.Log;
        // General.Enabled 放最前 Bind（保证 cfg 键已生成后才可能早退）
        Enabled = Config.Bind("General", "Enabled", true, "总开关：false=全部改回游戏原样；下次启动完全不加载本插件。");
        UnlockVine = Config.Bind("Vine", "UnlockVine", true,
            "藤蔓全角色：任何角色收庄稼时都可能捡到爬山虎种子、能种藤蔓挡尸潮（原本只有大学生会）。false=改回游戏原样。");
        UnlockFlowerSeeds = Config.Bind("Vine", "UnlockFlowerSeeds", true,
            "花种子全角色：任何角色收庄稼时都可能捡到花朵种子、能种花插瓶（原本只有大学生会）。false=改回游戏原样。");
        ShareDefenseRecipes = Config.Bind("Defense", "ShareDefenseRecipes", true,
            "防御配方互通：让所有角色都能做燃烧瓶、电网这类防线道具（当前游戏版本已自带此效果，此项留作保险，不用管）。false=改回游戏原样。");
        ClearPool = Config.Bind("Plan", "ClearPool", true,
            "生存规划全角色：每日规划商店里，别的角色的专属规划（种花、做菜、存粮、搬东西这些共 46 项）也会刷出来，谁都能买（已买的不退）。注意：各角色的房屋扩建类规划不会互通。false=改回游戏原样（只能刷到自己的）。");
        VaseRecipe = Config.Bind("Plan", "VaseRecipe", true,
            "花瓶全角色：制作等级到 1 级，谁都能在工作台学会做花瓶（原本只有大学生的剧情会送）。false=改回游戏原样。");
        UnlockTrade = Config.Bind("Trade", "UnlockTrade", true,
            "交易解锁：无人机交易每次买完，该交易点的货架下次打开时自动重新上架全新货物（官方旧版行为；官方现行是买掉后要等第二天才补货）。false=改回游戏原样。");
        UnlockDailyOnce = Config.Bind("Explore", "UnlockDailyOnce", true,
            "探索解锁：地图上每天只能去一次的探索点（当前是科技公司）改为每天想去几次去几次。false=改回游戏原样。");
        UnlockChances = Config.Bind("Rebirth", "UnlockChances", true,
            "重生解锁：死亡后的\"大重生\"机会改为基本不限次（官方每档只有少数几次，用完就真结局）。注意：开着期间重生的次数会照常记进存档，关掉后超过官方上限的档会回到无机会。false=改回游戏原样。");
        UnlockCap = Config.Bind("Attribute", "UnlockCap", true,
            "属性上限解锁：饱腹、心态、精力、生命这四条属性的\"上限值\"官方封顶 500，改为最高可堆到 CapValue 设的值（只解除封顶，不白送数值；想堆满还得靠游戏里加上限的规划/天赋/食物）。false=改回游戏原样（已在场的角色要过图重进后生效）。");
        CapValue = Config.Bind("Attribute", "CapValue", 999,
            new ConfigDescription("属性上限解锁的封顶值（500~9999）：四条属性的上限值最高能堆到多少，默认 999。只在 UnlockCap 开启时生效；改小后已堆高的上限要过图重进后才会压回新封顶。",
                new AcceptableValueRange<int>(RoleTable.AttrCapMin, RoleTable.AttrCapMax)));

        System.EventHandler reload = (object s, System.EventArgs e) => { Rerun = true; };
        Enabled.SettingChanged += reload;
        UnlockVine.SettingChanged += reload;
        UnlockFlowerSeeds.SettingChanged += reload;
        ShareDefenseRecipes.SettingChanged += reload;
        ClearPool.SettingChanged += reload;
        VaseRecipe.SettingChanged += reload;
        UnlockTrade.SettingChanged += reload;
        UnlockDailyOnce.SettingChanged += reload;
        UnlockChances.SettingChanged += reload;
        UnlockCap.SettingChanged += reload;
        CapValue.SettingChanged += reload;

        // General.Enabled=false：不启 Ticker、零日志零改写（表值本会话从未改过；
        // 运行中关闭由 RoleTicker 按全功能还原目标态处理）
        if (!Enabled.Value) return;

        // 交易解锁：逐类挂载+异常隔离（EndlessGuard 2026-10-25 事故教训同款：PatchAll 整包挂载时
        // 单个补丁 IL 编译错会炸掉整个 Load()。逐类隔离后坏一个不连坐，表线 Ticker 照常工作）
        var harmony = new HarmonyLib.Harmony(Guid);
        foreach (var t in typeof(UnlockHubPlugin).Assembly.GetTypes())
        {
            if (!t.IsClass || !t.IsDefined(typeof(HarmonyLib.HarmonyPatch), false)) continue;
            try { harmony.CreateClassProcessor(t).Patch(); }
            catch (Exception e) { Log.LogWarning("patch 挂载失败 " + t.Name + ": " + e.Message); }
        }

        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<RoleTicker>())
            ClassInjector.RegisterTypeInIl2Cpp<RoleTicker>();
        var go = new GameObject("UnlockHub.Ticker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<RoleTicker>();
        Log.LogInfo("[UnlockHub] loaded：藤蔓种子全角色可发现 + 防御配方四角色互通 + 生存规划/花瓶配方互通 + 交易解锁（货架买后即刷）+ 探索解锁（每日一次探索点不限次）+ 重生解锁（大重生机会基本不限次）+ 属性上限解锁（饱腹/心态/精力/生命封顶 500→可配 500~9999）（表线纯内存编辑，全部功能可在配置关闭还原；General.Enabled=false 全停）");
        SurvivalLog.ModShared.WelcomeTip.Install(Log);
    }
}

/// <summary>
/// 全部表改写。目标态由配置推导，原值取进程内首读；每轮把当前值同步到目标态
/// （防读档/切场景配置表重载覆盖，同其他套件 mod 的轮询幂等模式）。
/// </summary>
internal static class RoleTable
{
    private const string KeyRareSeed = "Plant_RareSeedDiscover";
    private const string KeyFlowerSeed = "Plant_FlowerSeedDiscover";
    private const string KeyDailyOnceMaps = "Explore_DailyOnceMaps";

    // GlobalSetting 原值（null=尚未见过该条目）
    private static string _origRareAllow, _origFlowerAllow, _origDailyOnceMaps;

    // DefenseLv 每行四列原值：rowKey(Lv) -> [P1,P2,P3,P4]
    private static readonly Dictionary<int, List<int>[]> OrigCols = new();

    /// <summary>一轮同步。返回 null=表未就绪；否则 (日志行列表)。</summary>
    internal static List<string> Apply(GameCore.HotUpdate.ConfigManager cm, bool unlockVine,
        bool unlockFlower, bool shareRecipes, bool clearPool, bool vaseRecipe, bool unlockDailyOnce,
        int attrCap, bool verbose, bool listAll)
    {
        var lines = new List<string>();

        // ---- 1) GlobalSetting：藤蔓 / 花种白名单（防御门槛保留官方值，不改动）
        //         + 每日一次探索点列表（空=无任何地图受限，2026-11-08）----
        if (!ApplyAllowList(cm, KeyRareSeed, unlockVine, ref _origRareAllow, "藤蔓", verbose, lines) ||
            !ApplyAllowList(cm, KeyFlowerSeed, unlockFlower, ref _origFlowerAllow, "花种", verbose, lines) ||
            !ApplyAllowList(cm, KeyDailyOnceMaps, unlockDailyOnce, ref _origDailyOnceMaps,
                "每日一次地图", verbose, lines, "列表"))
            return null;

        // ---- 2) DefenseLv：P1~P4 列并集 ----
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_DefenseLv> dict;
        try { dict = cm._Config_DefenseLv_Dict; } catch { dict = null; }
        if (dict == null || dict.Count == 0) return null;

        try
        {
            foreach (var kv in dict)
            {
                var row = kv.Value;
                if (row == null) continue;
                int lv;
                try { lv = row.Lv; } catch { continue; }

                var cols = new Il2CppSystem.Collections.Generic.List<int>[4];
                try
                {
                    cols[0] = row.unlock_recipes_P1;
                    cols[1] = row.unlock_recipes_P2;
                    cols[2] = row.unlock_recipes_P3;
                    cols[3] = row.unlock_recipes_P4;
                }
                catch { continue; }

                // 首见快照原值（托管侧，防自身写入污染）
                if (!OrigCols.TryGetValue(lv, out var orig))
                {
                    orig = new List<int>[4];
                    for (int i = 0; i < 4; i++) orig[i] = Snapshot(cols[i]);
                    OrigCols[lv] = orig;
                }

                if (shareRecipes)
                {
                    // 并集（保持等级顺序去重；0 是占位"无"不算配方）
                    var union = new List<int>();
                    foreach (var o in orig)
                        foreach (var id in o)
                            if (id != 0 && !union.Contains(id)) union.Add(id);

                    bool changed = false;
                    for (int i = 0; i < 4; i++)
                        if (SyncList(cols[i], union)) changed = true;

                    if (changed || verbose || listAll)
                    {
                        var joined = string.Join(",", union);
                        lines.Add(changed
                            ? $"DefenseLv {lv} P1~P4 -> 并集 [{joined}]（原 P1[{string.Join(",", orig[0])}] P2[{string.Join(",", orig[1])}] P3[{string.Join(",", orig[2])}] P4[{string.Join(",", orig[3])}]）"
                            : $"DefenseLv {lv} 并集 [{joined}]");
                    }
                }
                else
                {
                    // 还原官方分列
                    bool changed = false;
                    for (int i = 0; i < 4; i++)
                        if (SyncList(cols[i], orig[i])) changed = true;
                    if (changed) lines.Add($"DefenseLv {lv} P1~P4 -> 还原官方分列");
                }
            }
        }
        catch { }

        // ---- 2.5) Talent：专属生存规划互通（Plan.ClearPool，默认 true；false=还原官方白名单）----
        if (!ApplyTalents(cm, clearPool, verbose, listAll, lines)) return null;

        // ---- 2.6) ProductionList：花瓶配方 325（Plan.VaseRecipe，默认 true；false=还原官方等级）----
        if (!ApplyVaseRecipe(cm, vaseRecipe, verbose, listAll, lines)) return null;

        // ---- 2.7) AttributeStandard：四条上限封顶 500→CapValue（Attribute.UnlockCap，2026-11-10）----
        if (!ApplyAttrCap(cm, attrCap, verbose, listAll, lines)) return null;

        return lines;
    }

    // ---- 3) Talent：专属生存规划互通（清 PlayerSelectId；家具位黑名单排除）----
    // 家具位扩建类规划绑定各角色自家地图，跨角色必排除；关键词对游戏更新新增的家具位自动免疫
    private static readonly string[] SlotTalentKeywords = { "位·", "扩建", "腾出一处", "置物位" };

    // Talent 行原白名单：ID -> 原值（进程内首见快照，幂等基准）
    private static readonly Dictionary<int, List<int>> OrigTalentRoles = new();

    // 首跑核对基线（2026-10-23 表数据；不符只报警不阻断，防游戏更新改表误判）
    private const int ExpectShareTalents = 46;
    private const int ExpectKeepSlotTalents = 77;

    /// <summary>清空功能类专属规划的角色白名单（clearPool=false 时还原官方白名单）。
    /// 返回 false=Talent 表未就绪。</summary>
    private static bool ApplyTalents(GameCore.HotUpdate.ConfigManager cm, bool clearPool, bool verbose, bool listAll, List<string> lines)
    {
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_Talent> dict;
        try { dict = cm._Config_Talent_Dict; } catch { dict = null; }
        if (dict == null || dict.Count == 0) return false;

        int kept = 0; bool changed = false;
        try
        {
            foreach (var kv in dict)
            {
                var row = kv.Value;
                if (row == null) continue;

                int id; string name, dec;
                Il2CppSystem.Collections.Generic.List<int> cur;
                try
                {
                    id = row.ID;
                    name = row.Name_Local ?? "";
                    dec = row.Dec_Local ?? "";
                    cur = row.PlayerSelectId;
                }
                catch { continue; }
                if (cur == null) continue;
                // 空白名单=本就全角色——但本会话清过的行（在快照表内）要走还原路径补回
                if (cur.Count == 0 && !OrigTalentRoles.ContainsKey(id)) continue;

                if (System.Array.Exists(SlotTalentKeywords, k => name.Contains(k) || dec.Contains(k)))
                {
                    kept++;
                    continue;
                }

                if (!OrigTalentRoles.TryGetValue(id, out var orig))
                {
                    orig = Snapshot(cur);
                    OrigTalentRoles[id] = orig;
                }
                var target = clearPool ? new List<int>() : orig;
                if (SyncList(cur, target))
                {
                    changed = true;
                    lines.Add(clearPool
                        ? $"Talent {id} {name} 白名单 [{string.Join(",", orig)}] -> 空"
                        : $"Talent {id} {name} 白名单 [] -> 还原官方 [{string.Join(",", orig)}]");
                }
            }
        }
        catch { }

        int touched = OrigTalentRoles.Count; // 快照累计=功能类受限规划总数（幂等，重复轮不重复计数）
        bool baselineOk = touched == ExpectShareTalents && kept == ExpectKeepSlotTalents;
        if (changed || listAll || verbose)
            lines.Add($"Talent 互通：{(clearPool ? "功能类已清空" : "已还原官方白名单")} {touched} 条 / 家具位黑名单保留 {kept} 条" +
                      (baselineOk ? "（基线 46/77 吻合）" : "（⚠ 与基线 46/77 不符，疑似游戏更新改表，请核对上方清单）"));
        return true;
    }

    // ---- 4) ProductionList：花瓶包裹 325 等级化解锁 ----
    // 原生：craft_level=0 仅大学生剧情事件发放；改为 1/1 后全角色制作等级 1 可学（对齐工具柜包裹 389 档位）。
    // P2 档已获者走"解锁列表含→解锁"优先分支，不受影响。纯表编辑，零事件触发零存档写入。
    // Plan.VaseRecipe（2026-10-24）：false=按首读快照还原官方 craft/show 等级。
    private const int VaseRecipeId = 325;
    private static int _origVaseCraft = int.MinValue, _origVaseShow = int.MinValue;

    private static bool ApplyVaseRecipe(GameCore.HotUpdate.ConfigManager cm, bool enabled, bool verbose, bool listAll, List<string> lines)
    {
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_ProductionList> dict;
        try { dict = cm._Config_ProductionList_Dict; } catch { dict = null; }
        if (dict == null || dict.Count == 0) return false;

        try
        {
            foreach (var kv in dict)
            {
                if (kv.Value == null || kv.Key != VaseRecipeId) continue;
                var row = kv.Value;

                int craft, show;
                try { craft = row.craft_level; show = row.show_level; } catch { return true; }
                if (_origVaseCraft == int.MinValue) { _origVaseCraft = craft; _origVaseShow = show; }

                int wantCraft = enabled ? 1 : _origVaseCraft;
                int wantShow = enabled ? 1 : _origVaseShow;
                if (craft != wantCraft || show != wantShow)
                {
                    row.craft_level = wantCraft;
                    row.show_level = wantShow;
                    lines.Add(enabled
                        ? $"Recipe {VaseRecipeId} 花瓶包裹 craft_level {craft}->1 show_level {show}->1（原值已快照）"
                        : $"Recipe {VaseRecipeId} 花瓶包裹还原官方 craft_level {craft}->{wantCraft} show_level {show}->{wantShow}");
                }
                else if (verbose || listAll)
                    lines.Add($"Recipe {VaseRecipeId} 花瓶包裹 = {craft}/{show}（原 {_origVaseCraft}/{_origVaseShow}）");
                return true;
            }
        }
        catch { }
        return true; // 表就绪但无 325 行=游戏无此配方，不阻断其他功能
    }

    // ---- 5) AttributeStandard："上限值"属性封顶解除（Attribute.UnlockCap + Attribute.CapValue）----
    // 上限属性行 101~105 的 Max=500 是整条读值钳制链（GetTotalValue 按 [Min,Max] 钳制，吃/回/
    // 增益/UI 全走它）的天花板；当前值行 1~5 自身 Max=int.MaxValue 不受限。本 mod 只改其中四行：
    // 101/102/103/105=主界面饱腹/心态/精力/生命（RA_Open 反汇编实证 HUD 只插 1/2/3/5）；
    // 104 健康=隐藏属性（无 HUD 条但 Buff 真实运作），作者定案不动；NPC 侧 81001+ 独立行，不动。
    // 时机知情：Attr.Max 在 AttributeComponent.OnCreate 首建 AttrDict 时从表快照（之后不回读），
    // 本 mod 在主菜单阶段先行改表、进档创建组件时拿到的已是目标值；运行中关闭=表立即还原，
    // 已在场角色的缓存值要过图重进后回官方 500。防误降：want=max(官方首读值, 目标值)——
    // 官方未来若自抬某行上限，本开关不降它。
    internal const int AttrCapMin = 500;  // CapValue 下限（=官方封顶，不许改小压上限）
    internal const int AttrCapMax = 9999; // CapValue 上限（再大无意义：UI/公式按 int 安全，作者定案）
    private const int AttrCapOfficial = 500; // 首跑核对基线（2026-11-05 表：四行全 500）
    private static readonly Dictionary<int, int> OrigAttrCaps = new();

    /// <summary>四条上限行（101/102/103/105）的 Max 500→target（target&lt;=0=还原官方值）。
    /// 返回 false=表未就绪；表就绪但目标行全缺=游戏无此结构，不阻断其他功能。</summary>
    private static bool ApplyAttrCap(GameCore.HotUpdate.ConfigManager cm, int target,
        bool verbose, bool listAll, List<string> lines)
    {
        Il2CppSystem.Collections.Generic.Dictionary<int, GameCore.HotUpdate.Config_AttributeStandard> dict;
        try { dict = cm._Config_AttributeStandard_Dict; } catch { dict = null; }
        if (dict == null || dict.Count == 0) return false;

        bool unlockCap = target > 0;
        int touched = 0; bool baselineOk = true;
        try
        {
            foreach (var kv in dict)
            {
                int id = kv.Key;
                if (id < 101 || id > 105) continue;
                if (id == 104) continue; // MaxHealth 健康：隐藏属性，作者定案不动
                var row = kv.Value;
                if (row == null) continue;

                int max; string name;
                try { max = row.Max; name = row.Des_Local ?? row.Name ?? ""; } catch { continue; }

                if (!OrigAttrCaps.TryGetValue(id, out var orig))
                {
                    orig = max;
                    OrigAttrCaps[id] = orig;
                }
                if (orig != AttrCapOfficial) baselineOk = false;

                int want = unlockCap ? Math.Max(orig, target) : orig;
                if (max != want)
                {
                    row.Max = want;
                    lines.Add(unlockCap
                        ? $"AttrStd {id} {name} 数值封顶 {max} -> {want}（官方 {orig}）"
                        : $"AttrStd {id} {name} 数值封顶 {max} -> 还原官方 {orig}");
                }
                else if (verbose || listAll)
                    lines.Add($"AttrStd {id} {name} 数值封顶 = {max}（官方 {orig}）");
                touched++;
            }
        }
        catch { }

        if (touched > 0 && (listAll || verbose))
            lines.Add($"AttrStd 上限解锁：{(unlockCap ? $"封顶 {AttrCapOfficial} -> {target}" : "已还原官方封顶")} 覆盖 {touched}/4 条" +
                      (baselineOk ? "" : "（⚠ 有条目官方原值≠500，疑似游戏更新改表，请核对上方清单）"));
        return true;
    }

    /// <summary>单条白名单同步：enabled=清空 Params3，disabled=还原原值。返回 false=表未就绪。
    /// noun=日志用语（藤蔓/花种为"白名单"；每日一次地图为"列表"）。</summary>
    private static bool ApplyAllowList(GameCore.HotUpdate.ConfigManager cm, string key, bool enabled,
        ref string orig, string label, bool verbose, List<string> lines, string noun = "白名单")
    {
        var e = cm.TryGet_Config_GlobalSetting(key);
        if (e == null) return true; // 条目不存在=游戏没有此限制，不算未就绪

        string cur;
        try { cur = e.Params3; } catch { return true; }
        if (orig == null) orig = cur ?? "";

        string want = enabled ? "" : orig;
        if ((cur ?? "") != want)
        {
            e.Params3 = want;
            lines.Add($"GS {key}.Params3 {label}{noun} \"{cur}\" -> \"{want}\"");
        }
        else if (verbose)
            lines.Add($"GS {key}.Params3 {label}{noun} = \"{cur}\"（原 \"{orig}\"）");
        return true;
    }

    private static List<int> Snapshot(Il2CppSystem.Collections.Generic.List<int> list)
    {
        var r = new List<int>();
        if (list == null) return r;
        try { for (int i = 0; i < list.Count; i++) r.Add(list[i]); } catch { }
        return r;
    }

    /// <summary>把 il2cpp 列表内容同步成 target（多余的删、缺的按序补）。返回是否有改动。</summary>
    private static bool SyncList(Il2CppSystem.Collections.Generic.List<int> list, List<int> target)
    {
        if (list == null) return false;
        bool changed = false;
        try
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (!target.Contains(list[i])) { list.RemoveAt(i); changed = true; }
            foreach (var id in target)
                if (!list.Contains(id)) { list.Add(id); changed = true; }
        }
        catch { }
        return changed;
    }
}

internal sealed class RoleTicker : MonoBehaviour
{
    public RoleTicker(IntPtr ptr) : base(ptr) { }
    public RoleTicker() { }

    private float _check;
    private bool _announced;

    public void Update()
    {
        // General.Enabled 总开关（2026-10-24）：false=全功能目标态切"还原官方"（各 Apply 的
        // 现役还原路径：allow-list 回原白名单/DefenseLv 回分列/Talent 回白名单/花瓶回官方等级），
        // 幂等轮询同步防读档重载覆盖。Load 阶段 false 时 Ticker 不会创建，本分支覆盖运行中关闭。
        bool on = UnlockHubPlugin.Enabled != null && UnlockHubPlugin.Enabled.Value;

        _check += Time.deltaTime;
        var due = UnlockHubPlugin.Rerun || _check >= 5f;
        if (!due) return;
        _check = 0f;

        var cm = BagTable.Cm();
        if (cm == null) { UnlockHubPlugin.Rerun = true; return; }

        // 首轮固定输出核对清单（46/77 基线自检），无开关
        var listAll = !_announced;
        // 属性上限目标值：开关全开=Clamp(CapValue,500,9999)，任一关=0（还原官方）；Clamp 兜底
        // AcceptableValueRange（手改 cfg 越界时 BepInEx 也会夹，这里再防程序侧写入）
        int attrCap = on && UnlockHubPlugin.UnlockCap.Value
            ? Math.Clamp(UnlockHubPlugin.CapValue.Value, RoleTable.AttrCapMin, RoleTable.AttrCapMax)
            : 0;
        var lines = RoleTable.Apply(cm,
            on && UnlockHubPlugin.UnlockVine.Value,
            on && UnlockHubPlugin.UnlockFlowerSeeds.Value,
            on && UnlockHubPlugin.ShareDefenseRecipes.Value,
            on && UnlockHubPlugin.ClearPool.Value,
            on && UnlockHubPlugin.VaseRecipe.Value,
            on && UnlockHubPlugin.UnlockDailyOnce.Value,
            attrCap,
            false, listAll);
        if (lines == null) { UnlockHubPlugin.Rerun = true; return; } // 配置表未就绪
        UnlockHubPlugin.Rerun = false;

        foreach (var l in lines) UnlockHubPlugin.Log.LogInfo("[UnlockHub] " + l);

        // 自证 trace（BepInEx 日志强杀不落盘；Config_GlobalSetting 在 gamekb 表 dump 中降级不可读，
        // Params3 断言走本文件）：首轮全量 + 其后仅状态迁移轮（含 "->" 的行），低频，验后可删。
        if (listAll || HasTransition(lines))
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] General.Enabled={on} 本轮输出：");
                foreach (var l in lines) sb.AppendLine("  " + l);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "RU_gate_trace.txt"),
                    sb.ToString());
            }
            catch { }
        }

        if (!_announced)
        {
            _announced = true;
            var vine = on && UnlockHubPlugin.UnlockVine.Value ? "开" : "关";
            var def = on && UnlockHubPlugin.ShareDefenseRecipes.Value ? "开" : "关";
            var pool = on && UnlockHubPlugin.ClearPool.Value ? "开" : "关";
            var vase = on && UnlockHubPlugin.VaseRecipe.Value ? "开" : "关";
            var daily = on && UnlockHubPlugin.UnlockDailyOnce.Value ? "开" : "关";
            var cap = on && UnlockHubPlugin.UnlockCap.Value ? $"开（封顶 {attrCap}）" : "关";
            UnlockHubPlugin.Log.LogInfo(
                $"[UnlockHub] 生效：藤蔓全角色解锁={vine}（防御门槛按官方规则保留），" +
                $"防御配方互通={def}，生存规划互通={pool}，花瓶配方={vase}，探索每日不限次={daily}，" +
                $"属性上限解锁={cap}。配置改动后自动重写/还原。");
        }
    }

    /// <summary>状态迁移行判定（"原值->目标值" 形态；listAll/verbose 的等值行不含 "->"）。</summary>
    private static bool HasTransition(List<string> lines)
    {
        foreach (var l in lines)
            if (l != null && l.Contains("->")) return true;
        return false;
    }
}
