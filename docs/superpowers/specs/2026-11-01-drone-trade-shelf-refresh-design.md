# 交易解锁（无人机货架即时刷新）设计文档

- 日期：2026-11-01（2026-11-02 修订：归属本仓功能扩展 + mod 改名）
- 状态：设计定稿，待 spike 验证 detour 触发（见"验证计划"）
- 归属：**本 mod（现 RoleUnlock）的功能扩展**，非新仓。功能名：**交易解锁**，配置节 `Trade`
- 适配版本：游戏 26157732（2026-10-23 dump 反汇编实证；mod 按类名/方法名解析，不怕 RVA 变化，游戏更新后需重验签名与调用方式）

## 〇、mod 定位与改名

本 mod 定位从"角色专属解锁"扩展为**通用解锁合集**（现有：角色线 5 项；新增：交易解锁；未来：同类解锁陆续加入）。现名 RoleUnlock/角色专属解锁已不覆盖新定位，**随本次迭代改名**：

- 建议新名：**UnlockHub（解锁中心）**，GUID `com.local.survivallog.unlockhub`
- 改名范围（本仓）：仓夹名、工程目录/csproj（`SurvivalLog.UnlockHub`）、插件 Guid/Name/命名空间、cfg 文件名（旧 `com.local.survivallog.roleunlock.cfg` 留存无害，新 cfg 按默认生成）、README/ReleaseREADME/DESIGN/CHANGELOG（历史保留+改名说明）、release.ps1 cfg 归一键表（新增 `UnlockTrade=true`）、欢迎日志
- 改名范围（套件侧，发版同步清单）：GitHub 仓 `survivallog-mods/RoleUnlock → UnlockHub`、ModCenter 注册、套件 version.json
- 玩家影响：老玩家 cfg 开关回到默认（全部默认 true，行为无实质变化）；旧 dll 需手删（套件常规）

## 一、意图与为什么

官方旧版：无人机交易每次交易完成后货架立即"补货"（快照不持久，见"机制考古"）。官方后来改为：买掉的量入档扣减，每天只按营地铁状态随机补 3~5 种，重复买同一种货必须等次日。

本功能恢复"交易后即可再买"的体验。**已拍板姿势1**：每次交易后删除该货架的库存快照，下次打开交易点时由游戏自己的 `DrawActiveShelf` 重新随机上架一个满货架。

**知情语义（设计选择，非 bug）**：姿势1 的"刷新"= 换一个全新随机货架，不是"同一批货回满"（那是姿势2，未采用）。同一货架关了再开，货物品类会变。

## 二、目标 / 非目标 / 反目标

**目标**
- 每次交易后自动触发，无玩家操作
- 下次打开交易点货架自动全新上架（100% 走游戏官方 `DrawActiveShelf` 路径，mod 不伪造任何货架数据）
- 零卡顿；无长驻无条件轮询
- cfg 开关 `Trade.UnlockTrade`（默认 true），关闭即回官方行为；`General.Enabled` 总开关同时管辖

**非目标**
- 不做"当天固定列表+数量回满"（姿势2）
- 不覆盖休闲模式稀有货架（`CasualCampShelf`，独立消耗路径，稀有限制保留）
- 不做 UI 界面开着时的原地即时刷新（重开界面生效即止，符合无人机往返操作流）

**反目标**
- 不改游戏文件、不写任何非官方格式的存档数据（存档里永远只有游戏自己写的东西）
- 不引入常驻后台重轮询：当天没有交易会话，就不做任何实际工作

## 三、机制（v26157732 反汇编实证）

### 存档侧数据（SaveChildData）

| 字段 | 偏移 | 含义 |
|---|---|---|
| `ActiveShelfStock` | 0x470 | `Dictionary<int,string>` 货架id → 库存快照 JSON `{"物品ID":数量}` |
| `ActiveShelfRestockDay` | 0x478 | `Dictionary<int,int>` 货架id → 上次补货日 |

（旧套 `ActiveShelfDay`(0x438)/`ActiveShelfSnapshots`(0x440) 为旧机制尸体，逻辑零引用，仅存档序列化器读写兼容，不碰。）

### 关键路径

1. **购买**：`Reducer_Web_TradeUI.DealCore` → `ConsumeActiveShelfStock(int shelfShopId, Dictionary<int,int> taken)`（RVA 0x2CA5990，private static）。只把快照数量减掉后写回，不触发补货。DealCore 对它是**直接 E8 调用**（@0x2ca6ac3，未内联）。
2. **补货/重抽**：`ResolveActiveShelf(int, bool, int)`（RVA 0x2CB5200，由 OpenUICore/GetShelfFillPct 调用）：
   - 有快照 → 若 `RestockDay[id] < 今天` 走 `RestockActiveShelf` 按天部分补货（3~5 种/天）；
   - **无快照 → `DrawActiveShelf` 全量随机上架 + 写回 + `RestockDay[id]=今天`** ← 本功能借用的官方兜底分支。
3. **安全性**：`ConsumeActiveShelfStock` 对缺键 TryGetValue 早退（@0x2ca5afe 已验证）；所有读方均 TryGetValue 守卫；Unity 单线程，无并发问题。少一个键是合法存档状态（旧档本来就没有）。

