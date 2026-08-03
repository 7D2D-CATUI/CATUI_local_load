# CATUI_local_load

CATUI 联机配置叠加模块。联机进入服务器时，客户端**保留服务器下发的 XUi / qualityinfo 配置**（服务器端其他 UI 定制不丢失），同时把**本地 CATUI 系列模组的补丁**叠加上去，保证本地 CATUI 界面完整；当服务器下发空/缺失配置时，自动回退到本地文件加载。

> 文档版本：3.0.0 · 适用游戏：《七日杀》7 Days to Die 3.1 · 目标框架：.NET Framework 4.8（C# 9.0） · 文档对应代码：`LocalLoadPatch.cs`（叠加/回退方案）

---

## 1. 模块概述

### 1.1 目的

《七日杀》多人联机时，服务器会在玩家进入世界前把一批 `Data/Config` 下的 XML 配置（`blocks`、`items`、`XUi_InGame/windows` 等共 49 项）下发给客户端，客户端以服务器下发的版本为准加载 UI 与游戏数据。

联机时可能出现两类问题：

1. **服务器版本不一致 / 没有 CATUI**：服务器下发的 XUi 配置与客户端本地 CATUI 不匹配，客户端 UI 布局、绑定错乱甚至报错；
2. **服务器配置缺失 / 损坏**：服务器 XML 缺失或不完整时，可能下发**空文件（0 字节）**甚至**不下发**，客户端加载到空白 UI，触发大量 `Can not parse input`、绑定求值报错。

`CATUI_local_load` 的处理策略是**"叠加而非全量替换"**：

- 服务器正常下发 → **保留服务器内容**，把本地 CATUI 系列模组的补丁叠加上去，同时修复服务器配置里常见的双重转义实体问题；
- 服务器下发空/缺失 → **回退本地文件**加载，保证界面完整。

### 1.2 核心功能（一句话）

> 联机时以"服务器配置为基座 + 本地 CATUI 补丁叠加"的方式加载 XUi / qualityinfo；配置缺失或为空时回退本地；全程输出诊断日志。

### 1.3 与主模组的关系

本模块是主模组 **CATUI**（`ZZZ_CATUI`）的配套组件。主模组负责全部 UI 定制，本模块只负责"联机时让本地 CATUI 补丁生效"。二者独立安装、可独立加载；部署目录名以 `ZZZ_` 开头，确保模组按字母序加载时排在最后，Harmony 补丁在其他模组之后生效。

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
                      ├─ 补丁1：WorldStaticData.ReceivedConfigFile      Postfix（空/缺失→回退，非空→标记叠加）
                      ├─ 补丁2：WorldStaticData.AllConfigsReceivedAndLoaded Postfix（回退汇总日志）
                      └─ 补丁3：XmlPatcher.ApplyConditionalXmlBlocks   Prefix（实体还原 + 本地 CATUI 补丁叠加）
