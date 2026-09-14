# 机械革命简洁版控制台（工作名：MechrevoLite）设计文档

> 日期：2026-08-04
> 状态：已批准（经 6 节逐节评审）
> 参考：G-Helper（seerge/g-helper，华硕 Armoury Crate 轻量替代），本机已下载源码于 `g-helper-main/`
> 逆向依据：`ARCHITECTURE.md`（系统架构）、`FEATURES.md`（原版 99 项开关清单与 MQTT 载荷）

## 1. 目标与背景

为机械革命笔记本开发一个类 G-Helper 的简洁版控制台，替代原版 Control CenterX：
- **简洁直观**：扁平导航、模式一键切换、按机型能力显隐开关
- **占用更小**：单文件 WPF 应用，最终态无后台服务常驻（仅保留内核驱动 UWACPI）
- **功能完整**：覆盖原版四大功能域（散热与性能、显示与显卡、RGB 灯效、电池与系统开关）；游戏联动（GameProfile/GamingMonitor/Doudou）与 OTA 后期再加

目标机型：优先适配用户自有机型（BIOS_PROJECT_ID=`IDY`，Intel+NVIDIA 平台，支持液冷/独显直连/核显模式/超频/校色，不支持小键盘/LocalDimming/Overdrive）。

## 2. 关键技术决策

| 决策 | 选择 | 理由 |
|---|---|---|
| 硬件访问 | 混合：Phase 1 复用原版 MQTT，Phase 2 直连驱动 | 风险最低，Phase 1 出可用版 |
| 技术栈 | WPF + .NET 8，单文件发布 | 现代 UI（OSD/动画/雷达），运行时共享 |
| 运行形态 | 托盘常驻 + 主窗口，开机自启（可选） | 同 G-Helper |
| 机型范围 | 先只支持自有机型（IDY） | 保证质量，后续扩展 |
| 能力探测 | 直接读注册表 `HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport` | 原版 UI 同源，免 MQTT 往返 |
| 后端架构 | `IHardwareBackend` 抽象，MqttBackend/DriverBackend 双实现 | 平滑迁移、可回退 |

## 3. 架构

```
MechrevoLite.exe (WPF, .NET 8, 单文件, 托盘常驻)
├─ UI 层：Home | 风扇 | GPU/显示 | 灯效 | 电池 | 快捷开关 | 设置 （MVVM）
├─ 服务层：CapabilityService(读 ItemSupport)
│         TelemetryService(5s 轮询) + Mode/Fan/Gpu/Display/Lighting/Battery/QuickSwitch
│         IHardwareBackend ── MqttBackend(Phase1) ── DriverBackend(Phase2)
├─ 基础设施：MQTT(M2Mqtt.Net) | HID | Win32(注册表/电源计划/OSD/托盘/自启动)
└─ Models：载荷 DTO（字段名严格对齐反编译出的原版结构）
```

### Phase 1 数据流（MQTT 接入）

```
启动 → 检查 GCUBridge 服务
  ├─ 未运行 → 引导条 + 一键 sc start GCUBridge
  └─ 运行中 → MQTT 连接（UWPClient 凭据）→
       ├─ 读 ItemSupport/SMAPCTable/MyLightBar 注册表
       ├─ Customize/Control {GETSUPPORT} → Customize/SupportInfo
       ├─ 各页进入发 GETSTATUS（Fan/Setting/Keyboard…）
       └─ 窗口激活 → System/Control {System_ON} → 5s 遥测流；失活发 System_OFF
```

已实测事实：
- `\\.\ACPIDriver` 可打开、EC 可直读（Phase 2 通路已验证）
- broker 接受 `UWPClient_5` 连接但订阅不推流、发布 System/Control 后断连 → UI 完整握手未复刻，M1 解决

### Phase 2 直连路径

```
EC 访问   : CreateFile("\\.\ACPIDriver") → DeviceIoControl
             ├─ IOCTL_GPD_ACPI_ECREAD/ECWRITE (0x9C402108/0x9C40210C)
             ├─ IOCTL_GPD_ACPI_SMAPCTABLE (0x9C402200) — 风扇表/功耗墙持久化
             ├─ IOCTL_GPD_ACPI_CUSTOMCTL (0x9C402204)
             └─ SMI: SMRW_CMD_READ=0xBB / WRITE=0xAA
RGB HID   : SetupAPI 枚举 + HidD/HidP 直写（键盘 feature report、灯条）
电源计划   : PowrProf + 注册表（ECO/STD）
监控       : LibreHardwareMonitor 或 Win32 API
```

