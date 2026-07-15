# 🎮 Slay the Spire 2 Mod 开发完全指南

> 基于 [ModTemplate-StS2 Wiki](https://github.com/Alchyr/ModTemplate-StS2/wiki) 和 TokenSpire2 实际开发经验
> 适用版本: STS2 (Godot 4.5 + .NET 9.0)
> 语言: 中文

---

## 目录

1. [STS2 Mod 架构概览](#1-sts2-mod-架构概览)
2. [项目搭建与开发环境](#2-项目搭建与开发环境)
3. [Mod 入口与生命周期](#3-mod-入口与生命周期)
4. [游戏架构与关键系统](#4-游戏架构与关键系统)
5. [ICardSelector — 卡片选择接口](#5-icardselector--卡片选择接口)
6. [屏幕系统与 Overlay Stack](#6-屏幕系统与-overlay-stack)
7. [Harmony 补丁系统](#7-harmony-补丁系统)
8. [多人模式 (Multiplayer)](#8-多人模式-multiplayer)
9. [自动战斗/AI 开发](#9-自动战斗ai-开发)
10. [常见陷阱与最佳实践](#10-常见陷阱与最佳实践)
11. [调试与日志](#11-调试与日志)
12. [构建与部署](#12-构建与部署)

---

## 1. STS2 Mod 架构概览

### 1.1 技术栈

| 层级 | 技术 | 说明 |
|------|------|------|
| 游戏引擎 | Godot 4.5 | C# 绑定 (Mono) |
| .NET 版本 | .NET 9.0 | mod 项目目标框架 |
| Mod 框架 | Alchyr.Sts2.BaseLib | 提供 `[ModInitializer]` 等基础设施 |
| 补丁系统 | HarmonyLib | 运行时 IL 注入 |
| 资源打包 | Godot PCK v3 | mod 资源分发格式 |
| 网络层 | ENet + Steamworks | 多人游戏传输 |

### 1.2 Mod 如何被加载

```
游戏启动
  → Godot 引擎初始化
  → ModManager 扫描 mods/*.pck 和 mods/*/mod_manifest.json
  → 加载 PCK 资源包
  → 从 PCK 中读取 DLL 程序集
  → 反射查找标记了 [ModInitializer] 的类
  → 调用 Initialize() 静态方法
  → Mod 完成初始化，注册 Harmony Patches
```

### 1.3 关键程序集

游戏的核心代码在 `sts2.dll` 中，主要命名空间：

```
MegaCrit.Sts2.Core              — 核心类型
MegaCrit.Sts2.Core.Combat        — 战斗系统
MegaCrit.Sts2.Core.Entities      — 卡牌、遗物、药水
MegaCrit.Sts2.Core.Models        — 数据模型
MegaCrit.Sts2.Core.Nodes         — Godot 节点（场景树）
MegaCrit.Sts2.Core.Nodes.Rooms   — 房间类型
MegaCrit.Sts2.Core.Nodes.Screens — 屏幕/UI 类型
MegaCrit.Sts2.Core.Modding       — [ModInitializer] 标记
MegaCrit.Sts2.Core.TestSupport   — CardSelectCmd 等调试工具
```

---

## 2. 项目搭建与开发环境

### 2.1 项目结构 (ModTemplate-StS2 标准)

```
MyMod/
├── MyMod.csproj           # 项目文件
├── mod_manifest.json      # Mod 元数据（编译时复制到 build_cache/）
├── MainFile.cs            # 入口点 [ModInitializer]
├── src/                   # 源代码
│   ├── Patches/           # Harmony 补丁
│   ├── Handlers/          # 屏幕/场景处理器
│   ├── Solver/            # AI 决策逻辑
│   └── Core/              # 基础设施
├── assets/                # 资源文件
└── tools/                 # 外部工具（启动器等）
```

### 2.2 csproj 关键配置

```xml
<Project Sdk="Godot.NET.Sdk/4.5.1">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>true</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <!-- 必须：防止重复程序集属性 -->
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
  </PropertyGroup>

  <!-- 引用游戏 DLL（不复制到输出，游戏运行时已有） -->
  <ItemGroup>
    <Reference Include="0Harmony">
      <HintPath>$(GameDataPath)\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="sts2">
      <HintPath>$(GameDataPath)\sts2.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <!-- NuGet 包 -->
  <ItemGroup>
    <PackageReference Include="Alchyr.Sts2.BaseLib" Version="*" />
    <PackageReference Include="Alchyr.Sts2.ModAnalyzers" Version="*" PrivateAssets="all" />
    <PackageReference Include="BepInEx.AssemblyPublicizer.MSBuild" Version="0.4.2" PrivateAssets="all" />
  </ItemGroup>

  <!-- Publicizer: 让游戏内部类型可访问 -->
  <ItemGroup>
    <Publicize Include="$(GameDataPath)\sts2.dll" />
  </ItemGroup>
</Project>
```

### 2.3 mod_manifest.json 格式

```json
{
  "id": "MyMod",
  "name": "我的 Mod",
  "version": "1.0.0",
  "description": "Mod 描述",
  "author": "作者名",
  "dependencies": []
}
```

> ⚠️ **重要:** `mod_manifest.json` 必须放在 `mods/` 文件夹**外部**（如 `build_cache/`），因为游戏会递归扫描 `mods/` 下所有 `.json` 文件！如果有两份清单会导致冲突。

---

## 3. Mod 入口与生命周期

### 3.1 [ModInitializer] 标记

```csharp
using MegaCrit.Sts2.Core.Modding;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public static void Initialize()
    {
        // 1. 创建日志器
        var logger = new Logger("MyMod", LogType.Generic);

        // 2. 加载配置
        AppConfig.Initialize(modDirectory);

        // 3. 注册 Harmony Patches
        var harmony = new Harmony("com.mymod.patches");
        harmony.PatchAll(typeof(PatchClass).Assembly);

        // 4. 初始化其他系统
        // ...

        logger.Info("Mod initialized!");
    }
}
```

### 3.2 生命周期顺序

```
1. MainFile.Initialize()      — 最早调用，静态初始化
2. Godot Node._Ready()        — 场景树就绪后（如果继承了 Node）
3. Godot Node._Process(delta) — 每帧调用（~60fps）
4. Godot Node._ExitTree()     — 节点从场景树移除时
5. 游戏退出                    — 自然终止
```

### 3.3 重要限制

- **`Initialize()` 是静态方法** — 不能访问实例成员
- **Godot Node 可能被场景切换销毁** — 核心逻辑应放在不依赖场景树的普通类中
- **AssemblyPublicizer 使游戏内部类型可见** — 但访问 `internal` 成员可能在游戏更新后失效

---

## 4. 游戏架构与关键系统

### 4.1 游戏状态管理

```
RunManager.Instance              — 当前游戏进程管理器
  └── DebugOnlyGetState()        — 获取 RunState（玩家HP、卡组、遗物等）
  └── LocalContext.GetMe(state)  — 获取本地玩家上下文

NGame.Instance                   — 游戏实例
  └── DebugSeedOverride          — 种子覆盖（用于批量测试）
```

### 4.2 战斗系统

```
NCombatRoom.Instance             — 当前战斗房间
  ├── GetCombatState()           — CombatState（怪物、回合、能量）
  ├── IsInProgress               — 战斗是否进行中
  ├── PlayerTurnActive           — 是否玩家回合
  ├── ProceedButton              — 战斗结束后的"继续"按钮
  └── EndTurnButton              — 结束回合按钮

CardModel                        — 卡牌模型
  ├── Id.Entry                   — 卡牌ID（如 "STRIKE", "BASH"）
  ├── Type (Attack/Skill/Power/Curse/Status)
  ├── EnergyCost                 — 能量消耗
  ├── IsUpgraded                 — 是否已升级
  └── IsPlayable                 — 是否可打出
```

### 4.3 地图系统

```
NMapScreen.Instance              — 地图界面
  ├── IsOpen                     — 地图是否打开
  └── Map                        — MapModel（节点、路径）

MapModel
  ├── GetNextRooms()             — 从当前节点可达的下一层房间
  └── CurrentNode                — 当前所在节点
```

### 4.4 房间类型

| 类型 | Node 类 | 说明 |
|------|---------|------|
| 战斗 | `NCombatRoom` | 普通战斗/精英/Boss |
| 火堆 | `RestRoom` | 休息/锻造/回忆 |
| 商店 | `ShopRoom` | 购买卡牌/遗物/药水 |
| 宝箱 | `TreasureRoom` | 获取遗物 |
| 事件 | `EventRoom` | 选择事件选项 |
| 奖励 | `NRewardsScreen` | 战后奖励（金币/药水/选卡） |

---

## 5. ICardSelector — 卡片选择接口

### 5.1 接口定义

```csharp
public interface ICardSelector
{
    // 选择卡片（升级、删除、转化、战斗内 Armaments 等）
    Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect);

    // 选择卡牌奖励（战后选卡）
    CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives);
}
```

### 5.2 注册与注销机制

```csharp
// 注册 — 返回 IDisposable，调用 Dispose() 即可注销
IDisposable scope = CardSelectCmd.UseSelector(mySelector);

// 注销 — 恢复游戏原生UI
scope.Dispose();
```

### 5.3 ⚠️ 关键行为（最容易出错的点）

1. **一旦注册，游戏所有选卡都会经过 ICardSelector** — 包括战斗内（Armaments/True Grit）和非战斗（升级/删除/转化）

2. **如果 `GetSelectedCards` 返回空数组** → 游戏跳过 UI **且不选任何卡** → 操作静默失败

3. **如果 `GetSelectedCards` 返回非空结果** → 游戏直接使用返回的卡片，**UI 完全不显示**

4. **要显示原生 UI** → **必须注销** ICardSelector（调用 `Dispose()`），让 `CardSelectCmd` 走正常流程

### 5.4 推荐模式：动态注册/注销

```csharp
// 在 _Process 中根据屏幕状态动态切换
bool needAutoSelect = !IsHostManualMode || IsInCombat;
if (needAutoSelect && _selectorScope == null)
{
    _selectorScope = CardSelectCmd.UseSelector(_selector);
}
else if (!needAutoSelect && _selectorScope != null)
{
    _selectorScope.Dispose();
    _selectorScope = null;
}
```

### 5.5 多人模式选卡的特殊性

在多人 lockstep 模式下，**所有实例必须选择相同的卡片**，否则会导致 `StateDivergence`（状态分歧）。因此：
- Bot 使用相同的打分逻辑（确保确定性）
- Host 手动选卡时，必须**注销 ICardSelector** 让原生 UI 弹出
- 战斗内选卡（Armaments 等）由 ICardSelector 自动处理

---

## 6. 屏幕系统与 Overlay Stack

### 6.1 NOverlayStack

```csharp
NOverlayStack.Instance
    ├── ScreenCount          — 当前 Overlay 层数
    ├── Peek()               — 获取最顶层 Overlay（不移除）
    ├── Push(overlay)        — 压入新 Overlay
    └── Remove(overlay)      — 移除指定 Overlay
```

Overlay 类型包括：
- `NRewardsScreen` — 战后奖励
- `NCardRewardSelectionScreen` — 选卡界面
- `NCardUpgradeScreen` — 升级界面
- `NCardRemoveScreen` — 删牌界面
- `NModalContainer` — 模态弹窗

### 6.2 NModalContainer

```csharp
NModalContainer.Instance
    └── OpenModal            — 当前打开的模态弹窗
```

模态弹窗通常是确认对话框（如"确定要放弃吗？"）。

### 6.3 屏幕检测模式

```csharp
// 判断当前处于什么界面
var overlay = NOverlayStack.Instance?.Peek();
string typeName = overlay?.GetType().Name;

if (typeName.Contains("Upgrade"))    → 升级界面
if (typeName.Contains("Remove"))     → 删牌界面
if (typeName.Contains("Transform"))  → 转化界面
if (typeName.Contains("Rewards"))    → 战后奖励
if (typeName.Contains("CardReward")) → 选卡奖励
```

---

## 7. Harmony 补丁系统

### 7.1 基本用法

```csharp
using HarmonyLib;

[HarmonyPatch(typeof(TargetClass))]
[HarmonyPatch("TargetMethod")]
[HarmonyPatch(new Type[] { typeof(ParamType1), typeof(ParamType2) })]
public class MyPatch
{
    // Prefix: 在原方法执行前调用
    public static bool Prefix(TargetClass __instance, ParamType1 p1, ref bool __result)
    {
        // 返回 false 跳过原方法
        // 设置 __result 提供返回值
        return true;
    }

    // Postfix: 在原方法执行后调用
    public static void Postfix(TargetClass __instance, ref ReturnType __result)
    {
        // 修改返回值
        __result = modifiedValue;
    }
}
```

### 7.2 常用注入技术

| 技术 | 说明 | 用途 |
|------|------|------|
| Prefix | 在原方法前执行 | 修改/拦截输入 |
| Postfix | 在原方法后执行 | 修改返回值 |
| Transpiler | 修改 IL 代码 | 最深层的修改 |
| Finalizer | 类似 finally | 异常处理 |

### 7.3 ⚠️ 重要注意事项

1. **TargetMethod 要精确匹配** — 方法名和参数类型都要对
2. **游戏更新可能改变方法签名** — 每次更新后检查 Patch 是否仍然有效
3. **用 try-catch 包装** — Patch 失败不应导致游戏崩溃
4. **不要在同一方法上堆叠多个 Patch** — 它们按注册顺序执行，互相影响
5. **Prefix 返回 false 时 Postfix 不会执行** — 理解这个很重要

### 7.4 TokenSpire2 使用的 Patch 类型

```csharp
// Steam 相关 Patch — 用于绕过 Steam 匹配
SteamPersonaNamePatch   — 自定义玩家名
SteamIdPatch            — 修改 Steam ID 避免冲突

// ENet 网络 Patch — 直连模式
ForceENetHostPatch      — 强制 ENet Host 模式
ENetClientNetIdPatch    — 修复网络ID分配

// UI Patch
FlavorTextPatch         — 自定义角色描述文本
```

---

## 8. 多人模式 (Multiplayer)

### 8.1 架构概述

STS2 多人使用 **lockstep 确定性同步**：
- Host 运行 ENet 服务器（`--fastmp host_standard`）
- Client 连接到 Host（`--fastmp join`）
- 所有游戏逻辑在**每个实例上独立运行**
- 只有**玩家输入**通过网络传输
- 结果必须**完全相同**，否则触发 `StateDivergence` 断开连接

### 8.2 Bot 在多人模式中的角色

```
Host (Player)      Bot1 (Client)      Bot2 (Client)
    │                   │                   │
    ├─ 创建房间 ────────┤                   │
    │←── Bot1 加入 ─────┤                   │
    │←── Bot2 加入 ────────────────────────┤
    │                   │                   │
    │  所有实例运行相同的游戏逻辑（lockstep）      │
    │                   │                   │
    ├─ 选卡升级 ───→ 同步到 Bot1 ──→ 同步到 Bot2
```

### 8.3 StateDivergence 常见原因

| 原因 | 说明 | 预防 |
|------|------|------|
| `System.Random` 种子不同 | 每个实例 `new Random()` 产生不同序列 | 使用确定性算法（FNV-1a Hash） |
| 卡牌选择不同 | ICardSelector 在不同实例上选择不同卡 | 确定性打分 + 次级排序 |
| UI 操作时机不同 | 不同实例在不同帧点击按钮 | 使用游戏 API 而非 UI 点击 |
| `async` 结果不同 | Task 完成顺序不确定 | 避免在决策路径中使用 async |

### 8.4 确定性随机替代

```csharp
// ❌ 错误 — 不同实例产生不同随机数
var rng = new System.Random();
int pick = rng.Next(list.Count);

// ✅ 正确 — 基于内容的确定性哈希
uint hash = 2166136261; // FNV-1a offset basis
foreach (var item in list)
{
    string id = item.ToString();
    foreach (char c in id)
        hash = unchecked((hash ^ c) * 16777619);
}
int pick = (int)(hash % (uint)list.Count);
```

---

## 9. 自动战斗/AI 开发

### 9.1 决策架构

```
_Process (每帧)
  ├── 屏幕检测 (GameStateDetector)
  │   ├── MAIN_MENU     → 导航到多人/开始游戏
  │   ├── MAP           → MapDecider (选路)
  │   ├── COMBAT        → CombatHandler → IroncladSolver
  │   ├── REST          → RestDecider (休息/升级)
  │   ├── SHOP          → ShopDecider (购买/删牌)
  │   ├── EVENT         → EventDecider (事件选项)
  │   ├── TREASURE      → TreasureDecider (开箱)
  │   ├── CARD_REWARD   → CardRewardDecider (选卡)
  │   └── CARD_GRID     → CardGridDecider (升级/删除选择)
  └── DismissContinuePrompt (关弹窗)
```

### 9.2 Decider 模式

每个 Decider 遵循统一接口：
```csharp
// 每个 Decider 有一个 Decide() 方法
public static void Decide(GameScreen screen, double delta)
{
    // 1. 检测状态稳定性（StateStabilityDetector）
    if (!StateStabilityDetector.IsStable(screen)) return;

    // 2. 评估所有选项
    var options = GetAvailableOptions();
    var scored = options.Select(o => (option: o, score: EvaluateOption(o)));

    // 3. 选择最佳选项
    var best = scored.OrderByDescending(x => x.score).First();

    // 4. 执行选择
    ExecuteChoice(best.option);

    // 5. 记录审计日志
    DecisionLogger.Log(screen, scored, best);
}
```

### 9.3 打分系统设计

```csharp
// 火堆决策打分示例
double EvaluateRestOption(RestSiteOption option)
{
    string lower = option.ToString().ToLower();

    if (lower.Contains("smith") || lower.Contains("upgrade"))
    {
        // 升级：检查是否有值得升级的卡
        int upgradeCandidates = deck.Count(c => !c.IsUpgraded && !IsBasicCard(c));
        return upgradeCandidates > 0 ? 60 + upgradeCandidates * 10 : 0;
    }

    if (lower.Contains("rest") || lower.Contains("heal"))
    {
        // 治疗：HP 越低需求越高
        double hpRatio = currentHp / maxHp;
        if (hpRatio < 0.4) return 100;      // 危险 — 必须治疗
        if (hpRatio < 0.7) return 70;       // 偏低 — 建议治疗
        return 30;                           // 健康 — 不治疗
    }

    return 0;
}
```

---

## 10. 常见陷阱与最佳实践

### 10.1 ❌ 不要在 `_Process` 中使用 Thread.Sleep

```csharp
// ❌ 错误 — 冻结整个游戏
Thread.Sleep(1000);

// ✅ 正确 — 使用 cooldown 机制
_cooldown = 1.0; // 1秒内跳过处理
if (_cooldown > 0) { _cooldown -= delta; return; }
```

### 10.2 ❌ 不要硬编码 Godot 节点路径

```csharp
// ❌ 错误 — 游戏更新后可能失效
var btn = GetNode<Button>("/root/Game/RootSceneContainer/MainMenu/Buttons/PlayButton");

// ✅ 正确 — 使用游戏提供的 API
var menu = NMainMenu.Instance;
var btn = menu?.GetNode<Button>("PlayButton");
```

### 10.3 ❌ 不要假设游戏单例始终存在

```csharp
// ❌ 错误 — NullReferenceException
var state = RunManager.Instance.DebugOnlyGetState();

// ✅ 正确 — 防御性检查
var rm = RunManager.Instance;
if (rm == null) return;
var state = rm.DebugOnlyGetState();
if (state == null) return;
```

### 10.4 ❌ 不要用 ForceClick 代替正常交互

```csharp
// ❌ 错误 — 绕过游戏逻辑，多人下不同步
button.ForceClick();

// ✅ 正确 — 检查状态后再点击
if (button.IsEnabled && button.Visible)
    button.Press(); // 或让游戏自然处理
```

### 10.5 ✅ 多人模式的决策必须确定性

```csharp
// ❌ 错误 — 时间相关
int pick = DateTime.Now.Millisecond % options.Count;

// ✅ 正确 — 内容相关
int pick = HashOptions(options) % options.Count;
```

### 10.6 ✅ 每个 Harmony Patch 应独立且可失败

```csharp
try
{
    harmony.PatchAll(typeof(MyPatch).Assembly);
    logger.Info("Patch applied successfully");
}
catch (Exception ex)
{
    logger.Warn($"Patch failed (non-fatal): {ex.Message}");
    // Mod 继续运行，只是某个功能不可用
}
```

---

## 11. 调试与日志

### 11.1 日志位置

```
%APPDATA%/SlayTheSpire2/logs/godot<timestamp>.log
```

### 11.2 日志级别

```csharp
Logger.Info("一般信息");
Logger.Warn("警告信息");
Logger.Error("错误信息");
Logger.Debug("调试信息"); // 需要开启调试模式
```

### 11.3 多人模式的日志策略

在多人 lockstep 模式下，**所有实例产生相同的日志**。为了区分：

```csharp
string prefix = _isMultiplayerHost ? "[HOST]" : $"[BOT{_botIndex}]";
Logger.Info($"{prefix} 当前操作: ...");
```

### 11.4 减少日志噪音

```csharp
// 使用 LogOnce 避免重复日志
private string _lastLogMessage = "";
private void LogOnce(string msg)
{
    if (msg != _lastLogMessage)
    {
        Logger.Info(msg);
        _lastLogMessage = msg;
    }
}

// 只在状态改变时记录
if (currentScreen != _lastScreen)
{
    Logger.Info($"Screen changed: {_lastScreen} → {currentScreen}");
    _lastScreen = currentScreen;
}
```

---

## 12. 构建与部署

### 12.1 构建流程

```bash
# 构建 DLL
dotnet build -c Release

# 输出位置:
# bin/Release/net9.0/TokenSpire2.dll

# 部署到 3 个位置:
# 1. mods/TokenSpire2/TokenSpire2.dll   (mod 自身)
# 2. mods/TokenSpire2.dll               (mods 根目录)
# 3. data_sts2_windows_x86_64/TokenSpire2.dll  (游戏数据目录)
```

### 12.2 PCK 打包

PCK 是 Godot 的资源包格式。Mod 的 PCK 包含 `mod_manifest.json`：

```
PCK v3 格式:
  Header (112 bytes)
    ├── Magic "GDPC"
    ├── Version = 3
    ├── Godot Version (4.5.1)
    ├── Flags = 2
    └── Reserved (72 bytes)
  File Data
    └── mod_manifest.json 内容
  File Table (在文件末尾)
    └── 1 entry: path + offset + size + MD5
```

### 12.3 发布检查清单

- [ ] DLL 已构建为 Release 配置
- [ ] PCK 文件已生成
- [ ] mod_manifest.json 在 mods/ 外部
- [ ] 所有 Harmony Patch 有 try-catch 保护
- [ ] 无硬编码的绝对路径
- [ ] 单人和多人模式均已测试
- [ ] 日志级别适合生产环境

---

## 附录 A: 关键类型速查

### A.1 游戏引擎级接口

| 接口/类 | 用途 | 注意事项 |
|---------|------|---------|
| `ICardSelector` | 全局选卡钩子 | 注册后所有选卡都走这里 |
| `CardSelectCmd.UseSelector()` | 注册 ICardSelector | 返回 IDisposable |
| `NOverlayStack` | Overlay 栈管理 | Peek() 不弹出，Remove() 弹出 |
| `NModalContainer` | 模态弹窗管理 | OpenModal 可能为 null |
| `NCombatRoom` | 战斗房间 | Instance 可能为 null |
| `NMapScreen` | 地图界面 | 仅在 MAP 状态可用 |

### A.2 游戏数据访问

| 类/方法 | 用途 | 注意事项 |
|---------|------|---------|
| `RunManager.Instance.DebugOnlyGetState()` | 获取运行状态 | Debug API，可能不稳定 |
| `LocalContext.GetMe(state)` | 获取本地玩家 | 多人模式返回正确的玩家 |
| `NGame.Instance.DebugSeedOverride` | 设置种子 | 批量测试必备 |
| `CardModel.Id.Entry` | 卡牌ID | 如 "STRIKE", "BASH_UPGRADED" |

---

## 附录 B: 参考资源

- [ModTemplate-StS2 Wiki](https://github.com/Alchyr/ModTemplate-StS2/wiki) — 官方 Mod 模板文档
- [Harmony 文档](https://harmony.pardeike.net/) — IL 补丁框架
- [Godot .NET 文档](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/) — Godot C# API
- [FNV-1a Hash](https://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function) — 确定性哈希算法
