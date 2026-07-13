# TokenSpire2 Mod — 综合 Bug 审计报告 (最终版)

> **审计日期:** 2026-07-13
> **审计方法:** 5 路并行 Agent 全面审查 + 手动审计关键路径
> **审计范围:** 80+ 源文件，覆盖 AutoSlayNode / Solver / Handlers / Core / Chat / Llm / AutoBattle / Patches / Launcher
> **Agent 覆盖:** Core 基础设施 (14文件), Handlers+Patches+Chat+Llm (35文件), Solver (32文件), AutoSlayNode 主循环

---

## 摘要

| 严重度 | 数量 | 说明 |
|--------|------|------|
| **CRITICAL** | 16 | 游戏崩溃、死锁、数据损坏、AI 完全失效 |
| **HIGH** | 33 | 错误行为、功能失效、性能退化 |
| **MEDIUM** | 41 | 边界条件 Bug、维护危害、不正确评分 |
| **LOW** | 43 | 代码异味、死代码、小问题 |
| **总计** | **133** | |

---

## CRITICAL (16)

### 核心战斗 AI 损坏

**C1. CombatHandler 误用 Card.CombatState 导致敌方目标解析永久失败**
- `src/Handlers/CombatHandler.cs` ~109
- 方法在第 63 行捕获 `var combatState = player.Creature.CombatState` 但从未使用。第 109 行读取 `card.CombatState?.HittableEnemies`——手牌卡牌的 `CombatState` 几乎总是 null。所有敌方目标卡牌获得空敌人列表，`target` 永远为 null。**Bot 无法对敌人使用任何目标卡牌。**
- **修复:** 使用第 63 行捕获的 `combatState`。

**C2. CombatRecorder async void 异常可崩溃进程 + 线程竞态**
- `src/Chat/CombatRecorder.cs` ~137
- `OnCombatEnd()` 声明为 `async void`——未处理异常直接崩溃宿主进程。await 后线程池恢复时写入 `PreGeneratedDialogue`（第 169 行），主线程通过 `ConsumePreGeneratedDialogue` 读取（第 187 行）无任何内存屏障——正式数据竞态。
- **修复:** 改为 `async Task`，加内存屏障保护共享字段。

**C3. IroncladSolver Dark Orb Evoke 使用错误的伤害值**
- `src/Solver/IroncladSolver.cs` ~1127-1129
- `ApplyOrbEvokeEffect` 读取 `state.BaseDarkOrbDamage`（始终为 6），而非累积值。`TotalDarkOrbDamage` 被设置但从未读取。多 dark orb 时全部只造成 6 点。
- **修复:** 维护 `List<int> DarkOrbDamage` 追踪每个 orb 累积值。

**C4. IroncladSolver HP 成本边界 `<= 0` 阻止健康牺牲卡牌**
- `src/Solver/IroncladSolver.cs` ~598-600
- `if (entry.HpCost > 0 && state.Hp - entry.HpCost <= 0) continue;` 使用 `<= 0`。HP 恰好等于成本时阻止——可能是唯一生存机会（如 10 HP + Bloodletting 10 HP cost）。
- **修复:** 改为 `< 0`（只在致命时阻止）。

**C5. CombatHandler HP 成本边界 `<= 0` — 与 C4 相同模式的 handler 层副本**
- `src/Handlers/CombatHandler.cs` ~95-97
- 相同的 `<= 0` 错误，在 CombatHandlerAdapter 路径中重复出现。

### 死锁/崩溃

**C6. MP 死锁恢复中 RunManager.Instance 空引用崩溃**
- `src/AutoSlayNode.cs` ~1660, ~1757, ~4803
- 多处 `RunManager.Instance.ActionQueueSynchronizer` 无 null-conditional。死锁检测触发且 Instance 为 null 时 `NullReferenceException` 崩溃进程。
- **修复:** 完整 null-conditional 链: `RunManager.Instance?.ActionQueueSynchronizer?.RequestEnqueue(...)`

