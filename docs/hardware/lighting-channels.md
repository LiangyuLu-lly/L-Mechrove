# 灯光通道识别与控制（官方口径 × 我方实现）

出处前缀：`G40` = `_decompiled/gcu40-51751-27`（40 系 GCU 服务 1.0.2.70，未混淆，最权威）；`CCU` = `_decompiled/ccuwinui`（50 系控制台）；`DT` = `%TEMP%/kiro-defaulttool`（官方 DefaultTool）。行号 1 起。
本机 = 机械革命耀世 16 Ultra（Ultra 9 275HX + RTX 5080），BIOS_PROJECT_ID=IDY，ProjectID=26，已装 GCUService 1.2.0.0。

## 1. 官方如何判定每条通道存在

| 通道 | 服务端存在条件 | 官方界面显示条件 | 出处 |
|---|---|---|---|
| 键盘（RGB） | ITE HID：VID 048D，PID ∈ {CE00,6000,6001,6002,6003,6004,6006,6007,600A,600B}，**Usage=1**；按 UsagePage 分型（下表）；失败再试 EC 单区（EC 0x766 bit2） | `Keyboard/Status.type` ∈ 12 种逐键型号 或 `FourZone` → RGB 页；`SingleZone` → 单区页 | G40 `LightingModel/ITE_SPEC.cs:7-21`、`LM_ITE_RGB.cs:3770-3931`、`LM_EC_RGB.cs:37-50`；CCU `LightViewModel.cs:2210-2267` |
| 单色背光 | 单色项目白名单 + EC 0x78C bit0 → `Customize/Info.KeyboardType="2"` | `KeyboardType=="2"` 且不是单区 → 单色背光页（只有开关） | G40 `NvramVariable.cs:584-606`；CCU `LightViewModel.cs:2188-2191` |
| 主灯条（HidLightbar） | PID 7000/7001 **Usage=2** + UsagePage FF03 → Lighbar4；6005/6008/6010 Usage=1 + FW/BIOS → Lighbar1-3；设备为空时 GETSTATUS 抛异常**不发布** | `HidLightbar/Status.type` ∈ 4 代；Lighbar4 再按 BIOS：IDZ/IDB/IDA/IDX 走分区页，其余走普通灯条页 | G40 `LM_ITE_RGB.cs:4008-4080`、`HIDRGBLightbar.cs:324-347`；CCU `LightViewModel.cs:2381-2472` |
| Logo | 服务对**每台** Lighbar4 都建 Logo 通道（`HIDLightbar_logo.cs:197-199`） | IDZ/IDA/IDB/IDX 看 `LogoSupport`；IDA/IDB 看 `NewlogoSupport`（单线 Logo）；任何机型 `Support\MBALogo=1` → A 面 Logo | CCU `LightViewModel.cs:2425-2472、2514-2532` |
| 铰链（Hinge） | 同 Logo，每台 Lighbar4 都建通道并回状态壳 | 分区页里按 BIOS 显示；能力位 `HingeSupport` 只有 IDA/IDB/IDX/IDZ 才会被 0xA2 探测 | G40 `HIDLightbar_hinge.cs:200-202`、`LM_ITE_RGB.cs:4417-4623` |
| 同步灯带（Sync） | 仅 BIOS=IDZ 的 Lighbar4 初始化；否则状态全 null | `HidLightbarTitle_IDZ` | G40 `HIDLightbar_sync.cs:197-207`；CCU `LightPage.cs:670-685` |
| EC 灯带（MyRgbLightbar） | `LightbarType=="2"` 才启用管理器；否则 Control 被丢弃、不回状态 | `LightbarSupport≠0 && RGBLightbarSupport≠0`，收到 HID 灯条状态后隐藏 | G40 `App.cs:302-313、703-707`；CCU `LightViewModel.cs:2637-2683、2471-2472` |

**键盘分型**（G40 `LM_ITE_RGB.cs:3788-3921`，`HIDKeyboardFactory.cs:8-78`）

| UsagePage | 固件 Ver_High（0x80 回读） | RGBKB_Type | 形态 |
|---|---|---|---|
| FF12 | 任意（ProjectID 17 → FourZoneSingleColor） | FourZone / FourZoneSingleColor | 四区 / 四区单色（服务强制单色） |
| FF02 | 0x20 → MEZone_3nd_98，否则 MEZone_1st | 3nd_98 / 1st | 逐键（1st 官方工厂返回 null，无法经 GCU 控制） |
| FF03 | 0x12/0x13/0x16/0x14/0x20/0x22 + EC 0x73C(KBID) | 2nd / 2p1nd / 2p2nd / 3nd / 3p1nd | 逐键 |
| 其它 | — | 直接判无键盘 | — |