寄存器映射表建设（协议嗅探对照法）：
1. Phase 1 记录每次 MQTT 控制命令+回包，同时定时直读 EC 寄存器快照，两边对照
2. 以注册表 SMAPCTable（PL1=210/PL2=210/PL4=210/TccOffset=10 等已知值）为锚点反验
3. GCUService NLog（`C:\ProgramData\ControlCenter\*.log`）含 ECSpec 寄存器名线索

替换顺序（风险递增）：只读遥测 → 电源模式+功耗墙 → 风扇曲线 → 电池/快捷开关 → RGB 灯效（HID 最复杂最后）。全部替换后提供"停用 GCUBridge 服务"入口，驱动 UWACPI 保留。

## 4. UI 设计

主窗口：左侧窄导航图标栏 + 右侧内容区。首页为模式大卡片（办公/游戏/极速/自定义，单击生效）+ 关键指标雷达（CPU/GPU 温度、风扇转速）+ FanBoost + 极速子模式。

| 页 | 内容 |
|---|---|
| 首页 | 模式大卡片 + 指标雷达 + FanBoost + 极速子模式 |
| 风扇 | 16 点曲线编辑器（CPU/GPU 双折线可拖点）+ PL1/PL2/TCC + 高级 OC |
| GPU/显示 | 显卡模式（核显/独显直连/自动）+ 刷新率 + 校色色域 + 显示开关 |
| 灯效 | 键盘（效果/颜色/亮度/逐键）+ 灯条（本体/logo/转轴/同步） |
| 电池 | 健康模式 + 充电保护 + USB 充电 + 深度睡眠 |
| 快捷开关 | 卡片网格（触摸板/摄像头/蓝牙/WiFi/Win键/OSD/Fn…，按机型显隐） |
| 设置 | 语言/主题色/开机自启/托盘选项/服务管理 |

交互原则：模式切换单击生效 + OSD 悬浮提示（5s）；所有开关带加载/失败状态，不静默；机型不支持的开关直接隐藏（不留灰态）；托盘图标随模式变色 + 右键快速切模式。

## 5. 错误处理

- 写入不静默：每操作带状态回调，失败显示"写入失败"+ 原因
- 服务缺失：引导条 + 一键启动（需管理员）
- 断线：MQTT 5s 自动重连；UI 显示"离线"；操作排队重放（≤3 次）
- 未授权拦截：独显直连仅交流电可点（IsAC 判断）；需重启的切换先确认
- 崩溃恢复：写入前快照设置到本地 JSON，异常重启后提示恢复

## 6. 测试策略

- **真实硬件协议测试**（主）：本机为测试台，开关行为对照原版 UI + 注册表值 + `C:\ProgramData\ControlCenter\*.log`
- **MQTT 协议回放**：录制原版 UI 消息序列作为 golden reference 自动比对
- **能力矩阵参数化测试**：改 ItemSupport 键值模拟机型分支
- **UI 冒烟**：模式切换/灯效核心链路手动走查
- 单元测试覆盖：载荷 DTO 序列化、曲线点换算、能力矩阵解析

## 7. 里程碑

| 里程碑 | 内容 | 验收标准 |
|---|---|---|
| M1 探测工具 | MQTT 握手复刻 + 协议嗅探工具 | 摸清 broker 鉴权/路由，遥测流打通 |
| M2 骨架 | WPF 项目 + 托盘 + 后端抽象 + 能力矩阵 | 启动读全本机能力，UI 按能力显隐 |
| M3 散热核心 | 模式切换 + 遥测雷达 + 风扇曲线页 | 与注册表/日志对照，四模式切换生效 |
| M4 电池/快捷开关 | 电池页 + 快捷开关页 | 状态与 System/BatteryProtection 一致 |
| M5 GPU/显示 | MUX 切换 + 刷新率 + 校色 | 独显直连流程完整（含重启确认） |
| M6 灯效 | 键盘 + 灯条全套 | 灯效与 Keyboard/Status 同步 |
| M7 直连迁移 | 逐域替换 DriverBackend | 每域替换后停用服务验证 |
| M8 收尾 | 服务停用按钮 + 单文件打包 | exe + 使用文档 |

所有里程碑基于本机实测验收，不做 mock。

## 8. 已明确不做的（YAGNI）

- 游戏联动（GameProfile/GamingMonitor/Doudou 豆豆助手）、OTA 更新 —— 后期
- 液冷页面 —— 本机虽支持液冷（LiquidCoolingSupport=1），但无液冷设备，Phase 1 不做，架构预留（BT_LC 载荷已解码）
- 多机型适配 —— 后续扩展
- 原版 UI 同款的三级导航/横幅布局
