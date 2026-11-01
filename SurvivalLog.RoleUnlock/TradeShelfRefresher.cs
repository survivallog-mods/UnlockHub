using System;
using System.Reflection;
using HarmonyLib;

namespace SurvivalLog.RoleUnlock;

/// <summary>
/// 交易解锁（姿势1）：postfix 挂 Reducer_Web_TradeUI.ConsumeActiveShelfStock（private static，
/// DealCore 内直接 E8 调用，2026-11-01 反汇编实证见 docs/superpowers/specs
/// /2026-11-01-drone-trade-shelf-refresh-design.md §三）。交易发生瞬间删除该货架库存快照
/// → 下次 ResolveActiveShelf 发现快照缺失，走 DrawActiveShelf 官方兜底分支全新随机上架满货架
/// （即官方旧版"每次交易完就补货"的现行等价路径）。
///
/// 存档访问链（全 interop，ArrowTowerFx 同款单例模式）：
///   BaseSingleton<GameSaveManager>.Instance.GetGameSaveData().CurSave.ActiveShelfStock.Remove(shelfShopId)
/// GameSaveManager/GameSaveData/SaveChildData 均在 GameCore.HotUpdate（dump 86317 行实证）。
///
/// 纪律：全程 try/catch——任何异常放弃本轮删除直接返回，游戏官方扣减流程照常（幂等，
/// 最坏退化=官方行为）；TargetMethod 参数类型数组钉死（FallRestorer 同款），
/// postfix 只读 __0（int shelfShopId），不碰泛型字典参数内容（避开 il2cpp 泛型补丁静默失败坑）。
/// ActiveShelfRestockDay 不动：DrawActiveShelf 重抽分支自会写成今天。
/// </summary>
[HarmonyPatch]
internal static class TradeShelfRefresher
{
    // 首次拦截打一条（spike 验证锚点），之后静默
    private static bool _loggedOnce;

    // 参数类型数组钉死：Void(Int32, Dictionary<Int32,Int32>)
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(GameCore.HotUpdate.ReduxUI.Reducer_Web_TradeUI), "ConsumeActiveShelfStock",
            new[] { typeof(int), typeof(Il2CppSystem.Collections.Generic.Dictionary<int, int>) });

    private static void Postfix(int __0)
    {
        try
        {
            // 开关门控：cfg 未初始化(null)或任一开关关 = 不删，回官方行为
            if (RoleUnlockPlugin.Enabled == null || !RoleUnlockPlugin.Enabled.Value) return;
            if (RoleUnlockPlugin.UnlockTrade == null || !RoleUnlockPlugin.UnlockTrade.Value) return;

            if (!GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.GameSaveManager>.IsInstanceCreated) return;
            var save = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.GameSaveManager>.Instance?.GetGameSaveData();
            var stock = save?.CurSave?.ActiveShelfStock;
            if (stock == null) return;

            stock.Remove(__0);

            if (!_loggedOnce)
            {
                _loggedOnce = true;
                RoleUnlockPlugin.Log.LogInfo("[TradeUnlock] 首次拦截确认：货架 " + __0 + " 快照已删，下次打开交易点将全新上架（后续静默）");
            }
        }
        catch (Exception e)
        {
            // 放弃本轮删除：官方扣减已由原函数完成，行为退化=官方，无害
            RoleUnlockPlugin.Log.LogWarning("[TradeUnlock] 删快照异常（已跳过，官方行为不受影响）: " + e.Message);
        }
    }
}
