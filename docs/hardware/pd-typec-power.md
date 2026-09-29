# Type-C / PD 供电：EC 只读分析

目标：把「读取 PD 快充当前功率、USB-C 反向供电功率」这类官方没开放的参数落到确切寄存器上。
**全程只读**，没有任何 EC 写入。

证据来源：`_decompiled/gcu40-51751-27/`（控制台 5.17.51.27 内的 GCUService 1.0.2.70，
**真实方法体**）；本机只读回读（耀世 / IDY、Core Ultra 9 275HX、RTX 5080、原装圆口适配器在插）。

## 1. 已确定的寄存器

| 名称 | 地址 | 掩码 | 含义 | 证据 |
|---|---|---|---|---|
| `ADDR_COMPLEX_POWER_STATUS` | **1996 = 0x7CC** | `& 0x01` | **圆口（DC barrel）适配器在插** | `MyECIO/MyEcCtrl.cs:151-157` `GetRoundSocketStatusFromEC` |
| 同上 | 0x7CC | `& 0x36` | **Type-C 供电状态**字节 | `MyEcCtrl.cs:144-150` `GetTypeCsatusFromEC` |
| 同上 | 0x7CC | `& 0x06` | **正在用 Type-C 充电**（bit1 或 bit2 置位即为真） | `GCService5/GPUDeviceItem.cs:128-136` `isTypeCCharging` |
| `ADDR_BIOS_INFO_3_BYTE` | **1183 = 0x49F** | `& 0x78` | **适配器额定瓦数**（查表） | `MyEcCtrl.cs:106-143` `GetAdapterWattFromEC` |
| `ecPowSource` | 1168 = 0x490 | 位域 | 电源来源位域，含义未解码 | `Define/ECSpec.cs:129` |

`isRoundSocketCharging()`（`GPUDeviceItem.cs:137-145`）的判据是
`round==1 || (AC 在线 && (TypeC & 6)==0)`——也就是「圆口位置位，或者虽然没报圆口但系统在交流供电
且 Type-C 没在充电」。厂商用它区分「圆口供电（可跑高性能）」与「Type-C 供电（要降频）」。

`0x36` = `0b0011_0110` = bit1、bit2、bit4、bit5。厂商只用 bit1|bit2 判断是否在充电，
**bit4 / bit5 被纳入「Type-C 状态」但没有任何代码消费**——这两位是 PD 细节最可能的落点
（例如 PD 协商档位、是否支持反向供电）。

### 本机实测（圆口适配器在插，无 Type-C 供电）

```
0x7CC = 0x81 = 0b1000_0001   -> bit0=1（圆口在插）, bits1-2=0（Type-C 未充电）, & 0x36 = 0
0x49F = 0x5A                 -> & 0x78 = 88
0x490 = 0x07
```

`0x7CC` 的 **bit7 置位**，厂商代码从不读这一位，含义未知。

## 2. 发现的厂商缺陷：适配器瓦数查表覆盖不到 50 系

`GetAdapterWattFromEC` 的查表只有 `{0:330, 8:230, 16:180, 24:150, 32:120, 40:90, 48:65, 56:40, 64:280}`，
**表外一律返回默认 150 W**。本机 `0x49F & 0x78 = 88`，不在表内 → 厂商自己会把这台
330 W 的机器报成 150 W。

这不只是显示问题：`ModelRegistry.ExpandProjectId`（我们移植的 `GetProject2ExID`）在
`projectId==24` 的分支里用 `adapterWatt != 150` 来区分 6151/6155、6152/6156、6153/6157
三对机型代号。查表落到默认值 150，意味着**这条判据在新机型上会系统性地选错分支**。

> 处理方式：我们这边不照抄这张表。适配器瓦数要么如实标注「未知」，
> 要么等采集到更多机型的 `0x49F` 原始值后再补表；机型识别不要依赖它。

## 3. 没能确定的：Type-C / PD 的**功率数值**

- 50 系服务 `GCUService.exe` 1.2.0.0（混淆）的标识符里有
  `GetPowerFromTypeC`、`GetTypeCsatusFromEC`、`GetRoundSocketStatusFromEC`、`GetAdapterWattFromEC`、
  `TypeCAdaptorPrioritySupport/Switch`。
- **`GetPowerFromTypeC` 在任何可读源里都不存在**（40 系、30 系、10/20 系服务、50 系控制台全部 0 命中）。
  它是 50 系服务新增的方法，而那个二进制是混淆的 → **地址无法静态恢复**。
- `GetTypeCAdaptorPrioritySupport()` 在 40 系里是**硬编码 `return false`**
  （`MyEcCtrl.cs:254-258`）——所以「Type-C 适配器优先」在 40 系根本没开。
- 任何可读源里都没有 UCSI、PD 协商电压/电流、PDO 相关代码。

### 要定位它，需要一次真机 EC 差分（需要你配合）

方法（全程只读）：

1. 只插圆口适配器，取一份基线 EC 全量快照。
2. 拔掉圆口、只插 USB-C PD 充电器，取第二份。
3. 圆口 + PD 同时插，取第三份。
4. 用 USB-C 口给手机反向供电，取第四份。
5. 逐字节 diff 四份快照。随插拔状态一起变化的字节就是候选；
   其中**数值随充电器功率不同而变**的那个就是功率寄存器。

第 5 步能把候选收敛到个位数。理想情况下再换一个不同瓦数的 PD 充电器重复一次，
就能确认单位（常见是 W 或 0.1 W/字节）。

**这一步必须由你在机器旁操作插拔**，我这边没法远程插线。命令我会准备好，
你只需要按提示插拔并回车。

## 4. 已经可以做的只读展示

不依赖未知寄存器、现在就能如实显示的项：

| 显示项 | 来源 | 可靠性 |
|---|---|---|
| 供电方式（圆口 / Type-C / 电池） | `0x7CC` bit0 与 bits1-2 | 厂商同源判据，可靠 |
| 适配器额定瓦数 | `0x49F & 0x78` 查表 | **表外要显示「未知」**，不能像厂商那样默认 150 W |
| 电池实时功率（充/放电瓦数） | 已有 `BatteryRateReader`（IOCTL 0x29404C，mW） | 已在用 |
| CPU 封装功耗 | Windows `\Energy Meter(RAPL_Package0_PKG)\Power` | 已在用 |
| 显卡功率与功率墙 | NVML / NVAPI | 已在用 |

> 「PD 输入功率」与「Type-C 反向供电功率」在 §3 的差分实验完成前**不做**——
> 没有确定的寄存器就做，等于凭猜显示数字，属于伪功能。