**C7. AppConfig 线程安全声明欺诈 — 所有公共属性零保护**
- `src/Core/AppConfig.cs` ~10-12, 71-97, 207
- 文档声称 "Thread-safe: all public state reads use a reader-writer lock." 无任何读写锁。所有可变属性裸 `{ get; set; }`。`TogglePause()` 读-改-写无锁。多线程场景可能撕裂读或状态丢失。
- **修复:** 移除虚假声明或包裹 `ReaderWriterLockSlim`。

**C8. AutoBattleController _Ready() 中 AppConfig.Instance 未检查初始化**
- `src/AutoBattle/AutoBattleController.cs` ~62
- 如果 `_Ready()` 在 `AppConfig.Initialize()` 前触发，`AppConfig.Instance` 抛出 `InvalidOperationException`——启动时不可恢复崩溃。
- **修复:** 加 `if (!AppConfig.IsInitialized) return;` guard。

**C9. ShopHandlerAdapter ContinueWith 静默异常 — 永久死锁**
- `src/AutoBattle/Handlers/ScreenHandlers.cs` ~112-121
- `HandleAsync(...).ContinueWith(_ => { _handling = false; })` 丢弃异常。如果异步操作抛异常，`_handling` 永久 `true`，bot 在商店永远等待。
- **修复:** 加 try/catch 或 `OnlyOnFaulted` continuation。

### Bot 误触发/永久卡住

**C10. StuckDetector MarkActivity() 从未被任何 handler 调用**
- `src/Core/StuckDetector.cs` ~159 + `src/AutoBattle/AutoBattleController.cs`
- `MarkActivity()` 必须在每次行动后调用。无任何 handler adapter 调用它。`_activityOccurredThisFrame` 永远 false。每次 45s+ 战斗触发 stuck-kill 事件。Bot 陷入不断自我恢复→重新 stuck 循环。
- **修复:** 在每个 handler 成功后调用 `_stuckDetector.MarkActivity()`。

**C11. StuckDetector `if (true)` 死代码 — 合作模式误触发**
- `src/Core/StuckDetector.cs` ~82
- 注释 "// single-player: always accumulate timer"，条件为 `if (true)`。合作多人模式中人类玩家仍在思考时触发 bot 接管。
- **修复:** 恢复 `if (!IsCoop())`。

### 数据损坏/静默无操作

**C12. RunContext DeckCardIds/RelicIds/PotionIds 含 null Entry — 下游 NRE**
- `src/Core/RunContext.cs` ~265, ~320, ~485
- `cardList.Select(c => c.Id.Entry)` 对 null `Entry` 无保护。`Where(id => id.StartsWith(...))` 等 LINQ 在 null 元素上抛 NRE。三个独立列表，同一风险模式。
- **修复:** `cardList.Select(c => c.Id.Entry ?? "").ToList()`。

**C13. IroncladSolver CloneState 手动字段复制 — 新字段静默损坏**
- `src/Solver/IroncladSolver.cs` ~1909-1997
- 40+ 字段手动赋值。`SearchState` 新增字段未更新 `CloneState` 时，DFS 子搜索共享父状态可变数据引用。
- **修复:** 实现深度克隆或反射验证完整性。

**C14. Solver.RunState 与 Core.RunContext 重复实现 — 修复分裂**
- `src/Solver/RunState.cs` (615行) + `src/Core/RunContext.cs` (543行)
- 两个独立副本含相同牌组组合/协同/Boss 逻辑。修复一个不传播到另一个。两者均在使用（`CardRewardDecider` 用 `RunState`，`DecisionEngine` 用 `RunContext`）。
- **修复:** 合并为单一共享类。

