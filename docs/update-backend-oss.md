# 更新后端（阿里云 OSS 静态 JSON）

本项目的更新检查后端已迁移到**阿里云 OSS 上的单个静态 JSON 对象**。客户端把它当普通 JSON 读，不做任何服务端计算。

- 地域：华东1（杭州）
- Bucket：`lmechrevo`（ACL 公共读；「阻止公共访问」关闭）
- 对象 Key：`lmechrevo-oss/api/update_check.php`（**保留 `lmechrevo-oss/` 前缀，不要改**）
- 客户端必须配置的 Base URL：

  ```
  https://lmechrevo.oss-cn-hangzhou.aliyuncs.com/lmechrevo-oss
  ```

  客户端会在其后追加 `/api/update_check.php`，所以 Base 末尾**不要**再带 `/api`。

## 1. 客户端读取契约

客户端（`UpdateChecker.ParseResponse`）宽松解析，字段缺失按 `null` 处理，只有 `ok=false` 或结构不对才算整体失败：

| 字段 | 作用 |
| --- | --- |
| `ok` | 必须为 `true`（显式 `false` 会让客户端整段丢弃） |
| `data.current_version` | 客户端用来参与**交叉校验**的“当前版本” |
| `data.latest_version` | 要推送到的版本 |
| `data.update_available` | `true` |
| `data.download_url` | 安装包直链 |
| `data.sha256` | 安装包 SHA-256（**必须与 `download_url` 同时存在**才触发强校验） |
| `data.size` / `data.filename` / `data.notes` / `data.release_date` / `data.channel` | 展示与校验用 |
| `data.download_page` | 下载页（固定为站点页面） |

> **关键约束**：客户端用 **JSON 里的 `current_version`**（而不是自身版本）去比较 —— 只有当 `latest_version` 比 `current_version` 新时才会提示更新。因此 `current_version` 必须写成**比新版本低一档的版本**，否则更新永远不会出现。

> **强校验**：客户端只有在 `download_url` 与 `sha256` **同时非空**时才做哈希校验（`UpdateChecker.HasVerifiablePackage` / `UpdateInstaller`）。所以真实发版**务必带 `-PackagePath`**，否则退化成仅结构校验。

## 2. 每次发版：一行命令

```powershell
.\tools\publish-update-json.ps1 `
  -Version 0.289.0-beta16 `
  -DownloadUrl "https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v0.289.0-beta16/L-Mechrevo-0.289.0-beta16.exe" `
  -PackagePath .\release\L-Mechrevo-0.289.0-beta16.exe `
  -Notes "本版修复……"
```

- `-CurrentVersion` 默认会**读取线上 JSON** 得到“当前已发布版本”，一般无需手填；线上为空或要强制指定时显式传，例如 `-CurrentVersion 0.289.0-beta15`。
- 先演练、不上传：

  ```powershell
  .\tools\publish-update-json.ps1 -DryRun -Version 0.289.0-beta16 `
    -CurrentVersion 0.289.0-beta15 `
    -DownloadUrl "https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v0.289.0-beta16/L-Mechrevo-0.289.0-beta16.exe"
  ```

- 脚本上传后会自动匿名拉取线上对象（带 cache-bust 查询串）逐字段比对，任何不一致都会**显式报错并非零退出**。

脚本写入的请求头（固定）：

- `Content-Type: application/json; charset=utf-8`
- `Cache-Control: no-cache`（短缓存，避免客户端拿到旧文件）
- `Content-MD5`（正文 MD5，随签名一起校验）

## 3. 回滚

- **首选（当前现实）**：重新发布上一版 JSON —— 把 `-Version` 与 `-CurrentVersion` 都改回旧版本号再跑一次脚本；线上状态立即回到旧版。
- **对象版本控制（OSS Versioning）**：若 Bucket 已开启版本控制，可在控制台对同一 Key 选中历史版本执行「恢复/回滚」，无需重新上传。
  - 现状提示：本 Key 当前**无法读取版本配置**（见第 4 节的权限问题）；线上对象响应头 `x-oss-version-id: null`，说明该对象没有版本记录，**版本控制很可能尚未开启**。开启前请先在控制台确认。

## 4. 权限与安全

发布脚本对 OSS 的调用分两类：

1. **上传**（需要身份鉴权）：仅 `oss:PutObject`，资源 `acs:oss:*:*:lmechrevo/lmechrevo-oss/*`（见 `tools\oss-writer-policy.json`）。
   - 可进一步收紧到单对象：`acs:oss:*:*:lmechrevo/lmechrevo-oss/api/update_check.php`。
2. **读取/验证 + 自动推断当前版本**：走**匿名公有读**，不需要任何 RAM 权限（Bucket 已设公共读）。
   - 若将来把 Bucket 改成私有，则需额外授予 `oss:GetObject`（同样资源）。

`-CheckPrivilege` 是只读诊断开关，会额外调用 `oss:ListBuckets`；**不要**给生产发布用的 key 授予 `oss:ListBuckets`。

安全要求：

- AK 只存放在仓库**之外**的 `%USERPROFILE%\.lmechrevo\oss.ak`（两行：AccessKeyId、AccessKeySecret）。**绝不**提交、打印、写日志或放进安装包。
- 使用**最小权限 RAM 用户**，只授予上表的 `oss:PutObject`。
- 任何**曾出现在明文**（聊天、截图、日志、git 历史）的 AK 都应立即在控制台**轮换**：
  - 新建最小权限 RAM 用户 + AccessKey；
  - 覆盖 `.lmechrevo\oss.ak`；
  - 删除/禁用旧 AccessKey。
- 发布脚本本身不打印密钥：最多显示 AccessKeyId 前 8 位 + `****`。

## 5. 当前状态与已知阻塞（2026-09-15 核查）

- 线上对象当前内容（175 字节，未改动）：

  ```json
  {"ok":true,"data":{"current_version":"0.289.0-beta15","latest_version":"0.289.0-beta15","update_available":true,"download_page":"https://l-mechrevo.onismy.cn/download.html"}}
  ```

  `current_version == latest_version`，客户端交叉校验后按“无更新”处理，线上效果即“不推送更新”。

- **`oss.ak` 当前无法写入**：对该 Key 与测试 Key 的 `PutObject` 均返回
  `403 AccessDenied`（`NoPermissionType=ImplicitDeny`、`AuthAction=oss:PutObject`、`AuthPrincipalType=SubUser`）；
  对 `?acl` / `?bucketInfo` 返回 `The bucket you access does not belong to you`。
  即：该 AK 能通过签名校验，但**没有被授权写这个 Bucket**（疑为其所属账号与本 Bucket 归属账号不一致，或尚未绑定含 `oss:PutObject` 的 RAM 策略）。
  解决需在阿里云控制台为对应 RAM 用户绑定 `tools\oss-writer-policy.json`（或确认 AK 与 Bucket 是否同一账号）。在此之前，脚本无法完成真实上传。
