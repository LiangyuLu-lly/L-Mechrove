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
const FEEDBACK_LIST = 50;
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
$byTier = [];
$withErrors = 0;
foreach ($online as $row) {
    $m = ($row['model'] ?? '') !== '' ? $row['model'] : (($row['project'] ?? '') !== '' ? $row['project'] : '(未知)');
    $byModel[$m] = ($byModel[$m] ?? 0) + 1;
    $g = ($row['gpu'] ?? '') !== '' ? $row['gpu'] : '?';
    $byGpu[$g] = ($byGpu[$g] ?? 0) + 1;
    $v = ($row['ver'] ?? '') !== '' ? $row['ver'] : '?';
    $byVer[$v] = ($byVer[$v] ?? 0) + 1;
    $t = ($row['tier'] ?? '') !== '' ? $row['tier'] : '?';
    $byTier[$t] = ($byTier[$t] ?? 0) + 1;
    if ((int)($row['errors'] ?? 0) > 0) $withErrors++;
}
arsort($byModel);
arsort($byGpu);
arsort($byVer);
arsort($byTier);

$feedbackDir = __DIR__ . '/data/feedback';
function feedback_files(string $dir): array {
    $files = glob($dir . '/*.json') ?: [];
    rsort($files, SORT_STRING);   // 文件名以 UTC 时间开头：倒序 = 最新在前
    return $files;
}

$logId = preg_replace('/[^A-Za-z0-9]/', '', (string)($_GET['log'] ?? '')) ?? '';
if ($logId !== '' && strlen($logId) >= 8) {
    $logPath = __DIR__ . '/data/logs/' . $logId . '.txt';
    header('Content-Type: text/plain; charset=utf-8');
    if (is_file($logPath)) echo (string)file_get_contents($logPath);
    else echo "(no log)\n";
    exit;
}

$ticket = preg_replace('/[^0-9a-f\-]/', '', (string)($_GET['feedback'] ?? '')) ?? '';
if ($ticket !== '') {
    header('Content-Type: text/plain; charset=utf-8');
    $file = $feedbackDir . '/' . $ticket . '.json';
    $item = is_file($file) ? json_decode((string)file_get_contents($file), true) : null;
    if (!is_array($item)) { echo "(no feedback)\n"; exit; }
    $f = fn(string $k): string => (string)($item[$k] ?? '');
    echo "编号: " . $f('ticket') . "\n";
    echo "时间: " . date('Y-m-d H:i:s', (int)($item['time'] ?? 0)) . "\n";
    echo "安装 ID: " . $f('id') . "  IP: " . $f('ip') . "\n";
    echo "版本: " . $f('ver') . "  机型: " . $f('model') . "  项目: " . $f('project') . "  代际: " . $f('gpu') . "\n";
    echo "联系方式: " . ($f('contact') !== '' ? $f('contact') : '（未填）') . "\n\n";
    echo "【问题描述】\n" . $f('message') . "\n\n";
    echo "【系统信息】\n" . ($f('sysinfo') !== '' ? $f('sysinfo') : '（未附带）') . "\n\n";
    echo "【日志】\n" . ($f('log') !== '' ? $f('log') : '（未附带）') . "\n";
    exit;
}

$feedback = [];
foreach (array_slice(feedback_files($feedbackDir), 0, FEEDBACK_LIST) as $file) {
    $item = json_decode((string)file_get_contents($file), true);
    if (!is_array($item)) continue;
    $feedback[] = [
        'ticket' => (string)($item['ticket'] ?? ''),
        'time' => (int)($item['time'] ?? 0),
        'ver' => (string)($item['ver'] ?? ''),
        'model' => (string)($item['model'] ?? ''),
        'contact' => (string)($item['contact'] ?? ''),
        'summary' => mb_substr(str_replace("\n", ' ', (string)($item['message'] ?? '')), 0, 80, 'UTF-8'),
    ];
}
$feedbackTotal = count(feedback_files($feedbackDir));

