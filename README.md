# CATUI_local_load

CATUI 本地配置强制加载模块。让客户端在多人联机时优先使用**自己本地的 XUi / qualityinfo 配置**，避免因服务器端的 CATUI 版本不一致或服务器 XML 缺失/损坏，导致玩家 UI 报错、空白或错位。

> 文档版本：1.0.0 · 适用游戏：《七日杀》7 Days to Die 3.1 · 目标框架：.NET Framework 4.8（C# 9.0）

---

## 1. 模块概述

### 1.1 目的

《七日杀》多人联机时，服务器会把一批 `Data/Config` 下的 XML 配置（`blocks`、`items`、`XUi_InGame/windows` 等 49 项）在玩家进入世界前**下发给客户端**，客户端会以服务器下发的版本为准来加载 UI 与游戏数据。

当出现以下情况时，客户端 UI 会被破坏：

- 服务器**没有安装 CATUI**，或安装了**不同版本**的 CATUI —— 服务器下发的 XUi 配置与客户端本地 CATUI 不匹配；
- 服务器自身的 XML 配置**缺失 / 不完整** —— 服务器可能下发**空文件（0 字节）**甚至**不下发**，导致客户端加载到空白 UI；
- 服务器把**未压缩的原始空数据**当作配置发给客户端。

`CATUI_local_load` 的作用就是在这些场景下，**强制客户端回退到本地文件加载 XUi 与 qualityinfo 配置**，从而保证界面始终完整、与本地 CATUI 一致，不再被服务器的坏配置拖垮。

### 1.2 核心功能（一句话）

> 拦截服务器下发的 XUi / qualityinfo 配置；当配置缺失或为空时，自动改为从客户端本地加载，并输出诊断日志。

### 1.3 与主模组的关系

本模块是主模组 **CATUI**（`ZZZ_CATUI`）的配套组件。主模组负责全部 UI 定制，本模块只负责"联机时保证本地配置被采用"，二者独立安装、可独立加载。部署目录名以 `ZZZ_` 开头，确保在模组按字母序加载时排在最后，Harmony 补丁在其他模组之后生效。

---

## 2. 技术架构

### 2.1 模块组成

```
CATUI_local_load/
├── Source/                          # 工程源码（编译目录）
│   ├── CATUI_local_load.csproj     # .NET Framework 4.8 工程
│   ├── _Init.cs                    # 模组入口 IModApi → Harmony.PatchAll
│   ├── LocalLoadPatch.cs           # 全部 Harmony 补丁逻辑（核心）
│   ├── 0_TFP_Harmony/              # 编译期引用的 0Harmony.dll
│   └── 7DaysToDie_Data_DLL/        # 编译期引用的游戏程序集（Assembly-CSharp 等）
├── ZZZ_CATUI_local_load/           # 部署产物（放入游戏 Mods 目录）
│   ├── CATUI_local_load.dll
│   └── ModInfo.xml
└── README.md
```

### 2.2 加载与补丁装配流程

```
游戏启动
  └─ 模组加载器遍历 Mods/*/ModInfo.xml，识别到 ZZZ_CATUI_local_load
       └─ 调用 ModStartup.InitMod(Mod)
            └─ Harmony 实例 = new Harmony(程序集名)
                 └─ harmony.PatchAll(Assembly.GetExecutingAssembly())
                      ├─ 补丁A：WorldStaticData.XmlLoadInfo 构造函数 Postfix
                      ├─ 补丁B：WorldStaticData.ReceivedConfigFile Postfix
                      └─ 补丁C：WorldStaticData.AllConfigsReceivedAndLoaded Postfix
```

### 2.3 组件关系

本模块共 **3 个 Harmony 补丁**，全部作用于游戏静态数据加载类 `WorldStaticData`，相互协作完成"检测 → 回退 → 汇总日志"的完整闭环：

| 补丁 | 挂载目标 | 职责 |
|------|----------|------|
| `LocalLoadPatch_WorldStaticData_XmlLoadInfo` | `WorldStaticData.XmlLoadInfo` 构造函数（Postfix） | 把 XUi / qualityinfo 条目强制标记为"从客户端本地加载"（`LoadClientFile = true`） |
| `LocalLoadPatch_WorldStaticData` → `ReceivedConfigFile` | `WorldStaticData.ReceivedConfigFile`（Postfix） | 收到服务器配置时检测；空/缺失则回退本地并记录 |
| `LocalLoadPatch_WorldStaticData` → `AllConfigsReceivedAndLoaded` | `WorldStaticData.AllConfigsReceivedAndLoaded`（Postfix） | 所有配置接收完毕后输出一份"回退本地"汇总清单 |

### 2.4 依赖项

