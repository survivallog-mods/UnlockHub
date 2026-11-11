# SurvivalLog.RoleUnlock — 角色专属解锁（设计文档）

## 1. 功能

- **藤蔓全角色解锁**：任何角色（不只女大学生）收获作物时都可能"发现稀有种子"拿到藤蔓种子，
  并可种植藤蔓、安装到家具、获得藤蔓护盾防御效果。
- **防御配方全角色互通**：`Config_DefenseLv.unlock_recipes_P1~P4` 四列合并成并集——
  燃烧瓶等角色专属防御配方对全部角色变为**可解锁**（各自的防御等级门槛照旧，不是白送）。
- **生存规划互通**（2026-10-24）：46 条功能类专属规划全角色可刷可买（连带大学生 6 条烹饪配方、
  燃烧瓶强化、老主顾互通）；家具位类规划黑名单排除；**已购不清退**——还原只影响后续抽取。
- **花瓶配方互通**（2026-10-24）：花瓶包裹配方 325 等级化解锁，制作等级 1 全角色可学；
  P2 已获者不受影响。
- **属性上限解锁**（2026-11-10；v1.12.1 补齐第二道锁）：主界面四条属性（饱腹/心态/精力/生命）
  的"上限值"封顶 500→可配 500~9999 默认 999，上限增益 buff 的 200 层叠层同步放开（听音乐/浇水/
  看书等日常动作每层+2，官方叠满即零效果）；只解除封顶不白送数值；隐藏属性健康/NPC 属性不动；见 §12。
- 纯内存表编辑、零 Harmony、幂等。**全部功能有 cfg 开关（关=还原官方值）**：藤蔓/花种/防御配方
  各自独立开关；生存规划互通（Plan.ClearPool）/花瓶配方（Plan.VaseRecipe，2026-10-24 起有开关）；
  General.Enabled 总开关（ModCenter 启停位）false=全功能还原官方并停写。
  已购规划不清退（还原只影响后续抽取）。

## 2. 机制实证（2026-10-21 反汇编，dump=tools/il2cppdumper/ 根目录当日产物）

### 2.1 角色体系

- 当前角色 = `ConfigManager.GetCurrentPlayerSelectId()`（静态，返回 1~4 = P1~P4）。
- 通用判定 `ConfigManager.IsRoleAllowed(List<int>, int)`：空列表=不限，非空=白名单。
- `Config_ProductionList` / `Config_Item` / `Config_Furniture` 均**无**角色字段；
  角色限定分散在 Config_Talent/Config_Event 等的 `PlayerSelectId` 字段、DefenseLv 分列、
  GlobalSetting 白名单、以及 IvyPlanManager 的代码级比对。

### 2.2 藤蔓线（女大学生专属的原生实现）

- `Config_GlobalSetting["Plant_RareSeedDiscover"]` 四合一条目：
  `Params1(int 0x18)`=藤蔓种子物品ID（≤0 时随机挑已解锁种子）、`Params2(float 0x1C)`=发现概率、
  **`Params3(string 0x20)`=角色白名单**（';' 分隔，空=不限）。
- 三处消费全部只读 Params3 做"空=放行"判定（空值路径均已反汇编验证落点）：
  - `IvyPlanManager.IsIvyRoleAllowed()`（RVA 0x2702F80；空→return true @0x27030B6）——
    供 TowerCombatManager 咬人/波次结算、TowerWaveManager.EvaluateReadiness（护盾战斗）用；
  - `IvyPlanManager.IsInstallTarget(Furniture)`（0x2702CA0；空→跳过角色比对 @0x2702E30）；
  - `AE_HarvestPlant.TryDiscoverRareSeed`（经 RollYield 0x30943C0 调用）：收获时
    ①Params2>0 ②防御门槛 `Plant_RareSeedDiscover_DefenseLv`.Params1≤当前防御等级
    ③Params3 白名单含当前角色 → 概率=Params2×(1+天赋 Buff/AE_RareSeedDiscoverBonus)（封顶 1.0）
    掷中 → 收获物 +1 颗 Params1 种子（空→跳过角色门 @0x30963AC）。
  - 辅助：`AE_HarvestPlant.IsRoleInAllowList(value, role)`（0x3093720）=同款语义的列表版；
    `GetRoleExclusiveSeedPlantIds(role)`（0x3092870）用同一 Params3 决定哪些植物计入角色专属种子。
- **红线：白名单只能清空，不能改成 "1;2" 多值**——IsIvyRoleAllowed 的内联解析器逐字符
  解析数字、遇非数字即停（不识别 ';'），多值会静默变成"只放行解析出的首个数字"。
- 种植/安装家具本身（`Config_Furniture.PlantFurnitureID(0xF0)>0`）无角色字段，全角色可持有。

### 2.3 防御配方线（打工仔专属燃烧瓶的原生实现）

