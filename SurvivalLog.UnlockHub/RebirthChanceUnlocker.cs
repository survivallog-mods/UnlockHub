using System;
using System.Reflection;
using HarmonyLib;

namespace SurvivalLog.UnlockHub;

/// <summary>
/// 重生解锁（v1.11.0）：postfix 挂 GameKey.get_GlobalSetting_RebirthMaxChances（public static int，
/// RVA 0x264EC00，2026-11-10 build 26454866 反汇编实证见 DESIGN.md §11）。死亡后的"大重生"机会
/// 上限改为基本不限（返回 999）——全部判定/显示共用这一个 getter，一处补丁全链路生效：
/// 剩余 = max(0, 上限 − 存档已用次数)，死亡页 BattleMain.OpenGameoverUI / CanMajorRebirth /
/// GetRebirthChancesLeft / 重生选择页 Reducer_Web_RebirthChoice.RA_Open+RA_Select / 主菜单存档列表
/// Reducer_Web_MainUI.GetMajorRebirthChancesLeft+RA_RebirthSave 等共 12 处 E8 调用点实证全覆盖。
///
/// 为什么不像藤蔓/探索线改表：getter 内部有进程级静态缓存（首读表后缓存非 0 值，之后永不回读表，
/// getter 反汇编实证），纯表编辑无法保证早于首读、会随机不生效——故走补丁（交易线同款纪律）。
///
/// 存档语义知情：mod 本身不写存档；但开启期间正常重生会让游戏官方代码照常把"已用次数"记进存档
/// （可能超过官方上限），关闭开关后这类档位按官方公式计算=无剩余机会（多重生的是"借"不是"送"）。
///
/// 纪律：全程 try/catch，异常=放弃本轮改写直接返回（最坏退化=官方行为）；开关门控与交易线同款
/// （Enabled + UnlockChances 任一关=原样返回官方值，零状态零还原成本）；小重生（回退 1/5 天）、
/// 信念点价格、血上限惩罚等其他重生规则不动（各自独立的 GameKey 读取器，未被挂载）。
/// </summary>
[HarmonyPatch]
internal static class RebirthChanceUnlocker
{
    // 上限改写值：999（不用 int.MaxValue——UI"已用机会 X/Y"的排版与 LastChance 警告逻辑都对人类数字友好）
    private const int UnlimitedChances = 999;

    // 首次拦截打一条官方原值（装机核对锚点：离线表 dump 中 GlobalSetting 降级不可读，实值在这里读出），之后静默
    private static bool _loggedOnce;

    // 无参数静态 getter，方法名唯一，无重载歧义（interop 程序集 Cecil 实证：static Int32，0 参数）
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(GameCore.HotUpdate.GameKey), "get_GlobalSetting_RebirthMaxChances");

    private static void Postfix(ref int __result)
    {
        try
        {
            // 开关门控：cfg 未初始化(null)或任一开关关 = 原样返回官方值
            if (UnlockHubPlugin.Enabled == null || !UnlockHubPlugin.Enabled.Value) return;
            if (UnlockHubPlugin.UnlockChances == null || !UnlockHubPlugin.UnlockChances.Value) return;

            int official = __result;
            __result = UnlimitedChances;

            if (!_loggedOnce)
            {
                _loggedOnce = true;
                UnlockHubPlugin.Log.LogInfo("[RebirthUnlock] 首次拦截确认：官方重生机会上限 " + official +
                    " -> 本会话 " + UnlimitedChances + "（后续静默；小重生/信念价格等其他重生规则不动）");
            }
        }
        catch (Exception e)
        {
            // 放弃本轮改写：__result 保持官方值，行为退化=官方，无害
            UnlockHubPlugin.Log.LogWarning("[RebirthUnlock] 改写异常（已跳过，官方行为不受影响）: " + e.Message);
        }
    }
}
