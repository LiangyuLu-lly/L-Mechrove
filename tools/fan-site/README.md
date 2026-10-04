# 统计站（stats.l-mechrevo.cn）

站点根 `/var/www/usage`，Caddy 对 `/api/data*` 一律返回 404（数据、密钥、日志、反馈都在 `api/data/` 下）。

| 文件 | 作用 |
|---|---|
| `heartbeat.php` | 匿名心跳：启动 / 每 5 分钟 / 退出各一次（`usage_telemetry=0` 可关）。beta21 起多带 `elevated`、`tier`、`port`、`os`、`upd`、`errors`，旧客户端不带时按缺省记录 |
| `log.php` | 启动与错误触发的脱敏日志，按批次确认并去重；`data/logs/<id>.txt` 保留最近 96 KB 历史，旁边的 JSON 保存最近 128 个批次回执 |
| `feedback.php` | 问题反馈（用户点「发送」才上传）：描述 + 可选联系方式 + 可选系统信息与脱敏日志。请求体 ≤ 256 KB，每 IP 每小时 ≤ 6 条，总数 ≤ 3000（超出删最旧），存 `data/feedback/<UTC 时间>-<随机>.json` |
| `usage.php` | 统计页与反馈列表（`?key=密钥`；`&format=json` 出 JSON；`&log=<id>` 看日志；`&feedback=<编号>` 看反馈） |
| `update_check.php` | 更新检查（读 `data/update.json`） |

密钥在 `api/data/usage_secret.txt`（一行随机串），不要提交、不要打印。

在线定义：最后一次心跳在 12 分钟内，且 event 不是 `stop`。

部署：先备份服务器上的同名文件（`*.bak-<时间>`），再覆盖；`data/feedback/` 由 `feedback.php` 首次写入时创建（0750，属主 www-data）。

0.290.6 客户端：错误保留独立内存缓冲，约 3 秒合并后落到 `telemetry-outbox/`；新批次上传间隔至少 15 秒，失败按 15/30/60/120/240/300 秒退避。待传记录跨启动保留，收到匹配的批次、字节数和 SHA-256 回执后删除；最多保留 32 个脱敏快照，超出时淘汰最旧记录并记录容量事件。崩溃处理器同步保存待传快照；断电或直接结束进程仍可能失去未落盘的最后几秒。告知前不采集待传记录，关闭匿名统计后取消传输并清理待传记录。

隔离验证：`python3 tools/fan-site/test_log.py`（需要 PHP 与 mbstring），启动临时本地接收接口，不读写线上用户数据。