- 配方可做性总闸 `ToolTableManager.GetProductionCraftLockReason`（0x28098E0）：
  !IsUseable→隐藏 / 染色配方→走染色线 / 防御装置→防御等级线 / 解锁列表含→解锁 /
  `GetDefenseRequiredLevelForRecipe(id)`>0→防御等级线 / craft_level>0→等级线 / 否则 NotLearned。
- `ConfigManager.GetDefenseUnlockRecipes(cfg)`（0x2F37880）按当前角色 switch 取
  DefenseLv 行的 `unlock_recipes_P1(0x80)/P2(0x88)/P3(0x90)/P4(0x98)`；
  `GetDefenseRequiredLevelForRecipe`（0x2F37610）内部也走它——错角色配方查到的需求等级=0
  →落入 NotLearned → 工作台不可见。展示侧（ProficiencyManager.BuildRecipeUnlockList）
  与执行侧（ToolTableManager.TryCreateItem）都走这条链，数据层打通即双侧生效。
- "防御配方"界定 `IsTowerDeviceProduction`（0x2F49D30）：首产品 Config_Item.TargetFurnitureID
  →Config_Furniture.SlotType==10。燃烧瓶在哪个角色列/哪个等级行属表数据（装机后从
  ListOnReady 日志核对，见 §5）。

### 2.4 生存规划线（2026-10-24 反汇编+表统计，表=gamekb/2026-10-23_26157732）

- 生存规划 = Type2 天赋池：`SurvivalPlanningComponent` 按 talentId 从 `Config_Talent` 取规划，
  RogueSelect 信念点三选一抽取；UI 专属印章 = `PlayerSelectId` 非空（空列表不盖印）。
- Type2 专属共 123 条 = 家具位 77 + 功能 46（2026-10-23 表统计）。
- 空列表 = `IsRoleAllowed` 全放行（§2.1 通用判定）→ 清空 `PlayerSelectId` 即全角色可刷可买；
  家具位类规划绑定各角色自家地图，跨角色必排除（按名称/描述关键词黑名单，见 §8）。

### 2.6 防御配方列勘误（2026-10-24 汇编+表定案）

- 本版（build 26157732）防御配方数据已挪回**通用列** `unlock_recipes`(0x78)：Lv1[102 维修零件] Lv2[404 劣质/405 普通/406 烈性燃烧瓶] Lv3[402 电网] Lv4[403 机械电锯] Lv5[407 钛合金大门/408 防弹窗]；`unlock_recipes_P1~P4` 四列**全空**——10-23 游戏更新官方已自行统一全角色防御配方。
- `GetDefenseRequiredLevelForRecipe`(0x2F655D0) 先查通用列再查角色列（两者任一 Contains 即计入），`GetDefenseUnlockRecipes`(0x2F65840) 仍按角色取 P 列——机制未变、数据挪了。
- 推论：本 mod 的 ShareDefenseRecipes（P1~P4 并集）在本版**空转无害**（空并集），日志表现为 "DefenseLv N 并集 []" 属正常；若未来版本恢复分列则自动重新生效。§5 验证清单中"燃烧瓶出现在并集"一项在本版不适用（原生即全角色）。

### 2.5 花瓶配方 325（2026-10-24 表实证）

- 原生 `Config_ProductionList[325].craft_level=0`：不走等级解锁，纯 P2 剧情事件发放——
  与 434/435 并列三条"真独占"配方（零解锁途径）；作者定案只做 325（434/435 不动）。
- 锁判定链（§2.3 GetProductionCraftLockReason）里"解锁列表含→解锁"优先于等级线，故 P2
  已获者不受改表影响；改为 craft_level/show_level=1/1 后全角色制作等级 1 可学（对齐工具柜包裹 389 档位）。

## 3. 实现选型

- **为什么纯表编辑而不是 Harmony**：GetDefenseUnlockRecipes 返回 `List<int>`（il2cpp 泛型），
  Harmony 补丁参数含 il2cpp 泛型类型会 IL Compile Error 静默失败（既有实证）；且藤蔓侧
  清一个字符串即全链路生效，无补丁必要。
- **防御配方的表编辑等价性**：把每行 P1~P4 都写成四列并集 → GetDefenseUnlockRecipes 取哪列
  都是全量；GetDefenseRequiredLevelForRecipe 逐行扫"本角色列含该配方"→返回首个命中的行
  （即该配方在全体角色列中的最低防御等级）→解锁节奏与原生同构。
- 藤蔓侧：Params3→""；防御门槛默认不动（cfg 可清零）；花种白名单默认不动（cfg 可开）。
- 轮询幂等（同套件模式）：5 秒一轮，目标态由配置+进程内首读原值推导；读档/切场景表重载
  后下一轮自动补写；关配置自动还原官方值。快照不落盘、不写存档。

## 4. 配置

