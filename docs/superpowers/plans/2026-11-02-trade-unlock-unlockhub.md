# 交易解锁 + 改名 UnlockHub 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给本 mod 增加"交易解锁"功能（每次交易后删货架快照，下次打开交易点自动重新上架），并随之把 mod 从 RoleUnlock（角色专属解锁）改名 UnlockHub（解锁中心）。

**Architecture:** 单一 Harmony postfix 挂游戏私有静态方法 `Reducer_Web_TradeUI.ConsumeActiveShelfStock`，在交易发生瞬间从存档侧 `ActiveShelfStock` 字典删除该货架键；后续重抽 100% 由游戏官方 `DrawActiveShelf` 兜底分支完成。零轮询、零 mod 本地文件。改名是纯机械重命名（仓夹/工程/程序集/GUID/命名空间），放在功能验证之后执行。

**Tech Stack:** BepInEx 6 IL2CPP + HarmonyLib（0Harmony.dll）+ Il2CppInterop；net6.0；游戏 HotUpdate interop 程序集。

**Spec:** `docs/superpowers/specs/2026-11-01-drone-trade-shelf-refresh-design.md`（机制反汇编实证、姿势决策、fallback 设计、验证清单都在 spec，本计划不重复）

## Global Constraints

- 套件军律（EndlessGuard 2026-10-25/26 定案）：Harmony **逐类挂载+异常隔离**（`CreateClassProcessor(t).Patch()` 逐个 try/catch，禁 PatchAll 整包）；**禁 patch async 状态机**——本目标方法是普通私有静态方法，合规
- TargetMethod 用**钉死参数类型数组**（FallRestorer 同款纪律），postfix 只读 `__0`（int），不碰泛型字典参数内容（避开"泛型 List 参数补丁静默失败"已知坑）
- 不改游戏文件、不写任何非官方格式存档数据——本功能对存档的全部改动 = 删除一个官方字典键
- 版本权威 = csproj `<Version>`，插件 `Version` const 必须一致；CHANGELOG 必须有 `v1.8.0` 节（release.ps1 硬性 gate）
- 游戏路径 `E:\SteamLibrary\steamapps\common\Survival Log`；构建用 `E:\Game\Mod\survivallog-mods\sdk\dotnet\dotnet.exe`（`DOTNET_MULTILEVEL_LOOKUP=0`）
- 所有新代码 try/catch + 日志 trace，幂等（半途异常不影响游戏官方流程）
- 测试方式说明（偏离 TDD 的原因）：本功能核心是 interop 补丁，无纯逻辑可单测（套件先例：EndlessGuard.Tests 只测纯数学/存储）。验证 = 构建门 + 实机人工门（Task 2/3），每个门有明确判定标准

## Review Focus

无自动化测试，以下五个最可能翻车的点由实机门覆盖，执行者不得跳过：

1. **补丁静默不生效**（IL2CPP detour 已知坑：挂上但不触发）→ Task 2 spike：首次拦截日志必须出现，且行为验证（重开货架全新）
2. **界面会话内多笔交易显示异常**（DealCore 消费后的界面路径未全枚举）→ Task 3 回归第 4 项；异常即升级两段式（spec §四升级预案）
3. **跨日官方补货被破坏** → Task 3 回归第 3 项（隔天重抽/补货正常）
4. **休闲模式稀有货架被误伤** → Task 3 回归第 6 项（`CasualCampShelf` 独立路径必须原样）
5. **改名后发布链断裂**（cfg 名/归一键表/版本 gate）→ Task 5 构建门 + Task 6 `-PackageOnly` 干跑

---

### Task 1: Harmony 基建 + 交易解锁补丁（spike 版）

**Files:**
- Modify: `SurvivalLog.RoleUnlock/SurvivalLog.RoleUnlock.csproj`（加 0Harmony 引用）
- Create: `SurvivalLog.RoleUnlock/TradeShelfRefresher.cs`
- Modify: `SurvivalLog.RoleUnlock/RoleUnlockPlugin.cs`（Trade 节 cfg + 逐类挂载块 + 欢迎日志）

**Interfaces:**
- Consumes: `RoleUnlockPlugin.Log`（ManualLogSource）、`RoleUnlockPlugin.Enabled`（总开关 ConfigEntry<bool>）
- Produces: `RoleUnlockPlugin.UnlockTrade`（`ConfigEntry<bool>`，节 "Trade"，键 "UnlockTrade"，默认 true）；补丁类 `TradeShelfRefresher`（自包含，无外部依赖）

- [ ] **Step 1: csproj 加 0Harmony 引用（照 EndlessGuard 同款路径）**

在 `SurvivalLog.RoleUnlock.csproj` 的 Reference ItemGroup 里加：

