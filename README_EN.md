# CATUI_local_load

CATUI multiplayer config overlay module. When joining a server, the client **keeps the server-provided XUi / qualityinfo configs** (server-side UI customizations are preserved), then **overlays the local CATUI-family mod patches** so the local CATUI UI stays intact. When the server sends an empty/missing config, it automatically falls back to loading from local files.

> Document version: 3.0.0 · Game: 7 Days to Die 3.1 · Target framework: .NET Framework 4.8 (C# 9.0) · Corresponding code: `LocalLoadPatch.cs` (overlay / fallback design)

---

## 1. Module Overview

### 1.1 Purpose

In 7 Days to Die multiplayer, before a player enters the world the server sends a batch of XML configs from `Data/Config` (49 items, including `blocks`, `items`, `XUi_InGame/windows`, etc.). The client loads UI and game data from the server-provided versions.

Two classes of problems can occur online:

1. **Server version mismatch / server without CATUI**: the server's XUi configs do not match the client's local CATUI, causing broken layouts, binding errors, or UI errors.
2. **Server config missing / corrupt**: if the server's XML is missing or incomplete, it may send an **empty file (0 bytes)** or **nothing at all**, leading to a blank UI and a flood of `Can not parse input` / binding-evaluation errors.

`CATUI_local_load` uses an **"overlay instead of full replacement"** strategy:

- Server sends valid config → **keep the server content**, overlay the local CATUI-family mod patches on top, and fix common double-escaped-entity issues found in server configs;
- Server sends empty/missing config → **fall back to local files**, keeping the UI intact.

### 1.2 Core Function (one-liner)

> When online, load XUi / qualityinfo as "server config as base + local CATUI patch overlay"; fall back to local when the config is missing or empty; emit diagnostic logs throughout.

### 1.3 Relationship to the Main Mod

This module is a companion to the main mod **CATUI** (`ZZZ_CATUI`). The main mod handles all UI customization; this module only ensures "the local CATUI patches take effect online". They are installed and loaded independently; the deploy folder name starts with `ZZZ_` so the mod loads last in alphabetical order, applying its Harmony patches after other mods.

---

## 2. Technical Architecture

### 2.1 Module Layout

```
CATUI_local_load/
├── Source/                          # Project source (build directory)
│   ├── CATUI_local_load.csproj     # .NET Framework 4.8 project
│   ├── _Init.cs                    # Mod entry IModApi → Harmony.PatchAll
│   ├── LocalLoadPatch.cs           # All Harmony patch logic (core)
│   ├── 0_TFP_Harmony/              # 0Harmony.dll referenced at build time
│   └── 7DaysToDie_Data_DLL/        # Game assemblies referenced at build time (Assembly-CSharp, etc.)
├── ZZZ_CATUI_local_load/           # Deploy output (place into game Mods folder)
│   ├── CATUI_local_load.dll
│   └── ModInfo.xml
└── README.md
```

### 2.2 Load & Patch Assembly Flow

```
Game start
  └─ Mod loader scans Mods/*/ModInfo.xml, detects ZZZ_CATUI_local_load
       └─ calls ModStartup.InitMod(Mod)
            └─ Harmony = new Harmony(assemblyName)
                 └─ harmony.PatchAll(Assembly.GetExecutingAssembly())
                      ├─ Patch 1: WorldStaticData.ReceivedConfigFile           Postfix (empty/missing → fallback, non-empty → mark for overlay)
                      ├─ Patch 2: WorldStaticData.AllConfigsReceivedAndLoaded  Postfix (fallback summary log)
                      └─ Patch 3: XmlPatcher.ApplyConditionalXmlBlocks         Prefix (entity sanitize + local CATUI patch overlay)
```

### 2.3 Component Relationships

This module has **3 Harmony patches** targeting the game's config-loading classes `WorldStaticData` and `XmlPatcher`, working together as a "detect → fallback/overlay → summary log" loop:

| Patch | Target | Type | Responsibility |
|-------|--------|------|----------------|
| Patch 1 | `WorldStaticData.ReceivedConfigFile` | Postfix | Intercept server-sent XUi/qualityinfo: empty(0 bytes)/missing(null) → fall back to local + track; non-empty → add to overlay set |
| Patch 2 | `WorldStaticData.AllConfigsReceivedAndLoaded` | Postfix | After all configs are received, print a "fell back to local" summary (once per session) |
| Patch 3 | `XmlPatcher.ApplyConditionalXmlBlocks` | Prefix | For received XUi/qualityinfo docs: restore double-escaped entities, then overlay local CATUI-family mod patches |

### 2.4 Key Code Locations (decompiled vanilla reference)

- `WorldStaticData.ReceivedConfigFile(string _name, byte[] _data)`: client entry point for each received server config;
- `WorldStaticData.handleReceivedConfigs()`: coroutine that loads received configs in order; when `WasReceivedFromServer == EClientFileState.LoadLocal` it loads from the local file;
- `WorldStaticData.AllConfigsReceivedAndLoaded()`: flag for "all configs received";
- `XmlPatcher.ApplyConditionalXmlBlocks(string _xmlName, XmlFile _xmlFile, ..., EEvaluator _evaluator, ...)`: a normal (non-coroutine) method both the local and received load paths pass through — the seam used for overlay;
- `XmlPatcher.PatchXml / ReadPatchXmlWithFixedModFolders`: vanilla mod-patch APIs, reused directly.

### 2.5 Dependencies

| Dependency | Description |
|------------|-------------|
| `0_TFP_Harmony` | Prerequisite mod in the game Mods folder (provides the Harmony runtime). **Do not delete** or the mod won't load |
| `Assembly-CSharp.dll` | Main game assembly containing the patch targets |
| `.NET Framework 4.8` | Build target, compatible with Unity's Mono runtime |

---

## 3. Key Features

### 3.1 Feature 1: Keep Server Content + Overlay Local CATUI Patches (Patch 3)

**Mechanism**: In the `XmlPatcher.ApplyConditionalXmlBlocks` prefix, for XUi/qualityinfo configs received from the server this session (identified and consumed once via the `receivedOverlayConfigs` set), first sanitize entities, then apply each **local CATUI-family mod's** `Config/<name>.xml` patch file using the vanilla `XmlPatcher.PatchXml`.

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(XmlPatcher), "ApplyConditionalXmlBlocks")]
public static bool ApplyConditionalXmlBlocks(string _xmlName, XmlFile _xmlFile, XmlPatcher.EEvaluator _evaluator)
{
    if (_evaluator != XmlPatcher.EEvaluator.Client) return true;
    if (_xmlName == null || (!_xmlName.Contains("XUi") && !_xmlName.Contains("qualityinfo"))) return true;
    if (!receivedOverlayConfigs.Remove(_xmlName)) return true;   // only server-received configs
    try
    {
        SanitizeBindingEntities(_xmlFile);          // fix double-escaped entities
        ApplyLocalCatuiPatch(_xmlName, _xmlFile);   // overlay local CATUI patches
    }
    catch (Exception e)
    {
        Log.Error("{0} Failed to overlay local CATUI patch for '{1}': {2}", TAG, _xmlName, e);
    }
    return true;   // continue vanilla conditional handling and LoadMethod
}
```

**Effects**:
- Server-side UI customizations (window/template changes from other server mods) are **preserved**;
- Local CATUI UI changes **take effect on top**; precedence is "server content < other local mods < CATUI (`ZZZ_` applied last, wins on conflicts)";
- If an individual patch node cannot apply because of server-structure differences, the vanilla `PatchXml` only logs a `did not apply` warning and skips it (graceful degradation, no crash).

**Only CATUI-family mods are overlaid** (`Name`/`DisplayName` starting with `CATUI`, including `CATUI_backpack_91slot`, `CATUI_toolbelt_more_slot`, etc.), avoiding duplicate insertion of mods shared between client and server.

### 3.2 Feature 2: Double-Escaped Entity Sanitization (inside Patch 3)

When a server XML writes a comparison operator double-escaped (e.g. `&amp;gt;=` in the file), the client's parsed attribute value still literally contains `&gt;`. NCalc then parses it as "`&` (AND operator) + `gt` identifier", triggering `Parameter was not defined: gt`. `SanitizeBindingEntities` restores entities in **binding attribute values that contain `{`** (`&gt;`→`>`, `&lt;`→`<`, `&quot;`→`"`, `&apos;`→`'`, `&amp;`→`&`), up to 3 passes to handle multiple levels of escaping. It only touches binding values, not plain text.

### 3.3 Feature 3: Auto-Fallback on Empty/Missing Configs (Patch 1)

Postfix on `WorldStaticData.ReceivedConfigFile`, for configs whose name contains `XUi` / `qualityinfo`:

| Server delivery | Handling |
|-----------------|----------|
| `_data == null` (not sent) | `Log.Warning` + fall back to local + track |
| `_data.Length == 0` (empty file; server XML missing/incomplete) | `Log.Warning` + fall back to local + track |
| `_data` non-empty (normal) | Add to overlay set + green debug log `CATUI local load xml name: <name>` |

Fallback is done via `SetLoadLocal(name)`: it sets the matching entry's `WasReceivedFromServer` to `EClientFileState.LoadLocal` and clears `CompressedXmlData`, so the vanilla `handleReceivedConfigs` loads from the local file.

```csharp
if (_data != null && _data.Length == 0)
{
    Log.Warning("{0} Server sent an EMPTY config for '{1}'. This usually means the server's XML config is missing or incomplete. Falling back to local file.", TAG, _name);
    TrackLocalLoad(_name);
    SetLoadLocal(_name);
    return;
}
```

### 3.4 Feature 4: Fallback Summary Log (Patch 2)

Once all configs are received (`AllConfigsReceivedAndLoaded` returns `true`, fallbacks exist, and not yet logged this session), print a one-line summary:

```
[CATUI] XML fallback summary: the following server configs were missing/incomplete and were loaded from local files instead: XUi_InGame/windows, qualityinfo, ...
```

### 3.5 Usage Examples (log view)

**Normal join, server delivers valid configs**:

```
Received config file 'XUi_InGame/windows' from server. Len: 84781
<color=#00FF00>CATUI local load xml name: </color>XUi_InGame/windows
...
[CATUI] Overlaying local CATUI config 'XUi_InGame/windows' from mod 'CATUI' onto server config.
Loaded (received): XUi_InGame/windows
```

**Server config missing**:

```
[CATUI] Server sent an EMPTY config for 'XUi_InGame/windows'. ... Falling back to local file.
[CATUI] XML fallback summary: the following server configs were missing/incomplete and were loaded from local files instead: XUi_InGame/windows
```

**Server structure diverges (some patches cannot apply — normal degradation)**:

```
WRN XML patch for "" from mod "CATUI" did not apply: <remove xpath="..." (line 378 at pos 3)
```

---

## 4. API Reference

### 4.1 Public Members

| Member | Type | Description |
|--------|------|-------------|
| `LocalLoadPatch.LocallyLoadedConfigs` | `public static readonly List<string>` | Config names that fell back to local loading this session because the server config was missing/corrupt (deduplicated). Readable by other mods for diagnostics or further development |

> Apart from this field, the module exposes **no callable functions**. All capabilities are implemented as Harmony patches that intercept game methods.

### 4.2 Patch Targets & Signatures (internal reference)

**Patch 1 — `WorldStaticData.ReceivedConfigFile`**

```
Signature: public static void ReceivedConfigFile(string _name, byte[] _data)
Return: void (Postfix does not change the return value)
Parameters:
  _name  string  config name (e.g. "XUi_InGame/windows")
  _data  byte[]  compressed data from the server; null = not sent; length 0 = empty file
Behavior: only acts when _name contains "XUi" or "qualityinfo" (see 3.3)
```

**Patch 2 — `WorldStaticData.AllConfigsReceivedAndLoaded`**

```
Signature: public static bool AllConfigsReceivedAndLoaded()
Parameters: ref bool __result (original return value)
Behavior: if __result == true, LocallyLoadedConfigs non-empty, and not yet logged → print summary and set summaryLogged = true
```

**Patch 3 — `XmlPatcher.ApplyConditionalXmlBlocks`**

```
Signature: public static IEnumerator ApplyConditionalXmlBlocks(string _xmlName, XmlFile _xmlFile,
            MicroStopwatch _timer, XmlPatcher.EEvaluator _evaluator, Action _errorCallback)
Parameters (Prefix needs only the first three):
  _xmlName   string                 config name
  _xmlFile   XmlFile                the document about to be loaded (overlay mutates it in place)
  _evaluator XmlPatcher.EEvaluator  evaluator; only acts when Client
Behavior: see 3.1 / 3.2; returns true to let the original method proceed
```

### 4.3 Error Handling

- Patch 1 null-guards `_name`; empty/missing data goes to the fallback branch without throwing;
- Patch 3 wraps the whole body in try/catch — a failed overlay only logs `Log.Error` and does not affect loading; internally `XmlPatcher.PatchXml` logs and skips individual failed nodes;
- The vanilla `handleReceivedConfigs` has exception callbacks and `Log.Error` for local-load/parse/post-step failures; this module reuses the vanilla path and does not swallow extra errors;
- Entity sanitization only runs on binding values containing `{`; plain text is untouched.

---

## 5. Integration Guide

### 5.1 Prerequisites

- Install the `0_TFP_Harmony` prerequisite mod (in the game Mods folder; do not delete);
- Client must **disable Easy Anti-Cheat**;
- Install the main mod `ZZZ_CATUI` (this module overlays CATUI-family patches; without CATUI it is of limited use).

### 5.2 Method 1: Direct Deploy (recommended)

1. Copy the whole `ZZZ_CATUI_local_load/` folder (containing `CATUI_local_load.dll` and `ModInfo.xml`) into the game `Mods/` folder:
   ```
   <game root>/Mods/ZZZ_CATUI_local_load/
   ```
2. Start the game, join a multiplayer save, and check the log for overlay/fallback activity (see 3.5, 8.2).

### 5.3 Method 2: Build from Source

```powershell
# run in the Source directory
dotnet build "H:\git\7D2D-CATUI\CATUI_local_load\Source\CATUI_local_load.csproj"
```

- Output: `Source\bin\Debug\net48\CATUI_local_load.dll`;
- Manually copy the DLL to `ZZZ_CATUI_local_load\`, overwriting the existing one (**this project has no PostBuild auto-copy**);
- Building depends on `0_TFP_Harmony\0Harmony.dll` and `7DaysToDie_Data_DLL\Assembly-CSharp.dll` etc.; update these references after a game version change.

### 5.4 Deployment Strategy per Environment

| Environment | Install on server? | Install on client? | Effect |
|-------------|--------------------|--------------------|--------|
| Dedicated server + multiple clients | Not needed | **Yes** | Client overlays its own CATUI patches; server customizations preserved |
| Dedicated server + client (client only) | No | Yes | Works normally (overlay is client-side, no server dependency) |
| Single-player | Not needed | Optional | Single-player already loads locally; the mod does nothing |
| LAN (host opens the server) | Optional | Participants | Overlay takes effect on participants |

> Note: with the new design this module is **client-only**. The server does not need it; installing it on the server is a harmless no-op (the server does not "receive" configs, so `receivedOverlayConfigs` stays empty).

### 5.5 Working with Other CATUI-family Mods

Optional mods such as `CATUI_backpack_91slot` and `CATUI_toolbelt_more_slot` are also deployed with the `ZZZ_` prefix, and their `Name` starts with `CATUI`, so they are automatically included in the overlay scope. This module only handles `XUi` and `qualityinfo` configs and does not affect other mods' backpack/toolbelt layout data.

---

## 6. Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| Affected configs | Hardcoded | `XUi*` / `qualityinfo` | Only configs whose name contains `XUi` or `qualityinfo` are affected; others keep vanilla behavior |
| Overlay mod scope | Hardcoded | CATUI family | Only overlays mods whose `Name`/`DisplayName` starts with `CATUI` |
| Entity sanitization | Hardcoded | On, up to 3 passes | Only applies to binding values containing `{` |
| Logging | Hardcoded | On | Empty/missing → `Log.Warning`; normal → green `Debug.Log`; overlay → `Log.Out` |
| Summary log | Hardcoded | Once per session | Printed once after `AllConfigsReceivedAndLoaded` to avoid spam |
| Fallback mode | Hardcoded | Local files | Fallback sets `EClientFileState.LoadLocal`, using the vanilla local-load pipeline |

> This module has **no standalone config file** (only the DLL and ModInfo.xml in the Mods folder); all behaviors above are compile-time. To change the affected scope or overlay scope, edit `LocalLoadPatch.cs` and rebuild.

---

## 7. Performance Considerations

### 7.1 Overhead Analysis

- **Patch 1**: runs once per config when the server delivers them at world-entry (~49 times), not per-frame;
- **Patch 2**: runs once when all configs are received;
- **Patch 3**: runs once per received XUi/qualityinfo config: sanitization walks that document's attributes (limited count); overlay applies the local CATUI patch files (usually 6 files, a few dozen nodes).

None of the patches sit on the Update/render hot path — they only run during the "join server / enter world" loading phase. Impact on FPS, memory, and GC is negligible.

### 7.2 Memory & Resources

- `LocallyLoadedConfigs` / `receivedOverlayConfigs` are small collections (≤49 entries), resident memory is negligible;
- On fallback, `CompressedXmlData` is cleared so unused server data is not retained;
- Overlay and sanitization mutate the in-memory `XmlFile` in place — no extra copies, no disk writes;
- No additional textures, atlases, or audio resources are loaded.

### 7.3 Optimization Notes

- `SetLoadLocal` linearly scans `xmlsToLoad` (≤49 entries); no optimization needed;
- Logs are only emitted on anomalies/summary/overlay, so there is no log spam.

---

## 8. Known Limitations & Troubleshooting

### 8.1 Known Limitations

| # | Limitation | Description |
|---|------------|-------------|
| 1 | Only XUi & qualityinfo are covered | Other configs (blocks, items, etc.) still come from the server. If the server lacks them, the client may still error (outside this module's scope) |
| 2 | Overlay targets only CATUI-family mods | UI changes from other client-local mods are not overlaid online (vanilla behavior; server content wins) |
| 3 | Patches may not apply when server structure diverges | When a CATUI patch target does not exist in the server config, a `did not apply` warning is logged and that node is skipped; the affected CATUI feature is missing but the UI does not crash |
| 4 | Depends on `0_TFP_Harmony` | If missing, the mod silently does nothing (no error), and the problem persists |
| 5 | EAC conflict | Easy Anti-Cheat must be disabled to load |
| 6 | Local files must exist | After fallback, if the corresponding local XML is missing, vanilla logs `XML loader: XML is missing` |
| 7 | Code-level compatibility with overhaul mods | On servers with deeply redesigned UI overhauls (e.g., "Z计划"/Project Z), the overlaid CATUI UI can hit **code-level** conflicts (e.g., the window group lacks the vanilla `XUiC_RecipeCraftCount` controller, causing `XUiC_IngredientEntry.Init` to throw a null reference and crafting/workstation window groups to fail initialization). Such issues are a CATUI robustness concern — fix in CATUI (safety patch) or report to the mod author; this module does not provide code-level fallbacks |

### 8.2 Troubleshooting

**Q1: Did the mod load at all?**
Check `output_log_client__*.txt` (in `%AppData%/7DaysToDie/Logs` or `<game dir>/Logs`). After a successful load you should see the mod entry (Harmony patching). If there is no trace at all, check that `Mods/ZZZ_CATUI_local_load/` is intact and `0_TFP_Harmony` still exists.

**Q2: After joining, the UI is still blank / `Can not parse input` / binding errors?**
- If `[CATUI] Server sent an EMPTY config ...` appears but the UI is still broken, the fallback succeeded but the local CATUI still conflicts with other server data — make sure server and client CATUI versions match;
- If many `did not apply` warnings appear, the server structure differs significantly and some CATUI patches did not take effect — acceptable, or report to the server author;
- If there is no `[CATUI]` overlay/fallback line at all, the server delivered valid configs with nothing to overlay — confirm this mod and CATUI are installed on the client.

**Q3: Seeing `Parameter was not defined: gt` (NCalc error)?**
Usually the server config wrote a comparison operator double-escaped (`&amp;gt;=`). This module restores entities automatically during overlay (`SanitizeBindingEntities`); update to the build containing that fix. Also ask the server author to change `visible="{% int(windowWidth) &amp;gt;= 300 }"` to `>=`.

**Q4: Crafting/workstation windows fail to initialize (`Failed initializing window group crafting/workstation_*`)?**
Often caused by the server's UI overhaul mod removing a vanilla controller the client code requires (e.g., `XUiC_RecipeCraftCount`). This is a compatibility issue between CATUI and that server mod — report to the mod author, or add a null-safe patch on that `Init` in CATUI.

**Q5: How do I know which configs fell back?**
Search the log for the line starting with `[CATUI] XML fallback summary:` — it lists all fallback config names. Or read `LocalLoadPatch.LocallyLoadedConfigs` directly.

**Q6: No change in single-player?**
Expected. Single-player loads configs locally by default; this mod only acts on the "server delivers configs" phase.

**Q7: Does it affect saves or server data?**
No. This module only changes the client's **config load source** (in-memory `XmlLoadInfo` state and the pre-load document); it writes no saves and modifies no server files.

---

*Document generated from code analysis, based on the behavior of `LocalLoadPatch.cs`, `_Init.cs`, and `WorldStaticData.cs` / `XmlPatcher.cs` (decompiled vanilla). Refer to the actual version for authoritative details.*