| 项 | 默认 | 说明（人话版，与插件 cfg 描述一致） |
|---|---|---|
| General.Enabled | true | 总开关：false=全部改回游戏原样；下次启动不加载 |
| UnlockVine | true | 藤蔓全角色：任何角色收庄稼可能捡到爬山虎种子、能种藤蔓（原生仅大学生）；防御门槛保留官方 |
| UnlockFlowerSeeds | true | 花种子全角色：任何角色收庄稼可能捡到花朵种子（原生仅大学生）；2026-10-24 默认改 true |
| ShareDefenseRecipes | true | 防御配方互通（本版官方已统一，空转留作保险） |
| Plan.ClearPool | true | 生存规划全角色：46 项功能类专属规划谁都能刷能买（已购不退；房屋扩建类不通融）；false=还原 |
| Plan.VaseRecipe | true | 花瓶全角色：制作等级 1 谁都能学做花瓶（原生仅大学生剧情送）；false=还原 |
| Trade.UnlockTrade | true | 交易解锁：无人机货架买完即刷（官方旧版行为）；false=还原（见 §9） |
| Explore.UnlockDailyOnce | true | 探索解锁：每日一次探索点（科技公司）不限次；false=还原（见 §10） |
| Rebirth.UnlockChances | true | 重生解锁：大重生机会基本不限次（上限读取改 999）；false=还原（见 §11） |
| Attribute.UnlockCap | true | 属性上限解锁：主界面四条属性（饱腹/心态/精力/生命）上限封顶 500→CapValue，上限增益 buff 叠层 200 同步放开；false=还原（见 §12） |
| Attribute.CapValue | 999 | 属性上限解锁的目标封顶值（500~9999），仅 UnlockCap 开启时生效 |

注：SkipDefenseGate（清零藤蔓种子防御门槛）已于 2026-10-24 按作者决定移除——门槛保留官方规则；
UnlockFlowerSeeds 默认值同日改 true（作者定案：其他角色默认可发现花朵种子）。
注：生存规划互通 / 花瓶配方互通原为无开关设计，2026-10-24（ModCenter T11）起各有独立开关
（Plan.ClearPool / Plan.VaseRecipe，默认 true=行为不变）；总停用走 General.Enabled。

cfg 文件名 `com.local.survivallog.roleunlock.cfg`（Guid `com.local.survivallog.roleunlock`）。

## 5. 待装机验证清单（作者实测）

1. 日志 `GS Plant_RareSeedDiscover.Params3 藤蔓白名单 "X" -> ""`——X 即原白名单
   （女大学生的角色编号实值，顺带定 P1~P4 与四个角色的对照）。
2. 日志 `DefenseLv N P1~P4 -> 并集 [...]` 各行——确认燃烧瓶配方 ID 出现在并集、
   以及它原生只在打工仔那列（首跑日志带原始四列，一眼可核）。
3. 女大学生档：工作台能看到/解锁燃烧瓶（防御等级够的行）；收获作物能掉藤蔓种子（原生本来就能，回归验证）。
4. 打工仔档：收获作物能发现藤蔓种子；种下后家具菜单出现藤蔓相关入口（Ivy_BtnName），
   安装后守尸潮护盾生效（弹性降难日志里"燃烧瓶藤蔓"项>0）。
5. 关闭 ShareDefenseRecipes / UnlockVine 后重进游戏，工作台与收获行为回到原生。
6. 首跑日志两行：`Talent 互通：功能类已清空 46 条 / 家具位黑名单保留 77 条（基线 46/77 吻合）`
   与 `Recipe 325 花瓶包裹 craft_level X->1 show_level Y->1（原值已快照）`——
   前者不符时带 ⚠（疑似游戏更新改表，请核对上方清单）。
7. 打工仔档：中期规划池（RogueSelect 信念点三选一）可刷出并购买大学生功能天赋（烹饪配方等）。
8. 制作等级 1 的工作台出现花瓶包裹（可学可做）；大学生（P2）档已获花瓶配方不受影响。
9. 家具位类规划全程未出现在其他角色规划池。

## 6. 防盗署名气泡（套件统一）

- `Load()` 末尾调用 `SurvivalLog.ModShared.WelcomeTip.Install(Log)`（共享源 `ModShared/WelcomeTip.cs`，csproj 已链入）。
- 行为：进档后主角头顶显示欢迎文字（约 3 秒）——主通道走游戏官方说话队列 `CharacterTalkManager.EnqueueTalk`（与原生气泡消息排队轮流显示、互不顶替；进档瞬间有原生推送也不会被覆盖）；管理器不可用时退 `AgentModel.Unit_ShowHeadTalk(text, 3)` 直显。实测主角头顶条为官方无框文字样式（silentHeadTalk 默认 false，各通道均无框，非缺陷）；During≈真实秒（传 360 会远超 3 秒）。每次进档一次，回主菜单再进再弹。
- 套件全部 mod 调用，靠 `GameObject.Find("SurvivalLog.WelcomeTip")` 去重：先到者创建 DontDestroyOnLoad ticker，其余跳过——装多个只弹一条。无 cfg 开关（防盗署名为内置行为，防剥离）。
- 文案单源 `WelcomeTip.Text`（作者 Night · 抖音号 166660371）；改文案后全部 mod 需重编。