```

### 2.3 组件关系

本模块共 **3 个 Harmony 补丁**，作用于游戏配置加载类 `WorldStaticData` 与 `XmlPatcher`，协作完成"检测 → 回退/叠加 → 汇总日志"的闭环：

| 补丁 | 挂载目标 | 类型 | 职责 |
|------|----------|------|------|
| 补丁1 | `WorldStaticData.ReceivedConfigFile` | Postfix | 拦截服务器下发的 XUi/qualityinfo：空(0字节)/缺失(null)→回退本地并记录；非空→加入待叠加集合 |
| 补丁2 | `WorldStaticData.AllConfigsReceivedAndLoaded` | Postfix | 所有配置接收完成后，输出一份"回退本地"汇总清单（每会话一次） |
| 补丁3 | `XmlPatcher.ApplyConditionalXmlBlocks` | Prefix | 对收到的 XUi/qualityinfo 文档：先还原双重转义实体，再把本地 CATUI 系列模组补丁叠加上去 |

### 2.4 关键代码位置（原版反编译参考）

- `WorldStaticData.ReceivedConfigFile(string _name, byte[] _data)`：客户端接收单个服务器配置的入口；
- `WorldStaticData.handleReceivedConfigs()`：协程，按序加载收到的配置；`WasReceivedFromServer == EClientFileState.LoadLocal` 时走本地文件加载分支；
- `WorldStaticData.AllConfigsReceivedAndLoaded()`：全部配置接收完成标志；
- `XmlPatcher.ApplyConditionalXmlBlocks(string _xmlName, XmlFile _xmlFile, ..., EEvaluator _evaluator, ...)`：客户端加载配置前都会经过的普通方法（本地路径与接收路径都调用），是叠加的接缝点；
- `XmlPatcher.PatchXml / ReadPatchXmlWithFixedModFolders`：原版 Mod 补丁应用 API，直接复用。

### 2.5 依赖项

| 依赖 | 说明 |
|------|------|
| `0_TFP_Harmony` | 游戏 Mods 目录下的前置模组（提供 Harmony 运行时）。**不可删除**，否则模组无法加载 |
| `Assembly-CSharp.dll` | 游戏主程序集，补丁目标所在 |
| `.NET Framework 4.8` | 编译目标框架，与 Unity 的 Mono 运行时兼容 |

---

## 3. 核心功能

### 3.1 功能一：保留服务器内容 + 叠加本地 CATUI 补丁（补丁3）

**机制**：在 `XmlPatcher.ApplyConditionalXmlBlocks` 的 Prefix 中，对本次从服务器收到的 XUi/qualityinfo 文档（通过 `receivedOverlayConfigs` 集合识别并一次性消费），先做实体还原，再对**本地 CATUI 系列模组**逐个应用其 `Config/<配置名>.xml` 补丁文件（复用原版 `XmlPatcher.PatchXml`）。

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(XmlPatcher), "ApplyConditionalXmlBlocks")]
public static bool ApplyConditionalXmlBlocks(string _xmlName, XmlFile _xmlFile, XmlPatcher.EEvaluator _evaluator)
{
    if (_evaluator != XmlPatcher.EEvaluator.Client) return true;
    if (_xmlName == null || (!_xmlName.Contains("XUi") && !_xmlName.Contains("qualityinfo"))) return true;
    if (!receivedOverlayConfigs.Remove(_xmlName)) return true;   // 只处理服务器收到的配置
    try
    {
        SanitizeBindingEntities(_xmlFile);          // 修复双重转义实体
        ApplyLocalCatuiPatch(_xmlName, _xmlFile);   // 叠加本地 CATUI 补丁
    }
    catch (Exception e)
    {
        Log.Error("{0} Failed to overlay local CATUI patch for '{1}': {2}", TAG, _xmlName, e);
    }
    return true;   // 继续原版条件块处理与 LoadMethod
}
```

**效果**：
- 服务器端其他 UI 定制（其他 Mod 对窗口/模板的改动）**保留**；
- 本地 CATUI 的界面改动**叠加生效**，优先级为"服务器内容 < 本地其他 Mod < CATUI（`ZZZ_` 最后应用，冲突时获胜）"；
- 单个补丁节点因服务器结构差异无法应用时，原版 `PatchXml` 仅输出 `did not apply` 警告并跳过，不崩溃（容错降级）。

**只叠加 CATUI 系列**（`Name`/`DisplayName` 以 `CATUI` 开头，含 `CATUI_backpack_91slot`、`CATUI_toolbelt_more_slot` 等配套），避免把客户端与服务器共用的其他 Mod 重复叠加导致重复插入。

### 3.2 功能二：双重转义实体还原（补丁3 内置）

服务器 XML 中把比较运算符写成双重转义（文件里是 `&amp;gt;=`）时，客户端解析后属性值仍是字面 `&gt;`，NCalc 会把它解析成"`&` 与运算符 + `gt` 标识符"，触发 `Parameter was not defined: gt`。`SanitizeBindingEntities` 会对**含 `{` 的绑定属性值**做实体还原（`&gt;`→`>`、`&lt;`→`<`、`&quot;`→`"`、`&apos;`→`'`、`&amp;`→`&`），最多 3 轮处理多层转义，仅作用于绑定值、不影响普通文本。

### 3.3 功能三：空 / 缺失配置自动回退本地（补丁1）

Postfix 挂在 `WorldStaticData.ReceivedConfigFile` 上，当收到名字含 `XUi` / `qualityinfo` 的配置时：

| 服务器下发情况 | 处理 |
|----------------|------|
| `_data == null`（未下发） | `Log.Warning` + 回退本地加载 + 记录 |
| `_data.Length == 0`（空文件，服务器 XML 缺失/不完整） | `Log.Warning` + 回退本地加载 + 记录 |
| `_data` 非空（正常下发） | 加入待叠加集合 + 绿色调试日志 `CATUI local load xml name: <name>` |

