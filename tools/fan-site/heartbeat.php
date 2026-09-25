<?php
declare(strict_types=1);
header('Content-Type: application/json; charset=utf-8');
header('X-Content-Type-Options: nosniff');

if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
    http_response_code(405);
    echo json_encode(['ok' => false, 'error' => 'POST only']);
    exit;
}

$raw = file_get_contents('php://input');
if ($raw === false || strlen($raw) < 8 || strlen($raw) > 4096) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'bad body']);
    exit;
}

$in = json_decode($raw, true);
if (!is_array($in)) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'bad json']);
    exit;
}

function clip($v, int $max = 80): string {
    $s = trim((string)$v);
    $s = preg_replace('/[^A-Za-z0-9 ._\-+#()]/', '', $s) ?? '';
    if (strlen($s) > $max) $s = substr($s, 0, $max);
    return $s;
}

$id = clip($in['id'] ?? '', 64);
if (strlen($id) < 8) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'bad id']);
    exit;
}

$event = clip($in['event'] ?? 'beat', 8);
if (!in_array($event, ['start', 'beat', 'stop'], true)) $event = 'beat';

$now = time();
$row = [
    'id' => $id,
    'ver' => clip($in['ver'] ?? ''),
    'event' => $event,
    'model' => clip($in['model'] ?? ''),
    'project' => clip($in['project'] ?? ''),
    'bios' => clip($in['bios'] ?? ''),
    'gpu' => clip($in['gpu'] ?? ''),
    'gpu_name' => clip($in['gpu_name'] ?? ''),
    'cpu' => clip($in['cpu'] ?? ''),
    'gcu' => !empty($in['gcu']),
    'keyboard' => !empty($in['keyboard']),
    'lightbar' => !empty($in['lightbar']),
    'last' => $now,
    'ip' => clip($_SERVER['REMOTE_ADDR'] ?? '', 45),
];

$dir = __DIR__ . '/data';
if (!is_dir($dir) && !mkdir($dir, 0750, true)) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'data dir']);
    exit;
}

$path = $dir . '/usage.json';
$fh = fopen($path, 'c+');
if ($fh === false) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'open']);
    exit;
}

if (!flock($fh, LOCK_EX)) {
    fclose($fh);
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'lock']);
    exit;
}

$contents = stream_get_contents($fh);
$store = json_decode($contents ?: '{}', true);
if (!is_array($store)) $store = [];
if (!isset($store['clients']) || !is_array($store['clients'])) $store['clients'] = [];

$prev = $store['clients'][$id] ?? [];
$row['first'] = isset($prev['first']) ? (int)$prev['first'] : $now;
$store['clients'][$id] = $row;
$store['updated'] = $now;

rewind($fh);
ftruncate($fh, 0);
fwrite($fh, json_encode($store, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));
fflush($fh);
flock($fh, LOCK_UN);
fclose($fh);

echo json_encode(['ok' => true]);
