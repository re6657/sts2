# SteamFix64 联机补丁实现原理

## 概述

`E:\杀戮尖塔2 多人卡牌\杀戮尖塔2\联机补丁\联机补丁\` 中的联机补丁使用 **DLL 代理注入 + Steam API Hook** 的方式，在不修改游戏代码的情况下，将《杀戮尖塔2》的 Steam 多人联机功能重定向到局域网（LAN）。

## 文件清单

| 文件 | 大小 | 说明 |
|------|------|------|
| `winmm.dll` | 22 KB | Windows 多媒体 DLL 代理（DLL 劫持入口） |
| `SteamFix64.dll` | 1.4 MB | 核心 Steam API Hook 引擎 |
| `data_sts2_windows_x86_64/steam_api64.dll` | 300 KB | 替换的 Steam API 库 |
| `SteamFix.ini` | 315 B | 配置文件 |
| `使用说明.txt` | 428 B | 使用说明 |

## 实现原理

### 1. DLL 劫持（DLL Hijacking）

```
SlayTheSpire2.exe 启动
    │
    ├─ 加载 winmm.dll（Windows Multimedia API）
    │   └─ 游戏原本应该加载 C:\Windows\System32\winmm.dll
    │      但由于游戏目录下存在同名 winmm.dll，
    │      Windows 的 DLL 搜索顺序会优先加载游戏目录的版本
    │
    └─ winmm.dll（代理）
        ├─ 加载原始系统的 winmm.dll（转发所有原始函数调用）
        └─ 加载 SteamFix64.dll（注入 Hook 逻辑）
```

**关键机制**：Windows 默认的 DLL 搜索顺序是：
1. 应用程序所在目录
2. System32 目录
3. Windows 目录
4. 当前工作目录
5. PATH 环境变量

由于 `winmm.dll` 被放置在游戏目录下，它会在系统版本之前被加载，从而实现代码注入。

### 2. Steam API Hook

```
SteamFix64.dll
    │
    ├─ Hook Steam Networking API：
    │   ├─ ISteamNetworkingSockets  → 替换为 UDP 局域网通信
    │   ├─ ISteamFriends           → 局域网大厅发现
    │   ├─ ISteamMatchmaking       → 局域网大厅创建/加入
    │   └─ ISteamUser              → 模拟 Steam 用户身份
    │
    ├─ 配置文件 (SteamFix.ini)：
    │   ├─ RealAppId=2868840       → 杀戮尖塔2 的真实 Steam AppID
    │   ├─ FakeAppId=480           → Spacewar（Steamworks 示例游戏）
    │   │                             用于绕过正版验证
    │   └─ [Interfaces]            → 指定哪些 Steam 接口需要 Hook
    │
    └─ data_sts2_windows_x86_64/steam_api64.dll：
        └─ 替换游戏自带的 steam_api64.dll
           将游戏对 Steam API 的调用重定向到 SteamFix64
```

**Hook 流程**：

```
游戏代码调用 Steam API
    │
    ├─ steam_api64.dll（替换版）
    │   └─ 所有 Steam API 调用被转发到 SteamFix64.dll
    │
    ├─ SteamFix64.dll
    │   ├─ 网络相关 API → UDP 局域网包
    │   ├─ 大厅相关 API → 内存中的虚拟大厅
    │   ├─ 好友相关 API → 局域网节点发现
    │   └─ 其他 API → 转发给原始 steam_api64.dll / 返回模拟数据
    │
    └─ 结果：两个游戏实例可以通过 127.0.0.1（本机）或局域网 IP 互相发现和连接
```

### 3. 为什么不需要修改游戏代码？

| 层级 | 传统 Mod 方式 | SteamFix64 方式 |
|------|--------------|-----------------|
| 注入方式 | C# Harmony Patch（运行时 IL 重写） | 原生 DLL 劫持 |
| Hook 目标 | 游戏 C# 方法 | Steam C++ API |
| 修改点 | 需要找到并 Hook 每个相关方法 | 只需要在 API 层拦截一次 |
| 网络层 | 需要自己实现传输（如 TCP Broker） | 利用 Steamworks 的 P2P 网络模型，只替换底层传输 |
| 兼容性 | 游戏更新可能破坏 Hook 点 | 只要 Steam API 不变就能工作 |

### 4. 与 TokenSpire2 的集成

TokenSpire2 的 `AutoSlayPatch.cs` 中有三种模式：

```
DetectSteamFix64Mode()
    │
    ├─ CoopMode=false → 单人模式（纯自动战斗）
    │
    ├─ CoopMode=true + 无 broker 标记文件
    │   → SteamFix64 模式
    │   → 只安装双角色隔离补丁（不碰网络层）
    │   → 网络通信由 SteamFix64.dll 处理
    │
    └─ CoopMode=true + 有 broker 标记文件
        → Broker 模式
        → 安装全套网络补丁（Hook C# 网络方法 → TCP Broker）
        → 网络通信由自定义 TCP Broker Server 处理