回退通过内部方法 `SetLoadLocal(name)` 实现：把对应条目的 `WasReceivedFromServer` 置为 `EClientFileState.LoadLocal` 并清空 `CompressedXmlData`，使原版 `handleReceivedConfigs` 走本地文件加载分支。

```csharp
if (_data != null && _data.Length == 0)
{
    Log.Warning("{0} Server sent an EMPTY config for '{1}'. This usually means the server's XML config is missing or incomplete. Falling back to local file.", TAG, _name);
    TrackLocalLoad(_name);
    SetLoadLocal(_name);
    return;
}
```

### 3.4 功能四：回退汇总日志（补丁2）

所有配置接收完成后（`AllConfigsReceivedAndLoaded` 返回 `true`、存在回退记录且本会话未输出过），输出一行汇总：

```
[CATUI] XML fallback summary: the following server configs were missing/incomplete and were loaded from local files instead: XUi_InGame/windows, qualityinfo, ...
```

### 3.5 使用示例（日志视角）

**正常联机、服务器正常下发**：

```
Received config file 'XUi_InGame/windows' from server. Len: 84781
<color=#00FF00>CATUI local load xml name: </color>XUi_InGame/windows
...
[CATUI] Overlaying local CATUI config 'XUi_InGame/windows' from mod 'CATUI' onto server config.
Loaded (received): XUi_InGame/windows
```

**服务器缺配置**：

```
[CATUI] Server sent an EMPTY config for 'XUi_InGame/windows'. ... Falling back to local file.
[CATUI] XML fallback summary: the following server configs were missing/incomplete and were loaded from local files instead: XUi_InGame/windows
```

**服务器配置结构分叉（个别补丁打不上，正常容错）**：

```
WRN XML patch for "" from mod "CATUI" did not apply: <remove xpath="..." (line 378 at pos 3)
```

---

## 4. API 参考

### 4.1 对外暴露的公共成员

| 成员 | 类型 | 说明 |
|------|------|------|
| `LocalLoadPatch.LocallyLoadedConfigs` | `public static readonly List<string>` | 记录本次会话中因服务器缺失/损坏而回退本地加载的配置文件名（不含重复）。其他模组可读取，用于诊断或二次开发 |

> 除上述字段外，模块**不对外提供**可调用函数。所有能力通过 Harmony 补丁以"拦截游戏原方法"的方式实现。

### 4.2 补丁目标与签名（内部实现参考）

**补丁1 — `WorldStaticData.ReceivedConfigFile`**

```
原型：public static void ReceivedConfigFile(string _name, byte[] _data)
返回：void（Postfix 不改变返回值）
参数：
  _name  string  配置名（如 "XUi_InGame/windows"）
  _data  byte[]  服务器下发的压缩数据；null = 未下发；长度0 = 空文件
行为：仅当 _name 含 "XUi" 或 "qualityinfo" 时介入（见 3.3）
```

**补丁2 — `WorldStaticData.AllConfigsReceivedAndLoaded`**

```
原型：public static bool AllConfigsReceivedAndLoaded()
参数：ref bool __result（原方法返回值）
行为：__result == true 且 LocallyLoadedConfigs 非空且未输出过 → 输出汇总并置 summaryLogged = true
```

**补丁3 — `XmlPatcher.ApplyConditionalXmlBlocks`**

```
原型：public static IEnumerator ApplyConditionalXmlBlocks(string _xmlName, XmlFile _xmlFile,
       MicroStopwatch _timer, XmlPatcher.EEvaluator _evaluator, Action _errorCallback)
参数（Prefix 只需匹配前三个）：
  _xmlName   string                 配置名
  _xmlFile   XmlFile                即将加载的 XML 文档（叠加会就地修改它）
  _evaluator XmlPatcher.EEvaluator  求值器；仅 Client 时介入
行为：见 3.1 / 3.2；return true 放行原方法
```

### 4.3 错误处理

- 补丁1 对 `_name` 有 null 守卫；空/缺失数据走回退分支，不抛异常；
- 补丁3 整体包 try/catch，叠加失败仅 `Log.Error` 不影响加载流程；内部 `XmlPatcher.PatchXml` 对单节点失败只 `Log.Warning` 并跳过；
- 原版 `handleReceivedConfigs` 对本地加载失败、解析失败、后置步骤失败均有异常回调与 `Log.Error`，本模块复用原版路径，不额外吞错；
- 实体还原仅在含 `{` 的绑定属性值上执行，不影响普通文本。