**C15. DecisionEngine 静态可变 PendingCardSelectContext — 跨屏幕泄露**
- `src/Solver/DecisionEngine.cs` ~7, 101, 113
- `PendingCardSelectContext` 是静态字段。"UPGRADE" 上下文可能误应用于 "EXHAUST" 屏幕，bot 消耗最好的卡而非最差的。
- **修复:** 添加快过期超时（1-2s）。

**C16. ChatEngine 单例被最后构造实例覆盖 — 多 bot API 路由错误**
- `src/Chat/ChatEngine.cs` ~102
- `_instance = this` 无条件赋值。多 bot 时 `GetInstance()` 只返回最后创建者。`CombatRecorder.OnCombatEnd()` 可能用错误 API key/角色调用。
- **修复:** 按 bot 名称字典维护实例映射。

---

## HIGH (33)

### Solver — 评分/搜索精度 (8)
**H1.** `CloneState` 后用 `RemoveAt(index)` 删牌 — 未来过滤/排序导致删除错误的卡
**H2.** 效果应用顺序不匹配游戏引擎 — 伤害前应用 Vulnerable Debuff 高估输出
**H3.** X-Cost 卡牌只返回 0 和 max 能量选项 — 部分卡牌最优不是全花光
**H4.** CardEffectReader 搜索中调用 `UpdateDynamicVarPreview` 每回合数千次变更实际卡牌对象
**H5.** CardEffectReader 反射失败静默回退到 0 值（默认 Attack=6/Skill=5）
**H6.** CardEffectReader _diagnosticLogged 静态 HashSet 跨 run 无限增长 — 缓慢内存泄漏
**H7.** EstimateIntentDamage 对 null CombatState 返回空列表 — 传入伤害估算 0
**H8.** CardClassifier 与 RunState 卡牌分类集合不一致 — `DrawCardSet` vs `_drawCardIds` 含不同卡牌

### Handlers — 无声失败/崩溃风险 (9)
**H9.** FlavorTextPatch 文本长度过滤 `<= 20` 错误拒绝 >20 字符的合法行
**H10.** RewardsHandler 依赖 `DebugOnlyGetState()` — 调试 API 可能在发布版移除
**H11.** ShopHandler 多 null 安全漏洞 — `room.Inventory`, `s.Entry`, `OnTryPurchaseWrapper` 均可能 null
**H12.** CardGridHandler / SimpleCardSelectHandler — `NCardGrid` 为 null 时静默 no-op，卡牌选择被丢弃
**H13.** ChooseARelicHandler 无过滤搜索所有可点击控制器 — 随机选择可能选到非遗物 UI
**H14.** CrystalSphereHandler 接口转换可能失败 — 非 IOverlayScreen 时无限循环
**H15.** CardRewardHandler / ChooseCardHandler 用 `EmitSignal` 而非 `ForceClick` — 绕过输入禁用检查
**H16.** RestSiteHandler 脆弱类型名字符串匹配 `Contains("Rest")` — 类重命名静默失败
**H17.** EventRoomHandler — `o.Option.IsLocked` / `b.Option.IsEnabled` 均未检查 null

### Core — 线程/检测/诊断 (6)
**H18.** AppConfig.Instance TOCTOU — getter 无锁，Initialize 有锁
**H19.** StuckDetector 50% 超时警告每帧触发 — 60fps 下每秒 60+ 次日志
**H20.** StuckDetector.WriteDiagnostics 传入配置超时值而非实际 stuck 时长 — 诊断输出恒为 45.0s
**H21.** ScreenDetector.IsFriendListScreen 每帧全场景树递归 — 大厅帧率下降
**H22.** RunContext.DetectActBoss 在 Boss 节点存在但 ID 不可读时返回 "" — 禁用所有 Boss 对策
**H23.** DecisionEngine.Refresh 静默吞异常 — 使用过期 HP/金币做出错误决策