### 机制考古（旧官方怎么做的，为何可借鉴）

旧版只存"当天上架列表"快照（Snapshots+Day 判日重抽），**数量不入档** → 当天内怎么买都满。现行版本加的 Stock+RestockDay 层才是"买掉要等次日"的来源。"无快照 → 重抽满货架"这条旧路径至今活着——本功能删快照即是让游戏自己走回旧路。

## 四、触发方案（两级，按顺序）

### 首选：Harmony detour 动作触发（套件成熟先例）

```
postfix Reducer_Web_TradeUI.ConsumeActiveShelfStock(shelfShopId, taken)
  → 交易发生瞬间：ActiveShelfStock.Remove(shelfShopId)
```

- **套件先例充分**：EndlessGuard（FallBlocker/FallRestorer/DeviceXp*）、CabinetEverywhere、CabinetSize 均在用 HarmonyLib；本仓此前"零 Harmony"是 RoleUnlock 表编辑路线的局部选择，非套件禁令
- **合规性**（EndlessGuard 军律）：非 async 状态机（普通私有静态方法）✓；按 EndlessGuardPlugin 同款**逐类挂载+异常隔离**（禁 PatchAll 整包）✓；TargetMethod 用钉死参数类型数组（FallRestorer 同款）`(int, Il2CppSystem…Dictionary<int,int>)`，postfix 只读 `__0`（int），不碰泛型字典内容——避开"泛型 List 参数补丁静默失败"已知坑
- **存档访问链**（全 interop，FallRestorer/ArrowTowerFx 同款单例模式）：
  `BaseSingleton<GameSaveManager>.Instance.GetGameSaveData().CurSave.ActiveShelfStock.Remove(shelfShopId)`
- 删除发生在 DealCore 消费返回瞬间。已实证 DealCore 消费后不回读快照（结算段走本地数据）；界面内其余路径未全枚举——spike 回归来兜（见验证计划 6）
- 若回归发现界面内异常（货架数量显示不同步等）：升级为**两段式**——postfix 只记账 shelfId 进待删队列，`ResolveActiveShelf` prefix 里删除待删键再放行（删除点=游戏正要读快照前的一瞬，时序最干净，零界面状态检测）

### Fallback：条件轮询（仅当 detour 验证不可行）

**拉起—执行—停止 lifecycle（非常驻）：**

- **拉起**：检测到交易界面打开（TradeUI Redux 状态存在）→ 启动完整轮询
- **执行**：每 ~0.5s 枚举 `ActiveShelfStock`、解析各货架总量并与上轮对比；**总量下降 = 交易发生 → 删该键**（防误触发单调性规则：消耗只会使总量严格下降；官方重抽/补货只会上升。例：满10→买后4→删→重抽9，9≥4 判定为重抽 ✓）
- **停止**：交易界面关闭（或最后一次变化后 N 秒无新变化）→ 收尾本轮 → 停止轮询，等下次界面打开再拉起

**空载心跳的诚实说明**：完全零心跳做不到（无原生"界面打开"事件源时，检测打开本身需要看一眼状态）。设计为：空闲时 Ticker 每 ~1s 只做一次"界面是否打开"的布尔查询（纳秒级，无字典枚举无 JSON 解析）；只有界面打开期间才升到完整轮询档；**当天不打开交易界面 = 全天零实际工作**。这与"整日跑重轮询"是两个量级，符合"没交易就不干活"的要求。

## 五、实现要点

- 落点：本仓改名 UnlockHub 后的新工程 `SurvivalLog.UnlockHub`，新文件 `TradeShelfRefresher.cs`（Harmony patch 类）+ 插件 Load 挂载
- cfg：`Trade` 节 `UnlockTrade`（默认 true，人话描述对齐现有风格）；`General.Enabled` 总开关关闭时不挂/不生效
- `ActiveShelfRestockDay` 不动（重抽分支自会写今天）
- 日志：首次拦截打一条确认（验证用），之后静默或低频；全程 try/catch + trace（FallRestorer 同款幂等纪律）
- 无 mod 本地文件

## 六、验证计划

**Spike（第一步，只验触发）**
1. 单钩子版加载无异常，进游戏完成一笔交易，日志出现"已拦截"→ detour 可行，走首选方案
2. 钩子不触发/挂载失败 → 转 fallback 条件轮询

**功能回归（spike 通过后）**
3. 连买多笔 → 关开交易界面 → 全新满货架
4. 只开关界面不交易 → 货架不变（无删除发生）
5. 跨日：官方每日补货逻辑未被破坏（隔天重抽/补货正常）
6. 一次界面会话多笔交易期间界面显示正常（数量、已售项无异常）；异常则升级两段式
7. 存档/读档往返、重进游戏
8. 休闲模式：稀有货架仍按官方（独立路径未被波及）
9. 钉选物品（ActiveShelfPinned）跨重抽表现，记录实测结果

## 七、已知限制

- 语义=换新货架（非同款回满）；连续开关可反复刷货架——这是姿势1 的固有属性
- 休闲模式稀有货架限制保留（如需覆盖另立设计）
- 游戏更新后需重验：方法签名、`ConsumeActiveShelfStock` 是否仍被直接调用（若被内联则 detour 失效转轮询）
