<?php
declare(strict_types=1);
header('Content-Type: application/json; charset=utf-8');
header('X-Content-Type-Options: nosniff');
// beta21：客户端改为按 UTF-8 字节截到 64 KB、中文不再转义成 \uXXXX；上限放宽到 192 KB 请求体 / 96 KB 日志，
// 旧客户端（beta20 及以前，带中文的日志常被 40 KB 上限整条拒收）照样兼容。
const MAX_BODY_BYTES = 196608;
const MAX_LOG_BYTES = 98304;
if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
    http_response_code(405);
    echo json_encode(['ok' => false, 'error' => 'POST only']);
    exit;
}
$raw = file_get_contents('php://input', false, null, 0, MAX_BODY_BYTES + 1);
if ($raw === false || strlen($raw) < 8 || strlen($raw) > MAX_BODY_BYTES) {
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
function clip_id($v): string {
    $s = preg_replace('/[^A-Za-z0-9]/', '', (string)$v) ?? '';
    if (strlen($s) > 64) $s = substr($s, 0, 64);
    return $s;
}
$id = clip_id($in['id'] ?? '');
if (strlen($id) < 8) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'bad id']);
    exit;
}
$log = mb_convert_encoding((string)($in['log'] ?? ''), 'UTF-8', 'UTF-8');
if (strlen($log) > MAX_LOG_BYTES) $log = mb_strcut($log, strlen($log) - MAX_LOG_BYTES, MAX_LOG_BYTES, 'UTF-8');
$dir = __DIR__ . '/data/logs';
if (!is_dir($dir) && !mkdir($dir, 0750, true)) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'log dir']);
    exit;
}
$path = $dir . '/' . $id . '.txt';
$ok = file_put_contents($path, $log, LOCK_EX);
if ($ok === false) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'write']);
    exit;
}
echo json_encode(['ok' => true, 'bytes' => $ok]);
