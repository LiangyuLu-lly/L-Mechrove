<?php
declare(strict_types=1);
header('X-Content-Type-Options: nosniff');

$secretFile = __DIR__ . '/data/usage_secret.txt';
$want = is_file($secretFile) ? trim((string)file_get_contents($secretFile)) : '';
$got = (string)($_GET['key'] ?? '');
if ($want === '' || !hash_equals($want, $got)) {
    http_response_code(403);
    header('Content-Type: text/plain; charset=utf-8');
    echo "forbidden\n";
    exit;
}

const ONLINE_SECONDS = 720;

$path = __DIR__ . '/data/usage.json';
$store = ['clients' => []];
if (is_file($path)) {
    $decoded = json_decode((string)file_get_contents($path), true);
    if (is_array($decoded)) $store = $decoded;
}
$clients = is_array($store['clients'] ?? null) ? $store['clients'] : [];
$now = time();

function is_online(array $row, int $now): bool {
    if (($row['event'] ?? '') === 'stop') return false;
    return ($now - (int)($row['last'] ?? 0)) <= ONLINE_SECONDS;
}

$online = [];
$offline = [];
foreach ($clients as $row) {
    if (!is_array($row)) continue;
    if (is_online($row, $now)) $online[] = $row;
    else $offline[] = $row;
}

usort($online, fn($a, $b) => ((int)($b['last'] ?? 0)) <=> ((int)($a['last'] ?? 0)));
usort($offline, fn($a, $b) => ((int)($b['last'] ?? 0)) <=> ((int)($a['last'] ?? 0)));

$day = $now - 86400;
$active24 = 0;
foreach ($clients as $row) {
    if (is_array($row) && (int)($row['last'] ?? 0) >= $day) $active24++;
}

$byModel = [];
$byGpu = [];
$byVer = [];
foreach ($online as $row) {
    $m = $row['model'] !== '' ? $row['model'] : ($row['project'] !== '' ? $row['project'] : '(未知)');
    $byModel[$m] = ($byModel[$m] ?? 0) + 1;
    $g = $row['gpu'] !== '' ? $row['gpu'] : '?';
    $byGpu[$g] = ($byGpu[$g] ?? 0) + 1;
    $v = $row['ver'] !== '' ? $row['ver'] : '?';
    $byVer[$v] = ($byVer[$v] ?? 0) + 1;
}
arsort($byModel);
arsort($byGpu);
arsort($byVer);

$logId = preg_replace('/[^A-Za-z0-9]/', '', (string)($_GET['log'] ?? '')) ?? '';
if ($logId !== '' && strlen($logId) >= 8) {
    $logPath = __DIR__ . '/data/logs/' . $logId . '.txt';
    header('Content-Type: text/plain; charset=utf-8');
    if (is_file($logPath)) echo (string)file_get_contents($logPath);
    else echo "(no log)\n";
    exit;
}

if (($_GET['format'] ?? '') === 'json') {
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode([
        'ok' => true,
        'now' => $now,
        'online' => count($online),
        'installs' => count($clients),
        'active_24h' => $active24,
        'by_model' => $byModel,
        'by_gpu' => $byGpu,
        'by_ver' => $byVer,
        'clients' => array_values(array_merge($online, $offline)),
    ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

header('Content-Type: text/html; charset=utf-8');
function h($s): string { return htmlspecialchars((string)$s, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'); }
function when(int $ts): string { return $ts > 0 ? date('Y-m-d H:i:s', $ts) : '—'; }

function has_log(array $r): bool {
    $id = preg_replace('/[^A-Za-z0-9]/', '', (string)($r['id'] ?? '')) ?? '';
    return strlen($id) >= 8 && is_file(__DIR__ . '/data/logs/' . $id . '.txt');
}

function rows_html(array $rows, bool $on, string $key): string {
    $html = '';
    foreach ($rows as $r) {
        $status = $on ? '在线' : '离线';
        $gcu = !empty($r['gcu']) ? 'GCU 已连' : 'GCU 未连';
        $id = preg_replace('/[^A-Za-z0-9]/', '', (string)($r['id'] ?? '')) ?? '';
        $html .= '<tr>';
        $html .= '<td>' . h($status) . '</td>';
        $html .= '<td>' . h($r['model'] ?? '') . '</td>';
        $html .= '<td>' . h($r['project'] ?? '') . '</td>';
        $html .= '<td>' . h($r['gpu'] ?? '') . '</td>';
        $html .= '<td>' . h($r['gpu_name'] ?? '') . '</td>';
        $html .= '<td>' . h($r['cpu'] ?? '') . '</td>';
        $html .= '<td>' . h($r['ver'] ?? '') . '</td>';
        $html .= '<td>' . h($gcu) . '</td>';
        $html .= '<td>' . h($r['ip'] ?? '') . '</td>';
        $html .= '<td>' . h(when((int)($r['last'] ?? 0))) . '</td>';
        if (has_log($r)) {
            $html .= '<td><a href="?key=' . rawurlencode($key) . '&amp;log=' . rawurlencode($id) . '">查看</a></td>';
        } else {
            $html .= '<td>—</td>';
        }
        $html .= '</tr>';
    }
    return $html;
}
?>
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>L-Mechrevo 使用统计</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
body{font:14px/1.45 system-ui,sans-serif;margin:24px;background:#111;color:#eee}
h1{font-size:20px;font-weight:600}
.cards{display:flex;gap:12px;flex-wrap:wrap;margin:16px 0}
.card{background:#1c1c1c;border:1px solid #333;border-radius:8px;padding:12px 16px;min-width:120px}
.card b{display:block;font-size:28px}
table{border-collapse:collapse;width:100%;margin-top:16px}
th,td{border-bottom:1px solid #333;padding:6px 8px;text-align:left}
th{color:#aaa;font-weight:500}
.muted{color:#888}
</style>
</head>
<body>
<h1>L-Mechrevo 使用统计</h1>
<p class="muted">在线 = 最近 12 分钟有心跳且未发 stop。刷新本页即可。</p>
<div class="cards">
  <div class="card"><span>当前在线</span><b><?= count($online) ?></b></div>
  <div class="card"><span>24 小时活跃</span><b><?= $active24 ?></b></div>
  <div class="card"><span>累计安装</span><b><?= count($clients) ?></b></div>
</div>
<p>在线机型：<?php foreach ($byModel as $k=>$n) echo h($k).' '.$n.'　'; ?></p>
<p>在线显卡代际：<?php foreach ($byGpu as $k=>$n) echo h($k).' '.$n.'　'; ?></p>
<p>在线版本：<?php foreach ($byVer as $k=>$n) echo h($k).' '.$n.'　'; ?></p>
<table>
<thead><tr><th>状态</th><th>型号</th><th>项目代号</th><th>代际</th><th>GPU</th><th>CPU</th><th>版本</th><th>GCU</th><th>IP</th><th>最后心跳</th><th>日志</th></tr></thead>
<tbody>
<?= rows_html($online, true, $got) ?>
<?= rows_html($offline, false, $got) ?>
</tbody>
</table>
</body>
</html>
