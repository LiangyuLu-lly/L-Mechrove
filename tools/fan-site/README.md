# 粉丝站使用统计

客户端每 5 分钟 POST `https://l-mechrevo.onismy.cn/api/heartbeat.php`。

## 部署

1. 把 `heartbeat.php` 和 `usage.php` 放到站点 `api/` 目录（与 `update_check.php` 同级）。
2. 建目录 `api/data/`，不可 web 列出。
3. 写入密钥：

```
api/data/usage_secret.txt
```

内容一行随机串。查看统计：

```
https://l-mechrevo.onismy.cn/api/usage.php?key=你的密钥
https://l-mechrevo.onismy.cn/api/usage.php?key=你的密钥&format=json
```

4. `data/usage.json` 由心跳自动创建。把 `data/` 从备份以外的公开同步里排除。

在线定义：最后一次心跳在 12 分钟内，且 event 不是 `stop`。