| 依赖 | 说明 |
|------|------|
| `0_TFP_Harmony` | 游戏 Mods 目录下的前置模组（提供 Harmony 运行时）。**不可删除**，否则模组无法加载 |
| `Assembly-CSharp.dll` | 游戏主程序集，补丁目标所在 |
| `.NET Framework 4.8` | 编译目标框架，与 Unity 的 Mono 运行时兼容 |

---

## 3. 核心功能

### 3.1 功能一：联机时强制采用本地 XUi / qualityinfo 配置（补丁A）

**机制**：对 `WorldStaticData.xmlsToLoad` 中名字包含 `XUi` 或 `qualityinfo` 的条目，将其 `LoadClientFile` 字段强制置为 `true`。

**效果**：在原版 `SendXmlsToClient`（`WorldStaticData.cs:801`）中，`LoadClientFile == true` 的配置会以**空数据（null）**形式下发给客户端，客户端收到后会走"从本地加载"分支，从而使用自己本地的 XUi / qualityinfo 文件（即本地 CATUI 的界面），**彻底绕开服务器版本的 UI 配置**。

```csharp
// LocalLoadPatch.cs
public static void Postfix(string _xmlName, bool _loadAtStartup, ref bool _sendToClients,
    ..., ref bool ___LoadClientFile, ...)
{
    if (_xmlName.Contains("XUi") || _xmlName.Contains("qualityinfo"))
    {
        ___LoadClientFile = true;   // 强制客户端使用本地配置
    }
}
```

> 说明：该补丁在服务端运行时，效果是"让服务器告诉所有客户端从本地加载 UI"；在客户端运行时，为本地主机场景兜底。因此**服务器与客户端都装本模组**效果最佳。

### 3.2 功能二：服务器配置缺失 / 空文件时自动回退（补丁B）

**机制**：Postfix 挂在 `WorldStaticData.ReceivedConfigFile(string _name, byte[] _data)` 上，当收到名字含 `XUi` / `qualityinfo` 的配置时：

| 服务器下发情况 | 处理 |
|----------------|------|
| `_data == null`（未下发） | `Log.Warning` + 回退本地加载 |
| `_data.Length == 0`（空文件，服务器 XML 缺失/不完整） | `Log.Warning` + 回退本地加载 |
| `_data` 非空（服务器正常下发） | 仅输出绿色调试日志 `CATUI local load xml name: <name>` |

回退本地时调用内部方法 `SetLoadLocal(name)`：把对应条目的 `WasReceivedFromServer` 置为 `EClientFileState.LoadLocal` 并清空 `CompressedXmlData`，使原版 `handleReceivedConfigs` 走 `loadSingleXml` 本地加载分支。

```csharp
if (_data != null && _data.Length == 0)
{
    Log.Warning("{0} Server sent an EMPTY config for '{1}'. This usually means the server's XML config is missing or incomplete. Falling back to local file.", TAG, _name);
    TrackLocalLoad(_name);
    SetLoadLocal(_name);
    return;
}
```

### 3.3 功能三：回退汇总日志（补丁C）

所有配置接收完成后（`AllConfigsReceivedAndLoaded` 返回 `true` 且存在回退记录且未输出过），输出一行汇总：

```
[CATUI] XML fallback summary: the following server configs were missing/incomplete and were loaded from local files instead: XUi_InGame/windows, qualityinfo, ...
```

便于在日志中快速定位"哪些配置被服务器搞坏了"。

### 3.4 使用示例（日志视角）

正常联机、服务器也装了本模组时，进入世界后日志中**不会**出现回退警告，只会在收到 XUi 配置时出现绿色 `CATUI local load xml name: XUi_InGame/windows`（说明已采用本地 UI）。

服务器缺配置时，日志中出现：

```
[CATUI] Server sent an EMPTY config for 'XUi_InGame/windows'. ... Falling back to local file.
[CATUI] XML fallback summary: the following server configs were missing/incomplete and were loaded from local files instead: XUi_InGame/windows
```

---

## 4. API 参考

### 4.1 对外暴露的公共成员

| 成员 | 类型 | 说明 |
|------|------|------|
| `LocalLoadPatch.LocallyLoadedConfigs` | `public static readonly List<string>` | 记录本次会话中因服务器缺失/损坏而回退本地加载的配置文件名（不含重复）。其他模组可读取，用于诊断或二次开发 |

> 除上述字段外，模块**不对外提供**可调用函数。所有能力通过 Harmony 补丁以"拦截游戏原方法"的方式实现。

### 4.2 补丁目标与签名（内部实现参考）

**补丁A — `WorldStaticData.XmlLoadInfo` 构造函数**

