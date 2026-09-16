# beta17 — 为什么不能从安装器里删掉任何一套 GCU 载荷

调查日期：2026-09-16（只读调查）。结论：**四套载荷全部保留，选择逻辑保持现状。**

## 背景

安装器打包四套 GCU 载荷，安装时选一套：

| 载荷 | 原始体积 | 灯区数 | 形态 |
|---|---|---|---|
| `release\GCU-only`（50 系） | 85.7 MB | 22 | 3+1 + Logo |
| `release\GCU-40-51751` | 56.1 MB | 22 | 3+1（`MEZone_3p1nd_*`） |
| `release\GCU-40-51749` | 39.3 MB | 18 | **Baseline** |
| `release\GCU-common` | 60 KB | — | 叠加 |

曾考虑"移除默认不可达的 `GCU-40-51749`"以缩小安装器。**结论是不可移除。**

## 证据（逐条可复核）

1. **三套控制台覆盖的是同一批机型** —— `docs\hardware\README.md:36`：三套的 `UserFanTables` 覆盖**同样的 24 个 ProjectID**。
   → 因此"读机型 → 判断该用哪套"**无法区分**三者：三套都支持。

2. **真正的差异是键盘灯区形态**（不是机型列表）：`docs\hardware\keyboard-zones.json`。
   → `51749` 是 **Baseline（18 区）**，另两套是 **3+1（22 区）**。早期判别函数
   `OfficialConsoleCatalog.DetectRgbLayout`（`Baseline` / `ThreePlusOne` / `LogoCapable`）
   于 beta17 删除（git 历史 `764f3f4`），其语义即此。

3. **每套服务里确有"支持列表"，但取值不可读** ——
   `_decompiled/GCUService/GCUService.decompiled.cs:7638`：
   `private List<BIOS_PROJECT_ID> SupportProjectIDs;`
   其取值位于被 ConfuserEx 销毁的方法体内（方法体被替换为抛异常）→ **静态不可恢复**。

4. **运行时唯一可靠的机型判据，以及它的缺口** —— `docs\hardware\README.md:41-63`：
   `BIOS_PROJECT_ID`（`HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport\BIOS_PROJECT_ID`，如 `IDY`）。
   但 **`BIOS_PROJECT_ID` ↔ `ProjectID`（`PH4*`/`PH6*` 机型码）的映射在仓库中明确标注为"尚未建立"**。

5. **`OfficialConsoleCatalog` 不含机型映射** ——
   `docs\superpowers\specs\2026-08-22-device-capability-resolution-design.md` 明确写明它
   "does not map a project ID to an advertised model"。删除它与变体选择无关。

6. **当前选择逻辑**（`installer\Select-GcuPayload.ps1`）：按 GPU 名称/PCI id + 注册表
   `BIOS_PROJECT_ID` + `SystemProductName` 判代际；40 系默认 `51751`；`/GCUVARIANT=` 可手工覆盖。

## 决定

- **不删任何载荷。** `GCU-40-51749` 是 **Baseline 灯区**那一套，删掉会让该布局机型的键盘灯效失效
  —— 这正是"省 39 MB 换来一批机器灯效坏掉"的典型错误交易。
- **安装器选择逻辑保持现状**（代际探测 + 手工覆盖），beta17 不改。
- **不在 beta17 引入"按机型查表"**：owner 确认手上**没有** baseline/3+1 的机型对应关系，且
  `SupportProjectIDs` 不可读 → **没有可靠数据源**。硬做会引入"装错变体"的风险，而装错直接
  影响散热与灯效。宁可保留一个已知可用的默认值 + 手工覆盖。
- 发布说明需写明两个 40 系变体的区别与 `/GCUVARIANT=` 的用法。

## 未来若要做得更准（需要新数据，不是新代码）

| 需要的东西 | 现状 |
|---|---|
| baseline / 3+1 的**机型对应表** | **没有**（owner 确认） |
| `BIOS_PROJECT_ID` ↔ `ProjectID` 映射 | **未建立** |
| `SupportProjectIDs` 取值 | **被混淆销毁**，静态不可读 |
| 机器上已安装的 OEM 键盘灯区注册表 | 可用，但**仅在机器装过官方控制台时存在**，全新装机拿不到 |
| 每套控制台的原始 `SupportProjectIDs` | 需向厂商/官方控制台渠道获取 |

→ **在拿到"机型 ↔ 灯区形态"对应表之前，不要改动变体选择**，也不要为了体积删任何一套载荷。
