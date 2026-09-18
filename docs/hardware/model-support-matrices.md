# 机型/代际支持矩阵（唯一参考）

本文件是**后续机型接入的唯一参考**：24 个平台代号（轴 1）、5 张厂商名单、4 个风扇表键集分组、
逐代显卡/显示路由事实表（轴 2），以及两套模式枚举。机器可读块由
`tests\MechrevoLite.Tests\HardwareDocsParityTests.cs` 与代码逐项校验（注册表数据、逐代事实表、
模式枚举）——**文档漂移立即测试失败**，因此这里的数据不会与代码分叉。

> 轴 1（平台/机箱代号）**绝不**决定轴 2（dGPU 代际）。代际只在运行时探测（GPU 营销名 /
> NVIDIA PCI device-id 高字节）；`BIOS_PROJECT_ID` 仅佐证、不作判据。

## 1. 24 个平台代号（轴 1）

来源：`Resources\model-registry.json`（T2），目录名 = `UserFanTables` 的 24 个目录。

| 平台代号 | ProjectID | 风扇表分组 |
|---|---|---|
| PH4AQE3 | 6154 | k12 |
| PH4AQxx | (无) | k11 |
| PH4ARxx | 5889 | k11 |
| PH4AUxf | 5895 | k11 |
| PH4AUxx | 5890 | k11 |
| PH4AXxx | 5891 | k11 |
| PH4PGx1 | 6147 | k12 |
| PH4PGx2 | 6148 | k12 |
| PH4PRxx | 6145 | k9 |
| PH4PUxx | 6146 | k9 |
| PH4TQx1 | 20 | k11 |
| PH4TRX1 | 18 | k9 |
| PH4TUX1 | 19 | k9 |
| PH6AQxx | 5892 | k11 |
| PH6ARxx | 5893 | k11 |
| PH6PG0x | 6151 | k16 |
| PH6PG0x150W | 6155 | k16 |
| PH6PG3x | 6152 | k16 |
| PH6PG3x150W | 6156 | k16 |
| PH6PG7x | 6153 | k16 |
| PH6PG7x150W | 6157 | k16 |
| PH6PGEx | 6150 | k12 |
| PH6PRxx | 6149 | k12 |
| PH6TRX1 | 21 | k9 |

`PH4AQxx` 有风扇表目录但不在厂商 `ProjectID` 枚举内；`PH6TQxx`/`PH6AGxx` 有枚举但无目录
→ 按 F3 判 `NotInSet`（见 `ModelSupport`）。

## 2. 5 张厂商名单

来源：厂商 `GCUService` 名单常量（T2 逐项核对）。名单只在身份匹配时使用，不重推厂商判据。

| 名单 | 成员数 |
|---|---|
| commercial | 28 |
| commercialHave20Db | 14 |
| singleColorKeyboard | 9 |
| nonNumpad | 3 |
| idpIdy | 3 |

## 3. 4 个风扇表键集分组（由实测键集派生，Σ = 24）

分组规则 = 每机型 6 张表顶层键（去掉 `Activated`/`Name`）**并集**归一，键集相同者同组。

| 分组 | 键数 | 机型数 |
|---|---|---|
| k9 | 9 | 5 |
| k11 | 11 | 8 |
| k12（含 CTGP） | 12 | 5 |
| k16（含 CTGP2/DB2/PL*_S2） | 16 | 6 |

## 4. 逐代显卡/显示路由事实表（轴 2）

只记录**厂商行为**与已证事实，并把证据**拆成两列**：**控制台侧协议**（控制台发什么：MQTT topic
+ 动作词汇，逐方法/逐符号反编译或 .NET Native 元数据+PDB 证实）与**服务侧写路径**（厂商服务怎么
落地硬件）。**显示路由控制台只发 MQTT，从不写 `OemDisplayMode` 固件变量**；30/50 系服务侧写路径
保持 **UNKNOWN**（30 系 `MySettingManager` 未反编译、50 系服务 IL 混淆），只有 **40 系**服务侧
写路径 PROVEN。

| 代际 | 控制台侧协议 | 服务侧写路径 | iGPU-only | RESTART | 热切换 |
|---|---|---|---|---|---|
| 30 | INFERRED（`Setting/Control`；动作仅 `..._TOGGLE_ON/OFF`；.NET Native 无 IL，仅元数据+PDB 符号级） | UNKNOWN | PROVEN_ABSENT | PROVEN_ABSENT | PROVEN_ABSENT（`HOTSWAP` 0 命中） |
| 40 | PROVEN（`Setting/Control`；含 `..._IGPU`/`..._RESTART`/`IGPU_ONLY_*`） | PROVEN | PROVEN（40A WMI 0x30000000x） | PROVEN（`shutdown /r /t 0`） | UNKNOWN（0 命中） |
| 50 | PROVEN（`Setting/Control`；含 `..._RESTART`/`IGPU_ONLY_*`/`GPU_HOTSWAP_*`） | UNKNOWN | PROVEN（每 2 s 重发 / count>60 / 每第 4 次） | PROVEN（`Task.Delay(800)` 后发） | INFERRED（处理器 no-op） |