```xml
    <Reference Include="0Harmony">
      <HintPath>E:/SteamLibrary/steamapps/common/Survival Log/BepInEx/core/0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
```

- [ ] **Step 2: 写 `TradeShelfRefresher.cs`**

类结构（签名钉死，注释风格对齐 FallRestorer）：

```csharp
using System;
using System.Reflection;
using HarmonyLib;

namespace SurvivalLog.RoleUnlock;

/// <summary>
/// 交易解锁（姿势1）：postfix 挂 Reducer_Web_TradeUI.ConsumeActiveShelfStock（private static，
/// DealCore 内直接调用，2026-11-01 反汇编实证见 spec §三）。交易发生瞬间删除该货架库存快照
/// → 下次 ResolveActiveShelf 走 DrawActiveShelf 官方兜底分支全新上架。
/// 存档访问链：BaseSingleton<GameSaveManager>.Instance.GetGameSaveData().CurSave.ActiveShelfStock.Remove(id)。
/// 全程 try/catch：任何异常直接放弃本轮删除，游戏官方流程不受影响（幂等）。
/// </summary>
[HarmonyPatch]
internal static class TradeShelfRefresher
{
    private static bool _loggedOnce;

    // 参数类型数组钉死（FallRestorer 同款纪律）：Void(Int32, Dictionary<Int32,Int32>)
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(GameCore.HotUpdate.ReduxUI.Reducer_Web_TradeUI), "ConsumeActiveShelfStock",
            new[] { typeof(int), typeof(Il2CppSystem.Collections.Generic.Dictionary<int, int>) });

    private static void Postfix(int __0)
    {
        // 门控：总开关/功能开关关 = 不删（回官方行为）。__0 = shelfShopId
        // 访问链全 null 守卫；stock.Remove(__0)；_loggedOnce 首次拦截打 LogInfo 一条（spike 验证锚点），之后静默
        // 整体 try/catch(e) → Log.LogWarning 一行，不重抛
    }
}
```

要点：`GameSaveManager` 在**全局命名空间**（`BaseSingleton<GameSaveManager>.IsInstanceCreated` 先查，ArrowTowerFx 同款）；`GameSaveData`/`SaveChildData` 在 `GameCore.HotUpdate`。开关读取放 try 内（cfg 未初始化时 null 直接 return）。

- [ ] **Step 3: 改 `RoleUnlockPlugin.cs`**

- cfg：`UnlockTrade = Config.Bind("Trade", "UnlockTrade", true, "交易解锁：无人机交易每次买完，该交易点的货架下次打开时自动重新上架全新货物（官方旧版行为；官方现行是买掉后要等第二天才补）。false=改回游戏原样。")`
- `UnlockTrade.SettingChanged += reload`（复用现有 Rerun 模式，实际对本补丁无轮询语义，保持一致性）
- Load() 里（`Enabled.Value` 早退之后、Ticker 创建之前）加逐类挂载块——照 EndlessGuardPlugin.cs:71-79 原样缩写：

```csharp
var harmony = new HarmonyLib.Harmony(Guid);
foreach (var t in typeof(RoleUnlockPlugin).Assembly.GetTypes())
{
    if (!t.IsClass || !t.IsDefined(typeof(HarmonyLib.HarmonyPatch), false)) continue;
    try { harmony.CreateClassProcessor(t).Patch(); }
    catch (Exception e) { Log.LogWarning("patch 挂载失败 " + t.Name + ": " + e.Message); }
}
```

- 欢迎日志行追加 `+ 交易解锁`；类头注释补一行功能概述（对齐现有文档习惯）

- [ ] **Step 4: 构建门**

Run: `cd E:\Game\Mod\survivallog-mods\RoleUnlock && DOTNET_MULTILEVEL_LOOKUP=0 E:/Game/Mod/survivallog-mods/sdk/dotnet/dotnet.exe build SurvivalLog.RoleUnlock -c Release --nologo`
Expected: Build succeeded，0 error（warning 允许）

- [ ] **Step 5: Commit**

```bash
git add SurvivalLog.RoleUnlock/SurvivalLog.RoleUnlock.csproj SurvivalLog.RoleUnlock/TradeShelfRefresher.cs SurvivalLog.RoleUnlock/RoleUnlockPlugin.cs
git commit -m "feat(trade): 交易解锁——postfix ConsumeActiveShelfStock 删货架快照，重开自动上架（spike 版含首次拦截日志）"
```

### Task 2: Spike 实机验证（人工门，不可跳过）

**Files:** 无代码改动（部署 + 验证）