### Chat/Llm — API/进程 (5)
**H24.** ChatLogger `FileShare.Read` 不支持并发写入 — 多实例 AppendAllText 抛 IOException
**H25.** LlmClient._allRuns 无界增长 — 长期运行内存泄漏 + SaveHistory 序列化巨型 JSON
**H26.** LlmClient 静态 HttpClient Timeout 全局修改 — 最后创建实例覆盖所有
**H27.** LlmClient 流式传输异常时历史记录损坏 — 残留半写 assistant 消息
**H28.** CombatRecorder await 后在任意线程写 PreGeneratedDialogue 无内存屏障

### AutoSlayNode (3)
**H29.** SignalRunComplete 在 MP 非战斗 stuck 时被调用 — 冻结在主菜单
**H30.** ParseChoice 回退逐字符扫描截断多位数 — "15" → '1'
**H31.** LLM 失败追踪永远对 null 比较 — 自动禁用永不触发

### Launcher (2)
**H32.** .NET 框架版本不匹配 (net8.0 vs net9.0)
**H33.** JSON 配置生成用裸 C# 字符串插值 — `"` 或 `\` 输入生成畸形 JSON

---

## MEDIUM (41)

### Solver (12)
**M1.** CardRewardDecider 整数除法截断 Act 1 伤害评分（`BaseDamage / cost` 应为浮点）
**M2.** StateStabilityDetector 反射失败时默认为 "stable" — 动画中过早决策
**M3.** MapDecider 点击验证 `_clickVerifyRow` 默认 0 导致首次调用可能误判
**M4.** MapDecider 邻接回退产生幻影边 — 连接不存在的地图路径
**M5.** CardEffectReader Strength/Dex 双重应用 — 游戏引擎值 + 手动回退加法
**M6.** CardEffectReader 升级倍率在 fallback 路径无条件应用 — 已附魔卡牌双重加成
**M7.** EventDecider 按 `eventId:optionIndex` 做 key 而非内容 — 随机排序事件误拦截
**M8.** ShopDecider 购买验证用 `>=` 而非 `>` — 可能重试重复购买
**M9.** RestDecider 未使用常量 `REST_THRESHOLD = 0.50` — 实际阈值来自 params.json
**M10.** EventDecider 缺少状态卡牌惩罚 — Wound/Burn 事件被中性评分
**M11.** CardDatabase 回退优先级每卡牌记录 Debug — 每回合数百条日志
**M12.** BossStrategy Boss 名称子字符串匹配 — 非 Boss 敌人可能误匹配

### Handlers (10)
**M13.** CombatHandler.Potions 可能为 null — `.ToList()` 抛 NRE
**M14.** CombatHandler.GetPile(player).Cards 可能为 null
**M15.** EventRoomHandler DialogueHitbox 类型不匹配 — 查为 NButton，发 NClickableControl 信号
**M16.** TreasureRoomHandler 节点名 "Chest" 大小写敏感 — `Chest` vs `chest`
**M17.** RestSiteHandler `b.Option.IsEnabled` 未检查 null
**M18.** RewardsHandler 强制移除计数器跨 run 累积 — `_forceRemoveCount` 从不重置
**M19.** ShopHandlerAdapter._handling 主线程与线程池 continuation 间无内存屏障
**M20.** PotionHelper "Self" 目标返回第一个活着的玩家生物 — 非严格 self
**M21.** CardRewardHandler / ChooseCardHandler 使用 EmitSignal 而非 ForceClick — 绕过输入检查
**M22.** EventRoomHandler `o.Option.IsLocked` 未检查 null

### Core (7)
**M23.** AppConfig.Instance TOCTOU — `Instance` 读时无锁
**M24.** ScreenDetector.CollectButtons 递归遍历时子节点变更风险
**M25.** ScreenDetector NOverlayStack Instance TOCTOU — 检查与解引用间场景可能卸载
**M26.** StuckDetector `>` vs `>=` 导致 1 帧触发延迟
**M27.** AutoSlayCardSelector 空 options 返回 default — 访问 .card 的调用方 NPE
**M28.** AutoSlayCardSelector ParseChoices 无法解析逗号分隔 "CHOOSE 1, 2, 3"
**M29.** HandlerBase.Peek() 在空 overlay stack 上未检查 ScreenCount