if (($_GET['format'] ?? '') === 'json') {
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode([
        'ok' => true,
        'now' => $now,
        'online' => count($online),
        'installs' => count($clients),
        'active_24h' => $active24,
        'online_with_errors' => $withErrors,
        'by_model' => $byModel,
        'by_gpu' => $byGpu,
        'by_ver' => $byVer,
        'by_tier' => $byTier,
        'feedback_total' => $feedbackTotal,
        'feedback' => $feedback,
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
        $tier = (string)($r['tier'] ?? '');
        $port = (int)($r['port'] ?? 0);
        $service = $tier === '' ? '—' : $tier . ($port > 0 ? ':' . $port : '');
        $errors = (int)($r['errors'] ?? 0);
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
        $html .= '<td>' . h($service) . '</td>';
        $html .= '<td>' . (array_key_exists('elevated', $r) ? (!empty($r['elevated']) ? '是' : '否') : '—') . '</td>';
        $html .= '<td>' . h(($r['os'] ?? '') !== '' ? $r['os'] : '—') . '</td>';
        $html .= '<td>' . h(($r['upd'] ?? '') !== '' ? $r['upd'] : '—') . '</td>';
        $html .= '<td' . ($errors > 0 ? ' class="warn"' : '') . '>' . $errors . '</td>';
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
function feedback_html(array $items, string $key): string {
    $html = '';
    foreach ($items as $f) {
        $html .= '<tr>';
        $html .= '<td>' . h(when($f['time'])) . '</td>';
        $html .= '<td>' . h($f['ver']) . '</td>';
        $html .= '<td>' . h($f['model']) . '</td>';
        $html .= '<td>' . h($f['contact'] !== '' ? $f['contact'] : '—') . '</td>';
        $html .= '<td>' . h($f['summary']) . '</td>';
        $html .= '<td><a href="?key=' . rawurlencode($key) . '&amp;feedback=' . rawurlencode($f['ticket']) . '">查看</a></td>';
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
<meta name="robots" content="noindex">
<style>
body{font:14px/1.45 system-ui,sans-serif;margin:24px;background:#111;color:#eee}
h1{font-size:20px;font-weight:600}
h2{font-size:16px;font-weight:600;margin-top:28px}
.cards{display:flex;gap:12px;flex-wrap:wrap;margin:16px 0}
.card{background:#1c1c1c;border:1px solid #333;border-radius:8px;padding:12px 16px;min-width:120px}
.card b{display:block;font-size:28px}
table{border-collapse:collapse;width:100%;margin-top:16px}
th,td{border-bottom:1px solid #333;padding:6px 8px;text-align:left}
th{color:#aaa;font-weight:500}
.muted{color:#888}
.warn{color:#f5a524}
a{color:#6aa9ff}
</style>
</head>
<body>
<h1>L-Mechrevo 使用统计</h1>
<p class="muted">在线 = 最近 12 分钟有心跳且未发 stop。刷新本页即可。错误数 = 该次运行里记下的失败行数（进程重启归零）。</p>
<div class="cards">
  <div class="card"><span>当前在线</span><b><?= count($online) ?></b></div>
  <div class="card"><span>24 小时活跃</span><b><?= $active24 ?></b></div>
  <div class="card"><span>累计安装</span><b><?= count($clients) ?></b></div>
  <div class="card"><span>在线且有错误</span><b><?= $withErrors ?></b></div>
  <div class="card"><span>问题反馈</span><b><?= $feedbackTotal ?></b></div>
</div>
<p>在线机型：<?php foreach ($byModel as $k=>$n) echo h($k).' '.$n.'　'; ?></p>
<p>在线显卡代际：<?php foreach ($byGpu as $k=>$n) echo h($k).' '.$n.'　'; ?></p>
<p>在线服务档位：<?php foreach ($byTier as $k=>$n) echo h($k).' '.$n.'　'; ?></p>
<p>在线版本：<?php foreach ($byVer as $k=>$n) echo h($k).' '.$n.'　'; ?></p>
<h2>问题反馈（最近 <?= FEEDBACK_LIST ?> 条）</h2>
<table>
<thead><tr><th>时间</th><th>版本</th><th>型号</th><th>联系方式</th><th>摘要</th><th>详情</th></tr></thead>
<tbody>
<?= feedback_html($feedback, $got) ?>
</tbody>
</table>
<h2>客户端</h2>
<table>
<thead><tr><th>状态</th><th>型号</th><th>项目代号</th><th>代际</th><th>GPU</th><th>CPU</th><th>版本</th><th>GCU</th><th>服务</th><th>管理员</th><th>系统</th><th>更新</th><th>错误</th><th>IP</th><th>最后心跳</th><th>日志</th></tr></thead>
<tbody>
<?= rows_html($online, true, $got) ?>
<?= rows_html($offline, false, $got) ?>
</tbody>
</table>
</body>
</html>