`ItemSupport\KeyboardType` 是 RGBKB_Type **序号**（`LM_Manager.cs:69`，枚举顺序 `LightingModel/RGBKB_Type.cs`）：0 Normal、1 SingleZone、2 FourZone、3 FourZoneSingleColor、4 MEZone_1st、5-6 2nd、7-10 3p1nd……

**0xA2 分区位**（仅 IDA/IDB/IDX/IDZ 探测，结果写 `RGBKeyboard\Force*`，G40 `LM_ITE_RGB.cs:4417-4623`）：IDX/IDZ：0/5→铰链，1→铰链+Logo，4→全部，6→铰链+Base；IDA/IDB 另有 NewLogo 组合。其余 BIOS 四个位恒 false。

## 2. 控制与回读载荷

| 通道 | Ctrl | Status | 关键字段 |
|---|---|---|---|
| 键盘 | `Keyboard/Ctrl` | `Keyboard/Status` | `SetEffectALL{mode,effect,light 0-4,speed 0-4,direction,nv_save,color{isCircular,ColorBlocks,ColorBuffer[{ID,R,G,B}]}}`；`SetPower{powerstatus}`；`{"Action":"GETSTATUS"}` |
| 单区附加 | 同上 | 同上 | 必带 `MonochromeIndex, ManualIndex1-6, ManualInterval, BreathingIndex`，缺一条整条丢弃（G40 `SingleZone.cs:282-330`） |
| 灯条/Logo/铰链/同步 | `HidLightbar[_Logo/_Hinge/_Sync]/Ctrl` | 对应 `/Status` | 同键盘结构（G40 `HIDRGBLightbar.cs:324-458`） |
| 单色背光 | `Setting/Control {SINGLE_COLOR_KBBL_STATUS_ON/OFF}` | `Setting/Status.SingleColorKBBL` | — |
| EC 灯带 | `MyRgbLightbar/Control {Action}` | `MyRgbLightbar/Status` | `POWER_ON/OFF, COLORFUL_ON/OFF, BREATHINGLIGHT, RL/GL/BL(+_DC) Level 0-9`；每个 Action 后回一帧（G40 `MyRgbLightbarManager.cs:84-223`） |

- `ColorBuffer[i]` 服务端逐块读 `["ID"]` 赋 uint（G40 `HIDKeyboard.cs:179-192`），官方 UI 每块都带 ID、发满 7 块。
- **Status 基本是服务缓存回显**（`brightNess`=缓存档位、`powerStatus`=缓存电源），效果字段只在 SetPower 后更新（G40 `RGBKeyboard.cs:417-458、525-613`）。
- 服务执行完灯效后把 `_EffectData` 落盘到 `HKLM\SOFTWARE\OEM\GamingCenter2\RGBKeyboard\<类型>\<ProjectID>_LastEffect`（`HIDKeyboard.SaveEffectData`，电源关时不写）。
- **设备回读**：ITE 控制器 `SetFeature{00,88}` + `GetFeature` 返回 `[2]Control [3]Effect [4]Speed [5]Light`（G40 `LM_ITE_RGB.cs:1110-1125`）。本机实测：键盘 `00-88-02-33-00-32`（我方自定义帧模式），固件 `00-80-22-03`（Ver_High 0x22 → 3p1nd）；灯条 0x88 全 0、0x80=`31.04.18.00`、0xA2=01。

## 3. 各通道效果与参数（界面只显示为 ✓ 的控件）