**Interfaces:**
- Consumes: Task 1 构建产物 `SurvivalLog.RoleUnlock/bin/Release/SurvivalLog.RoleUnlock.dll`

- [ ] **Step 1: 部署 spike dll**

把 Task 1 的 dll 覆盖到 `E:\SteamLibrary\steamapps\common\Survival Log\BepInEx\plugins\SurvivalLog.RoleUnlock.dll`（旧版先备份到 mod 仓外任意处）。cfg 保持默认（`Trade.UnlockTrade=true`）。

- [ ] **Step 2: 实机验证（需用户配合进游戏）**

启动游戏 → 进任意存档 → 无人机交易买任意一件货 → 查 BepInEx\LogOutput.log。

判定（三选一，按结果走）：
- **PASS**：日志出现首次拦截行（`[RoleUnlock]` + 交易解锁字样），交易结算正常，关闭交易界面重开后该货架是**全新随机货物且数量全满**
- **FAIL-A**：日志出现"patch 挂载失败"或无拦截行且行为未变 → detour 不可行，**停止后续任务**，向用户报告，fallback 条件轮询另立计划（spec §四.2 已有设计）
- **FAIL-B**：拦截发生但游戏异常（报错/界面坏/存档异常）→ **停止**，报告现象，评估两段式升级（spec §四升级预案）

- [ ] **Step 3: 记录结果**

把判定结果（PASS/FAIL-x + 现象一句话）记入本文件对应 checkbox 处的备注或对话记录。PASS 才允许进入 Task 3。

### Task 3: 功能回归（人工门）

**Files:** 无代码改动（预期）；若触发升级预案则按 spec §四改 `TradeShelfRefresher.cs`

- [ ] **Step 1: 回归清单（spec §六 3-9 全项，进游戏逐项过）**

1. 连买多笔 → 关开交易界面 → 全新满货架 ✓
2. 只开关界面不交易 → 货架**不变**（无删除发生）✓
3. 跨日（改系统时间或控制台过夜）→ 官方每日补货/重抽正常 ✓
4. 一次界面会话内多笔交易：界面数量显示、已售项无异常（**异常即升级两段式**：postfix 记账 + ResolveActiveShelf prefix 删）✓
5. 存档/读档往返、重进游戏后功能与官方逻辑都正常 ✓
6. 休闲模式：稀有货架仍按官方消耗（稀有限制保留）✓
7. 钉选物品跨重抽表现，结果记入 DESIGN.md 已知限制节 ✓

- [ ] **Step 2: 修复或升级（仅清单有 FAIL 时）**

按 spec §四预案执行；改动后重复相关项。全部 ✓ 后 commit（如有改动）。

### Task 4: 文档与版本 v1.8.0

**Files:**
- Modify: `SurvivalLog.RoleUnlock/DESIGN.md`（追加交易解锁节）
- Modify: `README.md`、`SurvivalLog.RoleUnlock/README.md`、`ReleaseREADME.md`（改名后口径统一重写，本任务先按最终名"解锁中心 UnlockHub"写内容，Task 5 做机械改名）
- Modify: `SurvivalLog.RoleUnlock/CHANGELOG.md`（新增 v1.8.0 节）
- Modify: `SurvivalLog.RoleUnlock/SurvivalLog.RoleUnlock.csproj`（`<Version>` 1.8.0）、`RoleUnlockPlugin.cs`（Version const 同步 1.8.0）

**Interfaces:**
- Consumes: Task 1-3 的最终行为
- Produces: v1.8.0 版本三处一致（csproj / plugin const / CHANGELOG 节名）

- [ ] **Step 1: DESIGN.md 追加"交易解锁"节**

含：机制摘要（调 spec）、访问链、开关、验证结论（Task 2/3 结果一句话）、已知限制（换新语义/休闲稀有不覆盖/钉选实测结果）。

- [ ] **Step 2: 三份 README 重写为最终口径**

标题「解锁中心 UnlockHub（Survival Log MOD）」；功能清单 = 现有 5 项角色线 + 交易解锁；配置表加 `Trade.UnlockTrade` 行（人话描述同 Task 1 cfg 文案）；定位一句话改为"解除游戏各类官方限制的通用合集"。英文节同步。

- [ ] **Step 3: CHANGELOG v1.8.0 + 版本号三处同步**

CHANGELOG 新节：`## v1.8.0`（新增交易解锁；mod 更名 UnlockHub/解锁中心；cfg 文件名变更说明——老配置回默认无害）。

- [ ] **Step 4: 构建门 + commit**

Run: 同 Task 1 Step 4。Expected: Build succeeded。
```bash
git add -A && git commit -m "docs+chore: v1.8.0 交易解锁文档 + 改名 UnlockHub 口径统一"
```