## 7. 文件

- `RoleUnlockPlugin.cs`：插件主体（cfg + 轮询 Ticker + RoleTable 表改写），单文件。
- 依赖：ModShared（BagTable.Cm() ConfigManager 取实例 + WelcomeTip），随 csproj 源码并入编译。

## 8. 迭代维护清单

- **2026-10-24 新增：生存规划互通 + 花瓶配方 325 等级化解锁**（`RoleTable.ApplyTalents` / `ApplyVaseRecipe`，
  均无 cfg 开关）。
  - 依赖登记（`tools/gamekb/moddeps.json` RoleUnlock.configFields 新增两组）：`_Config_Talent_Dict`
    （`PlayerSelectId` / `Name_Local` / `Dec_Local`）与 `_Config_ProductionList_Dict`
    （`craft_level` / `show_level`）——游戏更新后跑 modcompat_check 重点看这两组是否仍命中。
  - **"已购不清退"语义**：清空 PlayerSelectId 只影响规划池的后续抽取；已购/已学规划为存档侧数据，
    不回读表——关 mod / 删 dll 后已购项保留，仅新抽取回官方行为。
  - 基线 46/77 来源：2026-10-23 表（`gamekb/2026-10-23_26157732`）统计——功能类专属 46 条、
    家具位黑名单 77 条。不符时插件日志带 ⚠ **只报警不阻断**（防游戏更新改表误判），需人工核对
    ListOnReady 清单后同步更新 `RoleUnlockPlugin.cs` 的 `ExpectShareTalents` / `ExpectKeepSlotTalents`。
  - 家具位黑名单关键词（`SlotTalentKeywords`，匹配 Name_Local / Dec_Local，命中即跳过不清）：
    `位·`、`扩建`、`腾出一处`、`置物位`——宽匹配设计对游戏更新新增的家具位规划自动免疫。
  - 花瓶 325 原值快照于进程内首读（`_origVaseCraft`/`_origVaseShow`），同表重载后下一轮自动补写；不改 434/435。
- **2026-10-24（ModCenter T11）：General.Enabled 总开关 + Plan.ClearPool / Plan.VaseRecipe 两开关**。
  - **General.Enabled**：Load 早退（全部 Bind 后、Ticker 创建前——停用=零 GameObject 零日志零改写）
    + RoleTicker 运行中关闭复用现役还原路径（`on && X.Value` 传参：false 时各 Apply 按首读快照
    回写官方值，allow-list `want = enabled ? "" : orig` 同款模式，幂等轮询防表重载）。
  - **Plan.ClearPool=false 还原语义**：ApplyTalents 目标态由"清空"切"还原快照原白名单"——
    空白名单行若在本会话快照表（OrigTalentRoles）内则走还原补回（`cur.Count==0 && !known` 才跳过，
    区分"本就全角色"与"自己清空的"）；家具位黑名单行永不入快照表，46/77 基线口径不变。
    已购规划为存档侧数据不受表还原影响（只影响后续抽取）。
  - **Plan.VaseRecipe=false 还原语义**：ApplyVaseRecipe 目标态切回 `_origVaseCraft/_origVaseShow`
    首读快照（本版 0/0：craft_level=0 纯 P2 剧情发放）。
  - 两开关默认 true（升级用户行为不变）；cfg 键首跑自动生成，SettingChanged 触发 Rerun 热重写。
- **2026-10-24（作者定案）：cfg 说明改人话版；删 Debug.ListOnReady/Verbose 两调试开关**（首轮核对清单改为固定输出，46/77 基线自检不丢）；UnlockFlowerSeeds 默认 true；SkipDefenseGate 移除（藤蔓种子防御门槛永远保留官方规则）。
- **2026-11-10 新增：属性上限解锁**（`RoleTable.ApplyAttrCap`，§12）：依赖登记
  （`tools/gamekb/moddeps.json` configFields 新增 `_Config_AttributeStandard_Dict`：
  `Max`/`Des_Local`/`Name`）——游戏更新后跑 modcompat_check 重点看是否仍命中。
- **2026-11-11 v1.12.1：属性上限解锁补第二道锁**（buff 叠层 200，§12）：依赖登记追加
  `_Config_Buff_Dict`：`MaxLayerCount`/`Name_Local`/`Name`。