---

## 5. 集成指南

### 5.1 环境前提

- 已安装 `0_TFP_Harmony` 前置模组（游戏 Mods 目录下，勿删除）；
- 客户端需**关闭 Easy Anti-Cheat**；
- 主模组 `ZZZ_CATUI` 已安装（本模块叠加的是 CATUI 系列补丁，无 CATUI 时意义有限）。

### 5.2 方式一：直接部署（推荐）

1. 将 `ZZZ_CATUI_local_load/` 整个文件夹（内含 `CATUI_local_load.dll`、`ModInfo.xml`）复制到游戏 `Mods/` 目录：
   ```
   <游戏根目录>/Mods/ZZZ_CATUI_local_load/
   ```
2. 启动游戏进入联机存档，查看日志确认叠加/回退是否生效（见 3.5、8.2）。

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
| 专用服务器 + 多个客户端 | 无需 | **安装** | 客户端叠加自身 CATUI 补丁，服务器定制保留，互不干扰 |
| 专用服务器 + 客户端（只装客户端） | 不装 | 装 | 正常工作（叠加为客户端行为，不依赖服务器） |
| 单机 | 无需 | 可装可不装 | 单机默认本地加载，模组不干预，无副作用 |
| LAN 联机（房主开服） | 可选 | 参与者装 | 参与者端叠加生效 |

> 说明：新方案下本模块为**纯客户端**生效，服务器端无需安装；服务器若装了也只是空操作（服务器不"接收"配置，`receivedOverlayConfigs` 为空），无副作用。

### 5.5 与其他 CATUI 系列模组的搭配

`CATUI_backpack_91slot`、`CATUI_toolbelt_more_slot` 等可选模组同样以 `ZZZ_` 前缀部署，且它们的 `Name` 均以 `CATUI` 开头，会自动纳入叠加范围。本模块只处理 `XUi` 与 `qualityinfo` 配置，不影响其他模组的背包/工具栏布局数据，可正常共存。

---

## 6. 配置选项

| 选项 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| 生效配置范围 | 硬编码 | `XUi*` / `qualityinfo` | 仅名字含 `XUi` 或 `qualityinfo` 的配置受本模块影响，其余配置保持原版行为 |
| 叠加模组范围 | 硬编码 | CATUI 系列 | 仅叠加 `Name`/`DisplayName` 以 `CATUI` 开头的模组 |
| 实体还原 | 硬编码 | 开启，最多 3 轮 | 仅对含 `{` 的绑定属性值生效 |
| 日志行为 | 硬编码 | 开启 | 空/缺失配置输出 `Log.Warning`；正常下发输出绿色 `Debug.Log`；叠加输出 `Log.Out` |
| 汇总日志 | 硬编码 | 每会话一次 | `AllConfigsReceivedAndLoaded` 后仅输出一次，避免刷屏 |
| 回退方式 | 硬编码 | 本地文件 | 回退即 `EClientFileState.LoadLocal`，走原版本地加载管线 |

> 本模块**没有独立的配置文件**（Mods 目录下只有 DLL 与 ModInfo.xml），以上均为源码级行为。如需调整生效范围/叠加范围，需修改 `LocalLoadPatch.cs` 后重新编译。

---

## 7. 性能考量

### 7.1 开销分析

- **补丁1**：进入世界、服务器下发配置时对每个配置执行一次（约 49 次），非每帧逻辑；
- **补丁2**：所有配置接收完成的瞬间执行一次；
- **补丁3**：每个收到的 XUi/qualityinfo 配置执行一次：实体还原遍历该文档的属性（数量有限），叠加逐个应用本地 CATUI 补丁文件（通常 6 个文件、几十个补丁节点）。

三个补丁**均不在 Update / 渲染热路径上**，只发生在"连接服务器进入世界"的加载阶段，对帧率、内存、GC 的影响可忽略。

### 7.2 内存与资源

- `LocallyLoadedConfigs` / `receivedOverlayConfigs` 为小型集合，最多 49 项，常驻内存可忽略；
- 回退本地时清空 `CompressedXmlData`，避免保留服务器下发但未使用的数据；
- 叠加与实体还原就地修改内存中的 `XmlFile`，不产生额外拷贝、不落盘；
- 无额外贴图、图集、音频等资源加载。

### 7.3 优化点

- `SetLoadLocal` 使用线性遍历 `xmlsToLoad`（最多 49 项），无需优化；
- 日志仅在异常/汇总/叠加时输出，不会造成日志刷屏。