| 通道/形态 | 效果（颜色块：0 无 / 1 单色 / 多色=七彩或单色铺满） | 速度 | 方向 | 出处 |
|---|---|---|---|---|
| 逐键 | 单色1 · 呼吸多 · 波浪0 · 按键反应多 · 彩虹0 · 涟漪多 · 雨滴多 · 跑马灯多 · 火花多 · 极光多 · 游戏4 | 单色/彩虹/游戏 ✗，其余 ✓ | 波浪 4 向；反应/涟漪/火花/极光 无/按键触发 | CCU `MyKeyboardTypeInfo.cs:40-140`、`RgbKeyboardView.cs:1891-1947` |
| 四区 | 单色4（每区一色）· 呼吸 · 波浪0 · 彩虹0 · 混合 · 闪烁 | 单色/彩虹 ✗ | 波浪 左右 2 向 | CCU `MyKeyboardTypeInfo.cs:61-115`、`RgbKeyboardView.cs:1844-1890` |
| 四区单色 | 单色（固定色） | ✗ | ✗ | G40 `FourZoneSingleColorKeyboard.cs:15-20` |
| EC 单区 | 单色（30 色调色板吸附）· 彩虹 | ✗ | ✗ | CCU `SingleZoneKeyboardView.cs:697-703`；G40 `SingleZone.cs:35-74` |
| Lighbar4 主灯条 / 铰链 | 单色1 · 呼吸多 · 波浪0 · 冲击多 · 流星1（ID5 只有前三项、波浪无速度） | 单色 ✗ | ✗ | CCU `MyHidLightbarTypeInfo.cs:116-204`、`SmartLightbarView.cs:1820-1900` |
| Logo（A 面，MBALogo） | 单色1+亮度 · 呼吸（6 种预设色，无亮度）· 混合（无颜色无亮度） | 全 ✗ | ✗ | CCU `SmartLightbarIDZ_Logo_View.cs:1612-1690` |
| Logo（普通 / 单线） | 单色 · 呼吸 ／ 单色·呼吸·波浪·冲击·流星·彩色跑马灯 | 单色 ✗ | ✗ | 同上 |
| 同步灯带 | 单色 · 晨曦（服务换成固件预设色） | 晨曦 ✓ | ✗ | CCU `MyHidLightbarTypeInfo.cs:204`；G40 `HIDLightbar4_sync.cs:176-257` |

## 4. 我方现状与差距（改动前）

| 问题 | 后果 |
|---|---|
| `SupportsKeyboard = KeyboardSupport(官方恒 1) ‖ 收到过 Keyboard/Status` | 无 RGB 键盘的机器（服务回 `type=Normal`）也显示键盘行与托盘「键盘灯效」 |
| `SupportsLogoLight = Logo 状态有内容 ‖ 注册表有 Lightbar_logo_*` | 每台 Lighbar4 都回 Logo 状态壳 → 假 Logo 入口 |
| 注册表 `KeyboardType` 1/2 都当「单区」 | 四区键盘（序号 2）只剩单色/呼吸两项 |
| 软件路径评分只看 FF03/MI_01 | 灯条 `048D:7001 MI_01 FF03` 得分与键盘接近（本机 34 vs 38），键盘 PID 不是 600B 时会把逐键帧写进灯条；四区 FF12 在 MI_01 时也会被当逐键 |
| GCU 载荷色块不带 ID、所有参数行常驻 | 与官方解析不一致；速度/颜色对不采用它们的效果「点了没用」 |
| 下发只看发布是否成功 | 没有「已确认 / 已下发」区分 |
| 铰链/同步/EC 灯带已整体移除 | 与官方通道集合不一致 |

## 5. 最终设计

- **识别**：`Hardware/LightingChannels.cs`（纯函数）按上表复刻；证据优先级：实时 `Keyboard/Status.type` > `ItemSupport\KeyboardType`（仅服务未回报时）> `Customize/Info.KeyboardType=="2"` > 我方 HID 扫描（官方 PID/Usage/UsagePage 判据）。灯条/Logo/铰链/同步/EC 各按第 1 节条件，**状态壳不等于灯珠**。本机结论：逐键 3p1nd_101、Lighbar4 主灯条、A 面 Logo；无铰链/同步/EC/单色背光。
- **路径**：键盘逐键（FF03/FF02）优先我方软件 HID 帧；HID 无兼容接口（四区/单区/无）、打不开/进不了自定义模式、或亮度写入被拒 → 服务在线时自动改走官方 GCU（`KeyboardLightPathPolicy`），效果目录按分型给出（第 3 节）。其余灯光通道只走 GCU。
- **回读**：软件路径 = 进入自定义模式后 0x88 回读 `Control=2/Effect=0x33` → 「已确认（设备回读）」；GCU 键盘 = 下发后 0x88 回读效果号一致 → 设备回读；其余 = GETSTATUS 新帧 `powerStatus=On`+亮度档一致，且服务落盘的上次灯效效果号一致 → 「已确认（官方服务回读）」；证据缺失/不一致 → 「已下发（未收到回读）」；没发出去 → 「失败」。EC 灯带看每个 Action 后的状态帧。
- **界面**：灯光组只显示识别到的通道行（键盘/灯条/Logo/铰链/同步/EC 灯带），参数页只显示当前效果采用的参数；结果显示在灯光组底部一行与参数窗状态行，8 秒后收起。

## 6. 未真机验证

本机没有：四区 / 四区单色 / EC 单区 / 单色背光键盘，Lighbar1-3，IDZ/IDA/IDB/IDX 的分区页、铰链、同步灯带、单线 Logo，EC 灯带（MyRgbLightbar）。这些按官方代码实现识别与载荷，未经真机下发验证。EC 单区的「手动（6 色轮换）」与 EC 灯带的呼吸/RGB 电平未移植（不列出）。
