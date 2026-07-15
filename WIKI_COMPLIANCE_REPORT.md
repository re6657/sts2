# TokenSpire2 Wiki 合规审查报告

> 审查日期: 2026-07-14
> 参考规范: [ModTemplate-StS2 Wiki](https://github.com/Alchyr/ModTemplate-StS2/wiki)
> 审查范围: 全部 src/ 源代码 + MainFile.cs + Harmony Patches

---

## 🔴 严重冲突 (CRITICAL)

### 1. ICardSelector 全局注册阻塞原生UI交互

**涉及文件:** `AutoSlayNode.cs:254-255`, `AutoSlayCardSelector.cs`

**问题描述:**
`ICardSelector` 在 `_Ready()` 中被注册并**永不注销**:
```csharp
_cardSelector = new AutoSlayCardSelector(_rng, null);
_cardSelectorScope = CardSelectCmd.UseSelector(_cardSelector);
```

根据 Wiki 规范，`ICardSelector` 是一个**游戏引擎级别的全局钩子**。一旦注册，**所有**卡片选择界面（升级、删除、转化、战斗内选卡如 Armaments/True Grit）都会调用 `GetSelectedCards()`。如果该方法返回非空结果，游戏引擎**直接使用返回的卡片，跳过原生UI**。

**影响:**
- **Host 玩家无法手动升级/删除卡牌** — 点击"锻造"或"删除"后无UI弹出，操作静默失败
- **Bot 自动选卡和 Host 手动选卡的逻辑冲突** — 两者共用一个 ICardSelector
- 多人模式下，Host 战斗内选卡（Armaments 等）也被自动代劳

**Wiki 推荐做法:**
- ICardSelector 应该仅在需要自动选卡的**特定场景**注册
- 不需要时应**完全注销**（`Dispose()` 返回的 `IDisposable`），让游戏原生UI接管
- 或者通过返回空集合来表示"我不处理此选择"，但这要求 ICardSelector 内部能区分场景

**修复方向:**
需要在 `AutoSlayNode._Process()` 中根据当前屏幕状态动态注册/注销 ICardSelector:
- Host 在非战斗场景（火堆/商店/事件选卡）→ 注销 → 原生UI
- Host 在战斗内（Armaments等）→ 注册 → 自动选
- Bot 在任何场景 → 注册 → 自动选

---

### 2. GetSelectedCards 的 async 模式与游戏引擎生命周期不匹配

**涉及文件:** `AutoSlayCardSelector.cs:41-46`

**问题描述:**
```csharp
public async Task<IEnumerable<CardModel>> GetSelectedCards(...)
```

`ICardSelector.GetSelectedCards` 被声明为 `async Task<>`。游戏引擎在调用此方法时可能：
- 在同步上下文中调用，导致 `async` 无法正常完成
- 在 `GetSelectedCardsInner` 中调用 `await _llm.SendAsync()` 时，如果 LLM 超时（30s+），游戏引擎可能已经超时放弃
- `async void` 类似的 fire-and-forget 行为可能导致选卡结果丢失

**Wiki 推荐做法:**
ICardSelector 的方法应该尽可能是**同步**的。如果必须异步（如 LLM 调用），应当：
- 使用同步等待（`.GetAwaiter().GetResult()`）或
- 在调用前预计算结果，使 `GetSelectedCards` 直接返回缓存结果

---

### 3. ForceClick() 绕过游戏交互逻辑

**涉及文件:** `AutoSlayNode.cs` 多处 (DismissContinuePrompt, HandleMainMenu, etc.)

**问题描述:**
大量使用 `ForceClick()` 代替正常的 UI 交互流程:
```csharp
abandonBtn.ForceClick();
proceed.ForceClick();
yesBtn.ForceClick();
```

`ForceClick()` 直接触发按钮的信号，但**不经过**游戏的输入验证、网络同步（多人模式）、动画过渡等逻辑。这可能导致：
- 多人模式下操作不同步（主机点了但客户端没收到）
- 跳过确认弹窗导致意外操作
- 按钮状态尚未就绪就被强制点击

**Wiki 推荐做法:**
应优先使用 `Press()` 或正常的输入事件。`ForceClick()` 仅应在确认按钮已就绪且可交互时使用。

---

## 🟡 中度冲突 (MEDIUM)

### 4. Godot Node 路径硬编码

**涉及文件:** `AutoSlayNode.cs` 多处

**问题描述:**
大量硬编码 Godot 场景树路径:
```csharp
"/root/Game/RootSceneContainer/MainMenu"
"MainMenuTextButtons/AbandonRunButton"
"VerticalPopup/YesButton"
```

这些路径在游戏更新时可能改变，导致 mod 在新版本中失效。

**Wiki 推荐做法:**
应使用游戏提供的公共 API（如 `NMainMenu.Instance`, `NOverlayStack.Instance`）来获取节点引用，而非硬编码路径。

---

### 5. _Process 主线中执行重量级逻辑

**涉及文件:** `AutoSlayNode.cs:_Process()` (~3000+ 行)

**问题描述:**
Godot 的 `_Process(double delta)` 在**渲染主线程**每帧调用。`TokenSpire2` 的 `_Process` 包含了：
- 决策引擎路由 (DecisionEngine.Decide)
- Solver 搜索 (IroncladSolver)
- LLM 调用 (async 但以同步方式等待)
- 多层嵌套的屏幕检测和按钮点击

大量计算在主线程会导致：
- 游戏帧率下降
- UI 卡顿
- 多人模式下的网络同步超时

**Wiki 推荐做法:**
重的计算逻辑应放在后台线程（`Task.Run`）或使用 Godot 的 `WorkerThreadPool`。

---

### 6. Harmony Patch TargetMethod 脆弱性

**涉及文件:** `Patches/` 目录下所有 Patch 文件

**问题描述:**
Harmony 补丁通过字符串或方法签名匹配目标方法。游戏更新后：
- 方法名可能改变
- 方法签名可能改变
- 类型可能移到不同的命名空间

当前补丁:
- `ForceENetHostPatch` — 补丁 `ENetClient.ConnectToHost`
- `SteamPersonaNamePatch` — 补丁 Steam 身份
- `SteamIdPatch` — 补丁 Steam ID
- `ENetClientNetIdPatch` — 补丁 ENet 网络ID
- `FlavorTextPatch` — 补丁角色文本

**Wiki 推荐做法:**
每个 Harmony Patch 应包含 try-catch 保护，Patch 失败时不应导致 mod 崩溃。应使用 `Harmony.DEBUG` 标记来追踪补丁状态。

---

### 7. 直接访问游戏内部单例

**涉及文件:** 几乎所有源文件

**问题描述:**
直接访问大量游戏内部单例，没有 null 检查或 fallback:
```csharp
NCombatRoom.Instance
NMapScreen.Instance
NOverlayStack.Instance
NModalContainer.Instance
RunManager.Instance
NGame.Instance
```

这些单例在游戏不同阶段可能为 null（启动、加载、菜单切换时），导致 NullReferenceException。

**Wiki 推荐做法:**
访问游戏单例前必须进行 null 检查。使用 `?.` 和 `??` 操作符提供安全的 fallback。

---

## 🟢 轻微冲突 (MINOR)

### 8. mod_manifest.json 管理方式

**涉及文件:** `TokenSpire2.csproj:158-165`

**问题描述:**
mod 的构建清单 (`mod_manifest.json`) 被放在 `TokenSpire2_build_cache/` 目录（mods 文件夹外部），这是正确的做法（游戏会扫描 mods/ 下所有 .json 文件）。但 PCK 生成逻辑内联在 csproj 中，可维护性差。

**Wiki 推荐做法:**
应将 PCK 生成逻辑提取到独立的 MSBuild targets 文件或构建脚本中。

---

### 9. AutoSlayNode 依赖 Godot Node 生命周期

**涉及文件:** `AutoSlayNode.cs` — 继承自 Node

**问题描述:**
整个模组的核心逻辑放在一个 Godot `Node` 子类中（`AutoSlayNode`），依赖 `_Ready()`, `_Process()`, `_ExitTree()` 等 Godot 生命周期。如果 Godot 场景树在运行时重建（比如场景切换），这个 Node 可能被销毁。

**Wiki 推荐做法:**
核心逻辑应放在不依赖场景树生命周期的普通 C# 类中，仅将 UI 交互层放在 Node 里。使用 `Autoload` 单例或 `[ModInitializer]` 的静态上下文。

---

### 10. 日志系统过多

**涉及文件:** 多个文件

**问题描述:**
大量的 `MainFile.Logger.Info()` 调用 — 在多人模式、自动战斗中产生海量日志。尽管这不会导致功能问题，但：
- 拖慢文件 I/O
- 让调试真正问题变得困难
- 在多人模式下，每个实例都产生相同的大量日志

---

## 📊 优先级修复建议

| 优先级 | 编号 | 问题 | 修复复杂度 | 影响范围 |
|--------|------|------|------------|----------|
| 🔴 P0 | #1 | ICardSelector 全局劫持 | 中 | Host 完全无法手动操作 |
| 🔴 P0 | #2 | async 与引擎生命周期 | 低 | 可能丢选卡/StateDivergence |
| 🔴 P1 | #3 | ForceClick 绕过 | 中 | 多人同步、UI 状态异常 |
| 🟡 P2 | #4 | 路径硬编码 | 低 | 游戏更新后失效 |
| 🟡 P2 | #7 | 单例 null 访问 | 低 | 随机 NRE 崩溃 |
| 🟡 P3 | #5 | 主线重量级计算 | 高 | 帧率下降 |
| 🟢 P4 | #6 | Patch 脆弱性 | 中 | 游戏更新后失效 |
| 🟢 P4 | #8-10 | 其他轻微问题 | 低 | 维护性/可调试性 |