### Chat/Llm (4)
**M30.** LlmConfig 优先 .json 而非 .txt — 触发 mod scanner 错误
**M31.** LlmClient._memory 在 system prompt 中无大小上限 — 可能超上下文窗口
**M32.** GameStateExtractor SumPower 子字符串匹配 "Strength" 也匹配 "StrengthDrainPower"
**M33.** CharacterProfileManager 双重检查锁定不正确 — 两次锁获取间可能重复加载

### Launcher (3)
**M34.** MainForm.SaveApiKey 静默丢弃数组/对象属性 — 未来配置格式会被损坏
**M35.** MainForm.WaitForSignalAsync 死代码 — 从未被调用
**M36.** MainForm.FindGameRoot 中 Environment.Exit(1) — 不 Dispose 粗暴终止

### AutoBattle (2)
**M37.** AutoBattleController.ApplyHpBoost 空桩 — HP 提升功能未实现
**M38.** AutoBattleController._tKeyWasDown 两次 IsKeyPressed 调用间 TOCTOU

### AutoSlayNode (3)
**M39.** PendingCardSelect 卡牌播放失败后残留 — 后续叠层用错误上下文
**M40.** _hpBoosted 在死亡→新 run 不重置
**M41.** Cooldown 导致叠层处理被跳过 — 快速转换时错误 handler 被调用

---

## LOW (43)

### Solver (10)
**L1.** CharacterConfigs Ironclad "EXHUME" 重复条目（行 489-490）
**L2.** RelicDecider 遗物名称子字符串匹配脆弱
**L3.** TreasureDecider 返回 void 与其他 decider 不一致
**L4.** CardRewardDecider 零费卡牌除法 guard 正确但 else if 应为 else
**L5.** BattleLogger SolverPlan null-conditional 冗余但脆弱
**L6.** RunState.Refresh 首帧前使用默认零值 — HpRatio 为 0/0 NaN
**L7.** UiUtils.WaitForCondition 误导性命名 — 不循环等待，仅单次检查后返回
**L8.** BossPlayLogger.SanitizeBossName 连续下划线产生双空格文件名
**L9.** CardEffectReader 统一 15% 升级倍率不适用于机制型升级
**L10.** GameStateDetector 过期实现 — 缺少稳定性追踪和 MP 屏幕

### Handlers (6)
**L11.** PotionHelper rng 为 null 时始终选第一个敌人（目前从未触发）
**L12.** OverlayHandlers 所有 Handle 方法重复 IsInstanceValid + null 检查 — 应集中
**L13.** CombatHandler.BoostHpIfNeeded return 后死代码（第 39 行 return，40-47 不可达）
**L14.** CrystalSphereHandler `c.Entity.IsHidden` 未检查 null
**L15.** HandlerBase.DeckGrid 属性返回任意 Node — 可能路由到错误 handler
**L16.** RewardsHandler 依赖 DebugOnlyGetState — 发布版脆弱性（见 H10）

### Core (5)
**L17.** AppConfig.IsInitialized 有锁但 Instance 无锁 — 合同不一致
**L18.** ScreenDetector.CombineCheck 累积匹配计数可能过快达阈值
**L19.** ScreenDispatcher.OnDeactivated 同 handler 实例可能被 deactivate→reactivate 对
**L20.** AutoSlayCardSelector LLM 失败只记录 ex.Message 而非 StackTrace
**L21.** AutoSlayCardSelector.IsPendingLlm 无同步 — 多线程场景竞态

### Chat/Llm (5)
**L22.** GameStateExtractor sb.Length -= 3 边界情况
**L23.** FlavorTextPatch 每次对话气泡读文件（性能）
**L24.** GameStateSerializer.StripBBCode 大量正则 — 序列化性能热点
**L25.** GameStateSerializer.GetPile 空引用风险（见 H6）
**L26.** LlmClient error 路径移除错误历史条目 — 流式异常时移除错位