- 版本与发版（2026-10-28 起，v2 迁移后）：版本号单源=本仓 csproj <Version>（作者定义）；单 mod 发版=本仓 .\release.ps1；ModShared 改动源=中枢仓 suite\ModShared（push_modshared 推送）。

## 9. 交易解锁（2026-11-02，v1.8.0；本 mod 首个非角色线功能 + 首个 Harmony 补丁线）

设计全文与反汇编取证：`docs/superpowers/specs/2026-11-01-drone-trade-shelf-refresh-design.md`（含实施计划）。

- **行为**：无人机交易每次买完，该交易点货架下次打开自动重新上架全新货物（官方旧版"买完即补"
  的现行等价路径；官方现行是每日按营地铁随机补 3~5 种）。知情语义：重开=换新随机货架，非同款回满。
- **机制**（v26157732 dump 实证）：购买走 `Reducer_Web_TradeUI.DealCore → ConsumeActiveShelfStock`
  （private static，DealCore 内直接 E8 调用）；补货/重抽只在 `ResolveActiveShelf`（开界面/查填充度），
  快照缺失 → `DrawActiveShelf` 官方兜底分支全量重抽。本 mod postfix 挂 Consume，交易瞬间删
  `SaveChildData.ActiveShelfStock[货架id]`（访问链 `BaseSingleton<GameSaveManager>.Instance
  .GetGameSaveData().CurSave.ActiveShelfStock.Remove(id)`，均在 GameCore.HotUpdate 命名空间）。
- **纪律**：postfix 只读 `__0`(int) 避开泛型补丁坑；逐类挂载+异常隔离（EndlessGuard 2026-10-25 军律）；
  全程 try/catch，异常即放弃本轮删除（最坏退化=官方行为）；`ActiveShelfRestockDay` 不动（重抽分支自写今天）。
- **spike 实证（2026-11-02）**：LogOutput.log 1503 行 `[TradeUnlock] 首次拦截确认：货架 920 快照已删`，
  无挂载失败，交易结算正常——detour 触发链验证通过。
- **不覆盖**：休闲模式稀有货架（CasualCampShelf 独立消耗路径，稀有限制保留）。
- **更名**：v1.8.0 起 RoleUnlock → **UnlockHub（解锁中心）**，GUID `com.local.survivallog.unlockhub`，
  cfg `com.local.survivallog.unlockhub.cfg`；ModCenter 注册表（Meta.cs / RepoDecision.RepoKeys）连动更新。

## 10. 探索解锁——每日一次探索点不限次（2026-11-08，v1.9.0）

- **行为**：地图上每天只能去一次的探索点（当前唯一成员=科技公司，Config_MapPoint 表 ID 1003）
  改为每天不限次前往。当次探索的其余规则（消耗、暴露值等）照旧不动。
- **机制**（build 26454866 反汇编实证，2026-11-08）：
  - 限制源=全局配置表条目 `Explore_DailyOnceMaps`：`Params3`（string 0x20）为 ';'
    分隔的受限地图ID列表；条目缺失时游戏回落内置默认常量 "1003"（`ExploreManager.DailyOnceDefaultMaps`）。
  - 判定总闸 `ExploreManager.IsDailyOnceMap(mapConfigId)`（public static，RVA 0x2723B50）：
    mapConfigId≤0→false；TryGet 条目成功取 Params3 / 失败取默认串；
    **IsNullOrEmpty → return false（@0x2723c30–0x2723c37，"空=无任何地图受限"）**；
    否则按 ';' 分割逐段 int.TryParse 与 mapConfigId 比对，命中→true。
  - 三条消费路径全部以 IsDailyOnceMap 为第一道判定，清空后全部短路放行：
    ① 进入闸门 `OnCheckAndGo`→`IsDailyOnceUsedToday`（0x2723CD0，首 call IsDailyOnceMap @0x2723cfa；
    false→放行，不走"今日已去过"拦截分支）；
    ② 进入后标记 `MarkDailyOnceEntered`（0x2723DA0，首 call @0x2723dca；false→跳过，
    "已去过"计数器 `explore.dailyonce.<mapId>` 不写入——零存档副作用）；
    ③ 跨日重置 `PostDailyOnceForMap`（0x27257E0，内部两处 call @0x2725970/0x27259ae）。
  - 旁路排查：`GameKey.GlobalSetting_Explore_DailyOnceMaps`（静态缓存读取器 0x263A520）
    全镜像无 E8 调用方（仅方法指针表登记）——不存在绕开表编辑的第二条判定路径。
  - UI 侧"每日一次"角标数据 `Ac_ExploreMap_Open.DailyOnceByMapPoint` 经 `TradeMapStatusRelay`
    中转（生产方 PostExploreDailyOnce 无直接调用方，未全追；仅显示层，不影响进入判定）。
- **实现**：复用藤蔓线 `ApplyAllowList`（清空=解除限制；false=按进程内首读快照还原原列表，
  参数 noun="列表"）；cfg `Explore.UnlockDailyOnce`（默认 true）。