---

## 8. 已知限制与故障排查

### 8.1 已知限制

| # | 限制 | 说明 |
|---|------|------|
| 1 | 只覆盖 XUi 与 qualityinfo | 其余配置（blocks、items 等）仍以服务器版本为准。若服务器缺少这些配置，客户端仍可能报错（非本模块职责） |
| 2 | 叠加只针对 CATUI 系列模组 | 其他客户端本地 Mod 的 UI 改动在联机时不被叠加（原版行为，服务器内容为准） |
| 3 | 服务器结构分叉时补丁可能打不上 | CATUI 补丁目标在服务器配置中不存在时，输出 `did not apply` 警告并跳过，相关 CATUI 功能缺失但界面不崩溃 |
| 4 | 依赖 `0_TFP_Harmony` | 缺失该前置模组时，模组静默不生效（无报错），表现为问题依旧存在 |
| 5 | EAC 冲突 | 需关闭 Easy Anti-Cheat 才能加载 |
| 6 | 本地文件必须存在 | 回退本地后若客户端本地对应 XML 缺失，原版会输出 `XML loader: XML is missing` |
| 7 | 大修模组代码级兼容 | 若服务器为深度改造的 UI 大修模组（如 Z计划），可能与本模块叠加出的 CATUI 界面产生**代码级**冲突（例如窗口组缺少原版 `XUiC_RecipeCraftCount` 控制器导致 `XUiC_IngredientEntry.Init` 空引用、制造/工作站窗口初始化失败）——这类问题属于 CATUI 健壮性范畴，需在 CATUI 侧加安全补丁或反馈给模组作者，本模块不做代码级兜底 |

### 8.2 故障排查

**Q1：模组到底加载了没有？**
查看日志 `output_log_client__*.txt`（路径 `%AppData%/7DaysToDie/Logs` 或 `<游戏目录>/Logs`）。加载成功后应能看到模组入口（Harmony 打补丁）相关输出；若日志完全无本模组痕迹，先检查 `Mods/ZZZ_CATUI_local_load/` 结构是否完整、`0_TFP_Harmony` 是否还在。

**Q2：进入联机服务器后 UI 还是空白 / 报 `Can not parse input` / 绑定错误？**
- 若日志出现 `[CATUI] Server sent an EMPTY config ...` 后界面仍异常，说明回退成功但本地 CATUI 与其他服务器数据仍有冲突，请确认服务器与客户端 CATUI 版本一致；
- 若出现大量 `did not apply` 警告，说明服务器结构改动较大、部分 CATUI 补丁未生效，可接受或反馈服务器作者；
- 若日志无任何 `[CATUI]` 叠加/回退记录，说明服务器正常下发且无叠加对象，请确认客户端已安装本模组与 CATUI。

**Q3：出现 `Parameter was not defined: gt`（NCalc 报错）？**
通常是服务器配置把比较运算符写成了双重转义（`&amp;gt;=`）。本模块已在叠加阶段自动还原实体（`SanitizeBindingEntities`），更新到含该修复的版本即可消除；同时建议让服务器作者把 `visible="{% int(windowWidth) &amp;gt;= 300 }"` 改为 `>=`。

**Q4：制造/工作站窗口初始化失败（`Failed initializing window group crafting/workstation_*`）？**
多因服务器 UI 大修模组移除了原版依赖的控制器（如 `XUiC_RecipeCraftCount`）。这是 CATUI 与该服务器模组的兼容问题，可反馈模组作者，或在 CATUI 侧为该 `Init` 加空引用安全补丁。

**Q5：怎么知道哪些配置被回退了？**
搜索日志中的 `[CATUI] XML fallback summary:` 一行，会列出全部被回退的配置名；或直接读取 `LocalLoadPatch.LocallyLoadedConfigs`。

**Q6：单机进游戏没有变化？**
正常。单机默认从本地加载配置，本模组只作用于"服务器下发配置"环节。

**Q7：会不会影响存档或服务器数据？**
不会。本模块只改动客户端的**配置加载来源**（内存中的 `XmlLoadInfo` 状态与加载前文档），不写存档、不改服务器文件。

---

*文档由代码分析生成，基于 `LocalLoadPatch.cs`、`_Init.cs` 与 `WorldStaticData.cs` / `XmlPatcher.cs`（原版反编译）行为描述，请以实际版本为准。*