```
原型：XmlLoadInfo(string _xmlName, bool _loadAtStartup, bool _sendToClients,
                 Func<XmlFile,IEnumerator> _loadMethod, Action _cleanupMethod,
                 Func<IEnumerator> _executeAfterLoad = null,
                 bool _allowReloadDuringGame = false,
                 Action<XmlFile> _reloadDuringGameMethod = null,
                 bool _ignoreMissingFile = false, string _loadStepLocalizationKey = null)
目标定位：AccessTools.Constructor(AccessTools.Inner(typeof(WorldStaticData),"XmlLoadInfo"), [10个参数类型], false)
行为：Postfix 中若 _xmlName 含 "XUi" 或 "qualityinfo" → 写 ___LoadClientFile = true
参数：通过 `ref bool ___LoadClientFile` 引用实例字段 LoadClientFile
```

**补丁B — `WorldStaticData.ReceivedConfigFile`**

```
原型：public static void ReceivedConfigFile(string _name, byte[] _data)
返回：void（Postfix 不改变返回值）
参数：
  _name  string  配置名（如 "XUi_InGame/windows"）
  _data  byte[]  服务器下发的压缩数据；null = 未下发；长度0 = 空文件
行为：仅当 _name 含 "XUi" 或 "qualityinfo" 时介入（见 3.2）
```

**补丁C — `WorldStaticData.AllConfigsReceivedAndLoaded`**

```
原型：public static bool AllConfigsReceivedAndLoaded()
参数：ref bool __result（原方法返回值）
行为：__result == true 且 LocallyLoadedConfigs 非空且未输出过 → 输出汇总并置 summaryLogged = true
```

### 4.3 错误处理

- 补丁代码本身不主动抛异常，依赖空值判断（`_data != null`、`?.`）避免常见空引用；
- 原版 `handleReceivedConfigs` 对本地加载失败、解析失败、后置步骤失败均有异常回调与 `Log.Error` 输出，本模块复用原版路径，不额外吞错；
- `ReceivedConfigFile` 中原版对未知配置名会输出 `XML loader: Received unknown config from server`，本模块不改变该行为；
- **已知健壮性提示**：补丁B 直接对 `_name` 调用 `Contains`，若 `_name` 为 `null` 会抛 `NullReferenceException`。原版调用方始终传入合法配置名，正常不会触发；如需绝对稳健可在 `_name` 上增加空值守卫。

---

## 5. 集成指南

### 5.1 环境前提

- 已安装 `0_TFP_Harmony` 前置模组（游戏 Mods 目录下，勿删除）；
- 客户端需**关闭 Easy Anti-Cheat**；
- 主模组 `ZZZ_CATUI` 已安装（本模块作用于 CATUI 的本地 XUi 配置，无 CATUI 时意义有限）。

### 5.2 方式一：直接部署（推荐）

1. 将 `ZZZ_CATUI_local_load/` 整个文件夹（内含 `CATUI_local_load.dll`、`ModInfo.xml`）复制到游戏 `Mods/` 目录：
   ```
   <游戏根目录>/Mods/ZZZ_CATUI_local_load/
   ```
2. 启动游戏进入存档，查看日志确认加载成功（见 7.2）。

### 5.3 方式二：从源码编译

```powershell
# 在 Source 目录下执行
dotnet build "H:\git\7D2D-CATUI\CATUI_local_load\Source\CATUI_local_load.csproj"
```