- **验证清单（装机）**：首跑日志 `GS Explore_DailyOnceMaps.Params3 每日一次地图列表 "1003" -> ""`；
  同日两次前往科技公司均正常进入；关开关后自动还原次日恢复每日一次；mod 开启期间存档无
  explore.dailyonce.1003 计数残留。

## 11. 重生解锁——大重生机会基本不限次（2026-11-10，v1.11.0）

- **行为**：死亡后的"大重生"机会上限改为 999（基本不限次）。官方每档只有少数几次（表实值离线
  不可读——gamekb 表 dump 中 GlobalSetting 降级 dict-cast-failed、448 个 MainPackage bundle 无明文，
  装机首跑拦截日志读出），用完即真结局（只能结算/重开）。小重生（回退 1/5 天，信念点计价
  RebirthPrice1Day/5Day + 开关 RebirthEnableDayRollback/5DayRollback）、血上限惩罚
  （RebirthHpMaxPenalty）、快照槽位等其他重生规则全部不动（各自独立 GameKey 读取器，未挂载）。
- **机制**（build 26454866 反汇编实证，2026-11-10）：
  - 限制源=全局配置表条目 `RebirthMaxChances`：Params1（int 0x18）为机会上限；读取器
    `GameKey.get_GlobalSetting_RebirthMaxChances`（public static，RVA 0x264EC00）。
  - 判定公式（全部消费点同构）：剩余 = max(0, 上限 − GameSaveData.RebirthChancesUsed(0xF0))；
    `CanMajorRebirth`（0x294E590）= CurSave!=null && 剩余>0（失败原因 "no-save"/"no-chance-left"）。
  - 12 处 E8 调用点全部经由该 getter（il2cpp 节 xref 全量）：`BattleMain.OpenGameoverUI`
    （死亡页 0x2F5C1DF）/`TryResumePendingDeathChoice`、`Reducer_Web_RebirthChoice.RA_Open/RA_Select`
    （选择页数量+确认）、`Reducer_Web_MainUI.GetMajorRebirthChancesLeft/HasMajorRebirthChance/
    RA_RebirthSave`（主菜单存档列表；无机会→log"已无大重生机会，忽略重生请求"拒绝进入）、
    `EnterSaveByFileName`、`Reducer_Web_EndingChoice.RA_Open/RA_Close`、
    `TryBeginMinorRebirth/TryBeginRoundRestart`、`WebGm.ProcessMessage`（"[GM] 重生态：已用机会
    {0}/{1}"）、`Reducer_Data_WorldState.RA_UpdateGameCount`（遥测只上报）。
  - 消耗：`BattleMain.SaveGameOver → MarkMajorRebirthConsumed`（0x295B770）唯一写入方——
    Used++ 后立即 SaveGameFileCore 落盘（顺带清 HpMaxPenaltyStacks/FaithSpentThisRound）；
    `HistoryChild.RebirthChancesUsed`(0x94) 为存档列表镜像元数据。
  - **排除**：`GameOverTriggerManager.CheckRebirthExceeded`（0x2749B00）是 DemoTwo 专用
    （Initialize 仅 IsDemoTwo && GS DemoTwoGameOver=1 时读 DemoTwoMaxRebirth），正式版恒 false，
    非限制源。
- **为什么补丁而不是表编辑**：getter 内部进程级静态缓存（GameKey+0x20F0：缓存非 0 直接返回，
  首读表后永不回读；条目缺失走 throw helper 0x50d330=不能删条目只能改值）。纯表编辑无法保证
  早于首读（主菜单存档列表 FillSaveInfoFromHistory 很早读取），会随机不生效 → Harmony postfix
  改返回值 999（交易线同款纪律：逐类挂载+异常隔离+开关门控；false=原样返回官方值，
  零状态零还原成本；999 而非 int.MaxValue——UI"已用机会 X/Y"排版与最后机会警告逻辑友好）。
- **存档语义知情**：mod 不写存档；开启期间重生时官方代码照常把 Used 记进存档（可超官方上限），
  关开关后该档按官方公式=无剩余机会（"借"不是"送"）。
- **实现**：`RebirthChanceUnlocker.cs`（postfix ref int __result）；cfg `Rebirth.UnlockChances`
  （默认 true）；首次拦截日志输出官方原值（装机核对锚点）。
- **验证清单（装机）**：首跑日志 `[RebirthUnlock] 首次拦截确认：官方重生机会上限 X -> 999`
  （X=表实值）；耗尽档（死亡页"重生机会耗尽"）开 mod 后重生入口恢复、可正常大重生、选择页显示
  剩余 999−X；主菜单存档列表重生入口不再锁；关开关后耗尽档重新锁死；小重生价格/血上限惩罚照旧；
  存档无本 mod 写入痕迹（Used 增长全部来自官方 MarkMajorRebirthConsumed）。