```

**SteamFix64 模式下 TokenSpire2 的职责**：
1. 自动战斗 AI（IroncladSolver 等）
2. UI 自动化（点击按钮、选择角色）
3. 双角色隔离（不让两个玩家的 UI/输入互相干扰）
4. 自动 Ready / 自动 Embark

**SteamFix64 模式下的网络通信**（完全由 SteamFix64.dll 处理）：
- 大厅创建/加入
- 角色选择同步
- 战斗回合同步
- 卡牌选择/出牌同步
- 遗物/药水同步

### 5. 数据流（完整联机流程）

```
┌─────────────────────────────┐    ┌─────────────────────────────┐
│  HOST 实例                    │    │  CLIENT 实例                  │
│  TOKENSPIRE2_ROLE=host       │    │  TOKENSPIRE2_ROLE=client      │
├─────────────────────────────┤    ├─────────────────────────────┤
│                             │    │                             │
│  SlayTheSpire2.exe          │    │  SlayTheSpire2.exe          │
│  ├─ winmm.dll (代理)         │    │  ├─ winmm.dll (代理)         │
│  ├─ SteamFix64.dll (Hook)   │    │  ├─ SteamFix64.dll (Hook)   │
│  ├─ steam_api64.dll (替换)   │    │  ├─ steam_api64.dll (替换)   │
│  │                          │    │  │                          │
│  │ 游戏代码                   │    │  │ 游戏代码                   │
│  │ ├─ CreateLobby()  ───────┼────┼──┤ Steam API                 │
│  │ ├─ 等待客户端加入           │    │  │ ├─ FindLobby() ──────────┤
│  │ ├─ 开始游戏  ──────────────┼────┼──┤  ├─ JoinLobby() ─────────┤
│  │ └─ 游戏同步               │    │  │ └─ 游戏同步               │
│  │                          │    │  │                          │
│  │ TokenSpire2 Mod          │    │  │ TokenSpire2 Mod          │
│  │ ├─ 人类玩家操作             │    │  │ ├─ 自动战斗AI              │
│  │ ├─ T键切换自动战斗          │    │  │ ├─ 自动点击UI              │
│  │ └─ 双角色隔离              │    │  │ └─ 双角色隔离              │
│  │                          │    │  │                          │
│  └──────────────────────────┘    │  └──────────────────────────┘
│             │                    │             │
└─────────────┼────────────────────┴─────────────┼────────────────
              │          UDP / LAN                │
              └───────────────────────────────────┘
```

## TokenSpire2 当前采用的方案

TokenSpire2 **当前使用 SteamFix64 模式**：

1. **安装联机补丁文件**（一次性）：
   - 将 `winmm.dll`, `SteamFix64.dll`, `SteamFix.ini` 复制到游戏根目录
   - 将 `steam_api64.dll` 复制到 `data_sts2_windows_x86_64/`（替换原文件，先备份）

2. **配置 TokenSpire2**：设置 `coop_config.json` 中 `"CoopMode": true`，**不创建** broker 标记文件

3. **启动游戏**：两个实例通过 SteamFix64.dll 在局域网内互相发现和连接

## 之前的 Broker 模式（已弃用）

TokenSpire2 之前尝试过自定义 TCP Broker 模式：
- 自己写了 TCP Broker Server (`BrokerServer/Program.cs`)
- 通过 C# Harmony 补丁 Hook 了 `InitializeMultiplayerAsHost/Client`、`JoinFlow.Begin` 等方法
- 将游戏内的 Steam 网络调用重定向到自定义 TCP 协议
- **遇到的问题**：C# Hook 层面无法完全模拟 Steam 网络的所有行为，导致 "Broker did not accept registration" 等错误，游戏实例之间不稳定

## 总结

SteamFix64 方式的关键优势在于它在 **更底层**（原生 DLL 层）做 Hook，不需要理解游戏的 C# 网络代码细节。它只需要将 Steam C API 的网络部分替换为 LAN 实现，游戏代码完全不需要修改。这也是为什么它是一个更稳定、更通用的方案。
