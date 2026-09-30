# 统计站（stats.l-mechrevo.cn）

站点根 `/var/www/usage`，Caddy 对 `/api/data*` 一律返回 404（数据、密钥、日志、反馈都在 `api/data/` 下）。

| 文件 | 作用 |
|---|---|
| `heartbeat.php` | 匿名心跳：启动 / 每 5 分钟 / 退出各一次（`usage_telemetry=0` 可关）。beta21 起多带 `elevated`、`tier`、`port`、`os`、`upd`、`errors`，旧客户端不带时按缺省记录 |
| `log.php` | 脱敏日志尾（启动时一次；运行中出现新的失败行时最多每 30 分钟一次；覆盖写 `data/logs/<id>.txt`） |
| `feedback.php` | 问题反馈（用户点「发送」才上传）：描述 + 可选联系方式 + 可选系统信息与脱敏日志。请求体 ≤ 256 KB，每 IP 每小时 ≤ 6 条，总数 ≤ 3000（超出删最旧），存 `data/feedback/<UTC 时间>-<随机>.json` |
| `usage.php` | 统计页与反馈列表（`?key=密钥`；`&format=json` 出 JSON；`&log=<id>` 看日志；`&feedback=<编号>` 看反馈） |
| `update_check.php` | 更新检查（读 `data/update.json`） |

密钥在 `api/data/usage_secret.txt`（一行随机串），不要提交、不要打印。

在线定义：最后一次心跳在 12 分钟内，且 event 不是 `stop`。

部署：先备份服务器上的同名文件（`*.bak-<时间>`），再覆盖；`data/feedback/` 由 `feedback.php` 首次写入时创建（0750，属主 www-data）。