30 系控制台侧标 **INFERRED**：厂商程序集为 .NET Native（无 IL，无 C# 可反编译），依据是
`.omo\evidence\g30-console-decompile.md` 的**元数据标识符堆 + 完整 PDB 符号表 + 全载荷 0 命中**
（符号级证据，不得升为 PROVEN）；`DGPU_DIRECT_CONNECT_TOGGLE_IGPU`、`..._RESTART`、`IGPU_ONLY_*`、
`GPU_HOTSWAP_*`、`SetToWMIEC` 在整个 477 文件载荷中 0 命中（PROVEN_ABSENT）。40/50 系控制台侧为
**PROVEN**（逐方法反编译的真实 C# 代码）。

**平台代号 → 代际 = INFERRED（未决，非厂商验证）**。矛盾出处：
`docs\upgrade-from-openrevo.md:140` 与 `docs\gcu-dependency-matrix.md:88` 相互矛盾，无可引用的
平台代号 → GN2x 映射，因此**登记未决、绝不用于门控**。

## 5. 两套模式枚举（分别建模，禁止隐式同值转换）

| 厂商 `SysPowerModeIndex` | 值 | | 控制台 `OperatingMode` | 值 |
|---|---|---|---|---|
| Performance | 1 | | Office | 0 |
| Balanced | 2 | | Gaming | 1 |
| BatterySaver | 3 | | Turbo | 2 |
| Benchmark | 4 | | Customize | 3 |

转换只经 `Mode\PowerModeEnums.cs` 的显式命名函数（`PowerModeMapping`）；下发永远是控制台
`OPERATING_*_MODE` + `SET_OPERATING_MODE_DETAIL` 的**绝对量**。

## 6. 机器可读块（测试解析校验；勿手改单边）

<!-- gcu-parity:begin -->
{
  "platformCodes": [
    "PH4AQE3", "PH4AQxx", "PH4ARxx", "PH4AUxf", "PH4AUxx", "PH4AXxx", "PH4PGx1", "PH4PGx2",
    "PH4PRxx", "PH4PUxx", "PH4TQx1", "PH4TRX1", "PH4TUX1", "PH6AQxx", "PH6ARxx", "PH6PG0x",
    "PH6PG0x150W", "PH6PG3x", "PH6PG3x150W", "PH6PG7x", "PH6PG7x150W", "PH6PGEx", "PH6PRxx", "PH6TRX1"
  ],
  "vendorListCounts": {
    "commercial": 28,
    "commercialHave20Db": 14,
    "singleColorKeyboard": 9,
    "nonNumpad": 3,
    "idpIdy": 3
  },
  "fanTableGroupCounts": {
    "k9": 5,
    "k11": 8,
    "k12": 5,
    "k16": 6
  },
  "dgpuGenerations": {
    "30": { "consoleProtocol": "INFERRED", "serviceWritePath": "UNKNOWN", "igpuOnly": "PROVEN_ABSENT", "restart": "PROVEN_ABSENT" },
    "40": { "consoleProtocol": "PROVEN", "serviceWritePath": "PROVEN", "igpuOnly": "PROVEN", "restart": "PROVEN" },
    "50": { "consoleProtocol": "PROVEN", "serviceWritePath": "UNKNOWN", "igpuOnly": "PROVEN", "restart": "PROVEN" }
  },
  "vendorSysPowerModes": {
    "Performance": 1,
    "Balanced": 2,
    "BatterySaver": 3,
    "Benchmark": 4
  },
  "consoleOperatingModes": {
    "Office": 0,
    "Gaming": 1,
    "Turbo": 2,
    "Customize": 3
  },
  "platformCodeToGeneration": "INFERRED"
}
<!-- gcu-parity:end -->

## 7. 维护规则

- 新增机型：先在 `Resources\model-registry.json` 增加平台代号与风扇表分组，**再**同步本文件；
  测试会拒绝任何单边变更。
- 代际事实变化（例如 30/50 系写路径被证实）：改 `Gpu\DisplayRouteMatrix.cs` 的 `RouteCell`，
  同步本文件第 4 节与机器可读块（**两列都要**：`consoleProtocol` 控制台侧、`serviceWritePath`
  服务侧）；**证据只能降级、不得升级**（UNKNOWN 可降为 INFERRED，INFERRED 不得升为 PROVEN），
  `PROVEN` 仅限代码级证据，符号级（元数据/PDB/字符串）最高 INFERRED。
- 本文件与 `docs\hardware\README.md`（寄存器/EC 数据底座）互补：本文件是**支持矩阵**，
  README 是**寄存器/协议勘查**。