### AutoSlayNode (6)
**L27.** RandomOverlayFallback 遗物选择日志与点击索引不同
**L28.** _drawJustFinished 死代码 — 从未被读取
**L29.** _combatCardDelay 跨 action 时间漂移
**L30.** 非战斗 stuck 恢复后可重复触发（磁盘 I/O 浪费）
**L31.** async void TrySendAiChat 有 catch-all 但 async void 仍有进程崩溃风险
**L32.** 无效药水 Action 静默 no-op

### Launcher (5)
**L33.** CleanupOldFiles 竞态条件 — 可能干扰运行中实例
**L34.** Launcher 角色选择 SelectedIndex == -1 需确认 catch 保护
**L35.** WaitForSignalAsync 死代码
**L36.** FindGameRoot MessageBox.Show 在 Application.Run 前可能抛异常
**L37.** TokenSpire2.csproj 缺少 Compile Remove

### AutoBattle (4)
**L38.** AutoBattleController._character 存储但从未使用
**L39.** AutoBattleController if (true) 死条件
**L40.** RunSummaryLogger._lastRunStats 从未清理
**L41.** AutoBattleController.NewArchitectureEnabled 永不为 true — 整个控制器死代码

### Patches (2)
**L42.** SteamIdPatch __result ref 依赖 Harmony 未文档约定
**L43.** RunSummaryLogger.ModelId 比较可能为引用相等 — 若为 class 则永不等于 none

---

## 按子系统分类

| 子系统 | CRITICAL | HIGH | MEDIUM | LOW | 总计 |
|--------|----------|------|--------|-----|------|
| Solver/ | 3 | 8 | 12 | 10 | 33 |
| Handlers/ | 3 | 9 | 10 | 6 | 28 |
| Core/ | 4 | 6 | 7 | 5 | 22 |
| Chat/Llm/ | 2 | 5 | 4 | 5 | 16 |
| AutoSlayNode.cs | 1 | 3 | 3 | 6 | 13 |
| Launcher | 0 | 2 | 3 | 5 | 10 |
| AutoBattle/ | 2 | 0 | 2 | 4 | 8 |
| Patches/ | 0 | 0 | 0 | 2 | 2 |
| **总计** | **16** | **33** | **41** | **43** | **133** |

---

## 系统性架构问题

1. **反射滥用，缺乏类型安全:** CardEffectReader, CardModelInspector, ScreenDetector, MapDecider, IroncladSolver, GameStateExtractor, RelicDecider, RestSiteHandler, CrystalSphereHandler, ChooseARelicHandler 等大量依赖反射（类型名称、属性名探测）。任何小游戏更新都可能静默破坏检测和模拟。

2. **重复代码需双重维护:** RunState.cs/RunContext.cs 和 GameStateDetector.cs/ScreenDetector.cs 完全或部分重复。Handlers/ 和 Solver/ 间也有重复逻辑。CardClassifier 与 RunState 卡牌分类集合需合并。

3. **手动克隆无验证:** CloneState（40+ 手动字段赋值）是新 bug 的保证来源。

4. **跨 run 静态可变状态:** DecisionEngine, MapDecider, RewardsHandler, SimpleSelectDecider, CardEffectReader._diagnosticLogged, TreasureDecider, AutoSlayCardSelector.IsPendingLlm, ChatEngine._instance, LlmClient._http 使用静态字段无适当重置。

5. **硬编码数据表:** CardEffectReader.FallbackEstimate（350+ 行）、CardRewardDecider 10+ 静态 HashSet、BossStrategy 中 boss 策略——均需手动代码更改。