### Task 5: 机械改名 UnlockHub

**Files:**
- Rename: `SurvivalLog.RoleUnlock/` → `SurvivalLog.UnlockHub/`（含 csproj 改名 `SurvivalLog.UnlockHub.csproj`、`RoleUnlockPlugin.cs` → `UnlockHubPlugin.cs`）
- Modify: `release.ps1`（cfg 归一键表）
- Rename: 仓夹 `E:\Game\Mod\survivallog-mods\RoleUnlock` → `UnlockHub`（最后一步，git 外操作）

**Interfaces:**
- Consumes: Task 4 完成的最终口径文档
- Produces: `SurvivalLog.UnlockHub.dll`、GUID `com.local.survivallog.unlockhub`、命名空间 `SurvivalLog.UnlockHub`、cfg `com.local.survivallog.unlockhub.cfg`

- [ ] **Step 1: 仓内重命名**

`git mv SurvivalLog.RoleUnlock SurvivalLog.UnlockHub`；`git mv SurvivalLog.UnlockHub/SurvivalLog.RoleUnlock.csproj SurvivalLog.UnlockHub/SurvivalLog.UnlockHub.csproj`；`git mv SurvivalLog.UnlockHub/RoleUnlockPlugin.cs SurvivalLog.UnlockHub/UnlockHubPlugin.cs`。csproj 的 `AssemblyName`/`RootNamespace` 改 `SurvivalLog.UnlockHub`；全仓文本替换命名空间 `SurvivalLog.RoleUnlock` → `SurvivalLog.UnlockHub`；插件 `Guid = "com.local.survivallog.unlockhub"`、`Name = "UnlockHub"`、GameObject 名 `RoleUnlock.Ticker` → `UnlockHub.Ticker`、日志前缀同步。

- [ ] **Step 2: release.ps1 键表**

cfg 归一注释 RoleUnlock 行改为 UnlockHub 并追加 `'^[[:space:]]*(UnlockTrade\s*=\s).*$', '${1}true'` 一行（PowerShell `-replace` 语法照现有行）；cfgName 由仓夹名自动推导，无需改。

- [ ] **Step 3: 构建门（改名后）**

Run: `DOTNET_MULTILEVEL_LOOKUP=0 E:/Game/Mod/survivallog-mods/sdk/dotnet/dotnet.exe build E:/Game/Mod/survivallog-mods/RoleUnlock/SurvivalLog.UnlockHub -c Release`
Expected: 产物 `SurvivalLog.UnlockHub/bin/Release/SurvivalLog.UnlockHub.dll` 生成，0 error。

- [ ] **Step 4: 仓夹改名（git 外）+ commit**

```bash
cd E:/Game/Mod/survivallog-mods && mv RoleUnlock UnlockHub
git -C UnlockHub add -A && git -C UnlockHub commit -m "refactor: RoleUnlock 更名 UnlockHub（解锁中心）——程序集/GUID/命名空间/cfg 名"
```

注意：仓夹改名后本会话工作区路径失效，需以新路径 `E:\Game\Mod\survivallog-mods\UnlockHub` 重开工作区再继续 Task 6。

### Task 6: 发版干跑 + 套件侧清单

**Files:** 无仓内代码改动

- [ ] **Step 1: `-PackageOnly` 干跑（改名后新路径下）**

Run: `cd E:/Game/Mod/survivallog-mods/UnlockHub && powershell -File release.ps1 -PackageOnly`
Expected: `release/` 下产出 `SurvivalLog.UnlockHub_v1.8.0.zip`，包内 cfg 名为 `com.local.survivallog.unlockhub.cfg` 且 `UnlockTrade=true`，update.json 的 `mod=UnlockHub`。任何 throw = 修复后重跑。

- [ ] **Step 2: 正式发版（作者确认步骤，套件惯例不自动）**

`release.ps1 -Yes` 或作者自走；Nexus 手动步骤照旧。

- [ ] **Step 3: 套件侧清单（用户执行，记入 迁移对账清单.md）**

- GitHub 仓改名：`gh repo rename UnlockHub --repo survivallog-mods/RoleUnlock`（旧 release URL 自动重定向）
- ModCenter 注册表更新（mod 名/GUID/cfg 名）
- 套件 version.json 同步 v1.8.0
- 玩家公告一句：更名 + 新 dll 需手删旧 `SurvivalLog.RoleUnlock.dll`

---

## 执行顺序与门禁总结

Task 1 →（构建门）→ Task 2 →（实机 spike 门：FAIL 即停）→ Task 3 →（回归门）→ Task 4 →（版本门）→ Task 5 →（改名后构建门 + 重开工作区）→ Task 6 →（干跑门）→ 发版。
