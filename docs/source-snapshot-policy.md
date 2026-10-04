# L-Mechrevo 对应源码快照政策

本文档规定 L-Mechrevo 对应源码（Corresponding Source）快照仓库的使用规则。
适用于本项目全部发布版本，任何发布都必须遵守。

## 1. 目的

本仓库为每一个对外发布的 L-Mechrevo 版本留存不可变的"对应源码"快照，
用来履行随包提供的源码承诺。2026-10-05 业主授权全量推送最新版；
0.290.6 起对应源码标签与归档随 GitHub Release 公开，仍保留本地归档。

## 2. 为什么每个发布版本都必须有快照

随每个发布包分发的 `THIRD_PARTY_NOTICES.txt` 中包含一段 GPLv3 §6(b)
书面报价（Written Offer），其核心内容是：

- L-Mechrevo 包含来自 G-Helper（GPL-3.0-only）的衍生代码；
- 自用户收到二进制副本之日起 **至少三年内**，任何收到副本的人都可以向
  分发者索取 **其所收到的那一个确切版本** 的完整、机器可读的对应源码；
- 收费不超过实际分发成本（介质与运费），或免费提供下载；
- 索取方式：发邮件到 `liangyulu781@gmail.com`，并说明所收到的确切版本号
  （应用内显示或发布压缩包名称）。

这意味着承诺的对象是"用户实际收到的那一个版本"，而不是"最新版"或"大概
差不多的版本"。如果某个版本在发布时没有留下快照，承诺将无法兑现——这就是
每个发布版本都必须先有快照的根本原因。

## 3. 源码发布与数据边界

- 发布到业主指定的 `LiangyuLu-lly/L-Mechrevo` 仓库，推送须有业主明确授权。
- 只发布源码、文档、测试与构建输入；服务器凭据、用户配置、诊断包和本机运行日志不得提交。
- 对应源码归档随 Release 提供；第 5 节的源码索取方式继续保留。

## 4. 发布前流程（每个版本）

1. 确认工作区干净：`git status --short` 无任何输出。
2. 用发布版本号创建快照：

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\snapshot-source.ps1 -Version <发布版本号>
   ```

   例如 `-Version 5.56.60.26`。脚本会创建带注释的标签 `v<版本号>` 并导出
   `artifacts\source-snapshots\L-Mechrevo-<版本号>-source.zip`。
3. **先确认快照成功（标签与 zip 均已生成），再打包发布二进制。**
   绝不允许"先发布、后补快照"。
4. 记录脚本输出的路径、大小和 SHA256，随发布记录一并留存。
5. 快照 zip 由分发者长期保存（与源码仓库分开备份），保存期限不短于
   GPLv3 §6(b) 承诺的三年。

## 5. 如何响应源码索取请求

当有人通过 `liangyulu781@gmail.com` 索取某版本的对应源码时：

1. 核对对方说明的版本号（例如 `5.56.60.26`）。
2. 在本地仓库确认对应标签存在：

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\snapshot-source.ps1 -List
   ```

3. 找到该版本的快照归档：
   `artifacts\source-snapshots\L-Mechrevo-<版本号>-source.zip`。
4. 将 zip 通过邮件附件或下载链接直接交付给索取人，不收取超过实际分发成本
   的费用（下载方式免费）。
5. 交付后记录：请求人、版本号、交付日期、归档的 SHA256。

注意：标签 `v<版本号>` 是该版本源码的唯一权威标识。归档 zip 只是标签内容
的导出结果，不能脱离标签单独维护。

## 6. 禁止事项

- **绝不** 在没有业主明确授权时推送；推送前核查工作树与可达历史中的凭据。
- **绝不** 重写、删除或移动已有标签（`git tag -d`、`git tag -f`、
  `git push --force` 等）。标签是"用户收到的那一版"的凭证，改动标签等于
  伪造对应源码。
- **绝不** 在没有匹配快照（标签 + 归档）的情况下发布任何二进制版本。
- **绝不** 在快照归档中混入发布二进制或其它无关内容；归档必须由
  `git archive` 从标签导出，保证与源码树一致。

## 7. 快照工具用法

脚本位置：`tools\snapshot-source.ps1`（PowerShell 5.1，UTF-8 带 BOM）。

创建快照（要求工作区干净，拒绝覆盖已存在的标签）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\snapshot-source.ps1 -Version 1.2.3
```

列出已有快照（标签与归档）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\snapshot-source.ps1 -List
```

脚本行为：

- 工作区不干净时打印 `git status --short` 并拒绝运行；
- 创建带注释标签 `v<Version>`；
- 导出 `artifacts\source-snapshots\L-Mechrevo-<Version>-source.zip`；
- 打印归档路径、字节数、SHA256。

## 8. 术语

- **对应源码（Corresponding Source）**：按 GPLv3 第 1 条定义，指生成、安装、
  运行二进制所必需的源码，以及构建脚本、安装信息等。
- **快照**：某个版本标签导出的源码归档（zip），与标签一一对应。
- **版本号**：随发布包对外公布的版本标识，必须与 `-Version` 参数及标签名
  完全一致。