6. **AutoBattleController 死代码:** 全新架构（17 handler, StuckDetector, 暂停开关）完整实现但从未连接（`MainFile.AttachNodes` 只创建 AutoSlayNode, `NewArchitectureEnabled` 永不为 true）。~30% 的 AutoSlayNode.cs 也是 LLM 死代码（LLM 已永久禁用）。

7. **线程安全系统性缺失:** AppConfig 声称线程安全但零同步。StuckDetector.WriteDiagnostics 用配置值代替实际值。LlmClient 静态 HttpClient 全局共享 Timeout。CombatRecorder 无内存屏障。ShopHandlerAdapter 无内存屏障。

8. **async void 风险模式:** CombatRecorder.OnCombatEnd 和 AutoSlayNode.TrySendAiChat 使用 async void——异常不可恢复。

9. **无声失败模式:** DecisionEngine.Refresh 吞异常用过期数据。CardGridHandler/SimpleCardSelectHandler 在 NCardGrid 为 null 时静默 no-op。ShopHandlerAdapter 吞所有 continuation 异常。

---

## 修复优先级

### 🔴 立即修复（下个版本）
1. **C1:** CombatHandler 误用 Card.CombatState（bot 所有目标卡牌失效）★ 最高
2. **C2:** CombatRecorder async void + 线程竞态（崩溃风险）
3. **C6:** MP 死锁恢复 NRE 崩溃
4. **C9:** ShopHandlerAdapter 静默永久死锁
5. **C10:** StuckDetector MarkActivity 从未调用（每战误报）
6. **C3:** Dark Orb evoke 错误伤害
7. **C5:** CombatHandler HP 成本 `<=0`
8. **H1:** CloneState RemoveAt 索引脆弱
9. **H2:** 效果应用顺序不匹配

### 🟡 尽快修复
10. **C4:** IroncladSolver HP 成本边界
11. **C11:** StuckDetector if(true) 合作模式
12. **C12:** RunContext null Entry → NRE
13. **C7:** AppConfig 线程安全欺诈
14. **C8:** AutoBattleController unchecked init
15. **H9-H17:** Handler null 安全漏洞
16. **H19-H21:** StuckDetector 诊断修复
17. **H24-H28:** Chat/Llm 修复
18. **H30-H31:** AutoSlayNode ParseChoice + LLM 追踪

### 🟢 下个迭代
19. **C13-C15:** 重复代码 / CloneState / ChatEngine 单例
20. **H18:** AppConfig TOCTOU
21. **H32-H33:** Launcher 修复
22. **M1-M41:** 所有 MEDIUM 问题
23. **L1-L43:** 所有 LOW 问题

---

## 已验证正确的部分

以下区域在审计中被确认正确：
- `EndTurnViaUiOrApi`: 正确区分 MP (ActionQueueSynchronizer) 和 SP (PlayerCmd.EndTurn)
- `RandomOverlayFallback`: 覆盖所有叠层类型，异常安全
- `SteamIdPatch` + `ENetClientNetIdPatch`: 正确 FNV-1a hash + 适当 guard
- `FlavorTextPatch`: 正确使用 ThreadStatic OverrideText
- 所有 6 个 NON_COMBAT_DECISION_TIMEOUT 回退点: 正确重置并执行随机回退
- Screen tracking: `_sameScreenDuration` / `_sameScreenTickCount` 正确递增
- `AutoSlayHelpers.FindAllRecursive`: 正确 IsInstanceValid guard
- `AutoSlayPatch`: 最小化 Harmony patch，错误处理正确
- `IScreenHandler` 接口: 良好设计，合理默认实现
- `MainMenuHandler` / `GameOverHandler` / `MapHandler`: 干净、正确
- `PromptStrings.cs` / `PatchStubs.cs`: 干净的本地化/文档代码
- `AiChatConfig.cs`: 正确的线程安全单例
- `GameStateExtractor.cs`: 全程 try/catch guard
- 所有 `using` 声明: 正确（2026-07-12 构建修复已验证）