- 产物路径：`Source\bin\Debug\net48\CATUI_local_load.dll`；
- 将生成的 DLL 手动复制到 `ZZZ_CATUI_local_load\` 覆盖同名文件（**此工程没有 PostBuild 自动复制**）；
- 编译依赖 `0_TFP_Harmony\0Harmony.dll` 与 `7DaysToDie_Data_DLL\Assembly-CSharp.dll` 等程序集，改动游戏版本后需同步更新这些引用。

### 5.4 不同环境的部署策略

| 环境 | 服务器是否安装 | 客户端是否安装 | 效果 |
|------|---------------|----------------|------|
| 专用服务器 + 多个客户端 | **推荐安装** | 安装（至少目标玩家安装） | 服务器下发"本地加载"指令，所有客户端 UI 用各自本地配置，互不干扰 |
| 专用服务器 + 客户端（只装客户端） | 不装 | 装 | 仅当服务器下发空/缺失配置时回退；服务器正常下发时仍用服务器版本 |
| 单机 | 无需 | 可装可不装 | 单机默认本地加载，模组不干预，无副作用 |
| LAN 联机（房主开服） | 房主装 | 参与者装 | 房主以"本地加载"方式同步 UI 配置给参与者 |

> 核心结论：要**完全**避免服务器版本不一致，服务器和客户端都装本模组。

### 5.5 与其他 CATUI 系列模组的搭配

`CATUI_backpack_91slot`、`CATUI_toolbelt_more_slot` 等可选模组同样以 `ZZZ_` 前缀部署。本模块只处理 `XUi` 与 `qualityinfo` 配置，不影响其他模组的背包/工具栏布局数据，可正常共存。

---

## 6. 配置选项

| 选项 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| 生效配置范围 | 硬编码 | `XUi*` / `qualityinfo` | 仅名字含 `XUi` 或 `qualityinfo` 的配置受本模块影响，其余 47 项配置保持原版行为 |
| 日志开关 | 硬编码 | 开启 | 空/缺失配置时输出 `Log.Warning`；正常下发时输出 `Debug.Log`（绿色） |
| 汇总日志 | 硬编码 | 每次会话一次 | `AllConfigsReceivedAndLoaded` 后仅输出一次，避免刷屏 |
| 回退方式 | 硬编码 | 本地文件 | 回退即 `EClientFileState.LoadLocal`，走原版本地加载管线 |

> 本模块**没有独立的配置文件**（Mods 目录下只有 DLL 与 ModInfo.xml），以上均为源码级行为。如需调整生效范围，需修改 `LocalLoadPatch.cs` 中的 `Contains("XUi") / Contains("qualityinfo")` 判断后重新编译。

---

## 7. 性能考量

### 7.1 开销分析

- **补丁A**：仅在游戏启动、构建 `xmlsToLoad` 静态数组（共 49 项）时执行一次，常数级开销；
- **补丁B**：仅在进入世界、服务器下发配置时对每个配置执行一次（约 49 次），非每帧逻辑；
- **补丁C**：仅在所有配置接收完成的瞬间执行一次。

三个补丁**均不在 Update / 渲染热路径上**，对帧率、内存、GC 的影响可忽略。

### 7.2 内存与资源

- `LocallyLoadedConfigs` 为小型 `List<string>`，最多 49 项，常驻内存可忽略；
- 回退本地时清空 `CompressedXmlData`，避免保留服务器下发但未使用的数据；
- 无额外贴图、图集、音频等资源加载。

### 7.3 优化点

- `SetLoadLocal` 使用线性遍历 `xmlsToLoad`（最多 49 项），无需优化；
- 日志仅在异常/汇总时输出，不会造成日志刷屏。

---

## 8. 已知限制与故障排查

### 8.1 已知限制

| # | 限制 | 说明 |
|---|------|------|
| 1 | 只覆盖 XUi 与 qualityinfo | 其余 47 项配置（blocks、items 等）仍以服务器版本为准。若服务器缺少这些配置，客户端仍可能报错（非本模块职责） |
| 2 | 需要服务器端配合 | 仅客户端安装时，若服务器正常下发非空配置，客户端仍采用服务器版本，无法强制本地 |
| 3 | 依赖 `0_TFP_Harmony` | 缺失该前置模组时，模组静默不生效（无报错），表现为问题依旧存在 |
| 4 | EAC 冲突 | 需关闭 Easy Anti-Cheat 才能加载 |
| 5 | 本地文件必须存在 | 回退本地后若客户端本地对应 XML 缺失，原版会输出 `XML loader: XML is missing` |
| 6 | 空值健壮性 | 补丁B 未对 `_name` 做 null 守卫，极端情况下可能 NRE（正常流程不会触发） |

### 8.2 故障排查

**Q1：模组到底加载了没有？**
查看日志 `output_log_client__*.txt`（路径 `%AppData%/7DaysToDie/Logs` 或 `<游戏目录>/Logs`）。加载成功后应能看到模组入口（Harmony 打补丁）相关输出；若日志完全无本模组痕迹，先检查 `Mods/ZZZ_CATUI_local_load/` 结构是否完整、`0_TFP_Harmony` 是否还在。

**Q2：进入联机服务器后 UI 还是空白 / 报 `Can not parse input` / XUi 绑定错误？**
- 若日志中出现 `[CATUI] Server sent an EMPTY config for 'XUi_InGame/windows'...` 但随后界面仍异常，说明本地回退成功但本地 CATUI 版本与服务器其他数据仍有冲突，请确认服务器与客户端 CATUI 版本一致；
- 若没有任何 `[CATUI]` 日志，说明服务器正常下发了自己的 XUi 配置且客户端采用了它——请在**服务器也安装**本模组。

**Q3：怎么知道哪些配置被回退了？**
搜索日志中的 `[CATUI] XML fallback summary:` 一行，会列出全部被回退的配置名；或直接读取 `LocalLoadPatch.LocallyLoadedConfigs`。

**Q4：单机进游戏没有变化？**
正常。单机默认从本地加载配置，本模组只作用于"服务器下发配置"环节。

**Q5：会不会影响存档或服务器数据？**
不会。本模块只改动客户端的**配置加载来源**（内存中的 `XmlLoadInfo` 状态），不写存档、不改服务器文件。

---

*文档由代码逆向分析生成，基于 `LocalLoadPatch.cs`、`_Init.cs` 与 `WorldStaticData.cs`（原版反编译）行为描述，请以实际版本为准。*