## 12. 属性上限解锁——主界面四条属性上限封顶 500→可配（2026-11-10，v1.12.0）

- **行为**：饱腹、心态、精力、生命（主界面四条）的"上限值"官方封顶 500，改为最高可堆到
  `Attribute.CapValue`（区间 500~9999，默认 999）。只解除封顶不白送数值——上限能堆多高仍由
  游戏内加上限的规划/天赋/食物/**日常动作（听音乐/浇水/看书等，v1.12.1 起叠层同步放开）**决定；
  当前值跟随上限（可超过 500 直到新上限）。
- **范围定案（2026-11-10 作者定案）**：只放开 101/102/103/105 四行。104 MaxHealth（健康）
  与 NPC 侧（81001+ 行，同为 500 封顶）不动。
- **可见性实证（2026-11-10 作者问询后反汇编补证）**：主界面 HUD 只显示四条属性——
  `Reducer_Web_CoreUI1.RA_Open`（0x2B72930）构建属性列表只插入 key 1/2/3/5（饱腹/心态/
  精力/生命，0x2B72C26~0x2B72C8B 四次 Add 逐条实证，无 key 4）。健康（Health，ID 4）无 HUD
  数值条，但数据层真实运作：TIPS"低于30会陷入【亚健康】、长期饥饿/疲劳影响健康"；Buff 表
  有「健康上限」(ID 14，专加其上限) 与「亚健康」debuff (104/1006 等)——故按作者定案保持官方
  封顶不动，日后想放开只需删 ApplyAttrCap 的 `id == 104` 跳过一行。
- **机制**（build 26454866 反汇编实证，2026-11-10）：
  - 属性体系：`AttrName` 枚举——当前值 1~5（Satiety/Morale/Stamina/Health/Vitality）、上限值
    101~105（MaxSatiety~MaxVitality）、间隔 201~、趋向 301~、系数 10001+/20001+/30001+。
    每个代理的 `AttributeComponent` 持 `AttrDict: Dictionary<AttrName, Attr>`；`Attr` 含
    BaseValue/StrengtheningValue/ConditionValue/Min/Max。
  - 封顶源=`Config_AttributeStandard` 行 101~105 的 `Max`（int 0x34）=500（表 dump 实证五行全
    500；当前值行 1~5 的 Max=int.MaxValue 自身不设限，81001~81004 为 NPC 上限行）。
  - 快照：`AttributeComponent.OnCreate`（0x3083C90）首建 AttrDict 时逐行把 `row.Min(0x30)/
    row.Max(0x34)/row.IsSingle(0x38)` 写进 `Attr`；AttrDict 已存在时只清零数值、**Min/Max 不回读**
    （两个分支均反汇编验证）——即 `Attr.Max` 仅组件创建时读表一次。
  - 钳制：`GetTotalValue`（0x3083640）`total=Strengthening+Base+Condition` 后按 `[Min,Max]`
    钳制（反汇编 0x308369c~0x30836a7：先比 Min 再比 Max，出界即夹回）。上限值本体经
    `GetTotalValue_Int(MaxSatiety)` 读取 → 超 500 一律读成 500；吃/回/增益/UI 的"到顶"判定
    全部经由它，一处表改全链路生效。
  - 旁路排查：`Get_Config_AttributeStandard`（0x2FC90F0）全部 6 处 E8 调用点
    （GroupChatManager.DeliverOneNpcReply / MusicPlaybackTools.GrantTrackFinishMorale /
    Reducer_Data_Pop.MoveNext / Reducer_Web_CoreUI1.RA_Open / Furniture.SettleCookingResult /
    AE_HarvestPlant.ApplyHarvestMorale）逐一反汇编核对，只读 `Des_Local`（本地化名称）等元数据，
    无封顶语义、无静态缓存读取器（与重生线 GameKey 缓存 getter 不同）→ 纯表编辑可靠。
- **时机知情**：改表落主菜单阶段（Ticker 首轮，cm 就绪即写），早于进档创建角色组件——进档
  拿到的 `Attr.Max` 已是目标值。运行中关闭开关（或改小 CapValue）：表立即重写，但**已在场
  角色**的 Attr.Max 缓存要到过图/重进存档（组件重建）后才回官方/新封顶——与其他表线"即时
  还原"不同，属快照语义的已知边界，cfg 描述已注明。
- **防误降**：目标值取 `max(官方首读值, CapValue)`——官方未来若自抬某行上限，本开关不降它；
  CapValue 越界由 AcceptableValueRange(500,9999) 与代码 Clamp 双保险夹取。
- **实现**：`RoleTable.ApplyAttrCap`（迭代 101~105，跳过 104；首读快照原值幂等；基线四行全
  500 自检，不符带 ⚠ 不阻断；target≤0=还原官方值）；cfg `Attribute.UnlockCap`（默认 true）
  + `Attribute.CapValue`（默认 999，int，500~9999）。
- **v1.12.1 第二道锁——上限增益 buff 叠层 200（2026-11-11 作者实测反馈修复）**：封顶 500 实为
  双锁。锁二是 `Config_Buff` 上限类增益 buff 的 `MaxLayerCount=200`（0x94）：听音乐（Action 105）/
  浇水（102）/看书（260）/放松/游戏机/纸飞机/电视/吹风/鉴赏等**全部日常动作加的都是同一批
  "上限"buff**——Effect 表 `ItemEndBuff02/EAddBuff` 指向 buff 11~19（每层 Satiety/Morale/Stamina/
  Health/Vitality 上限 +2~10，`Buff*Max` 字段），`AE_BuffAdd5Attr.Run`（0x3168C30 绝对值分支
  0x3169171）按 `层数×每层值` `AddStrengtheningValue(101~105)`（写入无钳制，0x3081AD4）。叠层走
  `BuffComponent.PlayBuff`（0x3088200）叠层分支 0x3088902：`新层=旧层+AddLayerCount(1)`，夹
  `MaxLayerCount`，**新层==旧层即早退（0x308892F）不触发 TriggerBuffCount → 满层后一次动作零效果**。
  旧档"心态 500"正是 buff 12 叠满 200 层（+400）+初始值所致；仅解除锁一（Attr.Max）时听音乐/
  浇水/看书全都不涨即此。另证：`AddConditionValue`（0x3081A2F `add [rax+18h]`）同样无钳制，
  `LoadGame`（0x30838C4）只恢复 BaseValue 不碰 Max/分量——存档序列化无第三锁。
  修复：`ApplyAttrCap` 同开关同步放开 buff 11/12/13/15/16/17/18/19 的 `MaxLayerCount`
  200→`max(200,CapValue)`（跳过 14 健康上限与 104 同作者定案；300110/300310 等规划 buff 是
  档位语义各 1 层，不在集合不碰；每层值不改——仍+2/次，不是白送）。**PlayBuff 的 [rdi+94h]
  每次实时读表 → 改表即时生效，满 200 层的旧档无需过图立刻可继续叠**（与锁一 Attr.Max 的
  OnCreate 快照语义不同）；运行中关闭=叠层上限立即还原 200（已叠出的层数不动，官方语义
  本就存档持久）。
- **v1.12.1 全来源盘点（2026-11-11 作者定案：唯一上限=CapValue，其余叠层锁全放开）**：全表
  交叉扫描 Config_Effect/Buff/Action/Item/Talent/Event——加上限来源三类：① 200 层通用池
  11~19（浇水/听音乐/看书动作/放松/游戏/纸飞机/无人机/吹风/鉴赏/电视/跑步/洗手×2/按摩 +
  全部书籍 3001~3026 读书 + 牛肉面/蚯蚓干食物；buff 16/17/18 表内无动作/物品引用，剧情线
  预留）；② **塑钢哑铃 11001→buff 11011 体能提升（MaxLayerCount=10，道具描述明示"最多+20"，
  UseTimes=-1 无限使用）——同款叠层锁，一并放开 10→CapValue 并入 AttrCapBuffIds**（基线
  字典按 ID 区分：11~19=200 / 11011=10）；③ 档位类（天赋 资源回收/快速加固/宅人之心 各
  5 档、规划 储备/心理建设/精力分配/强身/紧急 各 3 档、好眠 +20 一次性）每档独立 buff 各
  1 层**无叠层锁可解**——"买满即止"是天赋树/商店的内容结构，不动。放开后语义：所有来源
  叠加，唯一总闸=AttributeStandard.Max（CapValue）经 GetTotalValue 统一钳制。
- **验证清单（装机）**：首跑日志 4 行 `AttrStd 101 饱腹（上限） 数值封顶 500 -> 999（官方 500）`
  等（无 104 行）+ 汇总行"覆盖 4/4 条"；**9 行 `Buff 11 饱腹上限 叠层上限 200 -> 999（官方 200）`
  …`Buff 11011 体能提升 叠层上限 10 -> 999（官方 10）`（无 14 行）+ 汇总"覆盖 9/9 条"**；旧满层档
  进游戏（无需过图）听一次音乐/浇一次水/读一本书/举一次哑铃 → 对应上限 +2/+3；多本书叠加同理；
  吃/睡觉等回复随新上限到顶；改 CapValue=2000 后下一轮日志重写为 2000（封顶在场角色过图生效、
  buff 叠层即时）；关闭开关自动还原 500/官方叠层（封顶在场角色过图后生效、叠层即时）；隐藏属性
  健康与 NPC 邻居等仍按官方封顶；存档无本 mod 写入痕迹。
