<?php
declare(strict_types=1);
header('Content-Type: application/json; charset=utf-8');
header('X-Content-Type-Options: nosniff');
// beta21：客户端改为按 UTF-8 字节截到 64 KB、中文不再转义成 \uXXXX；上限放宽到 192 KB 请求体 / 96 KB 日志，
// 旧客户端（beta20 及以前，带中文的日志常被 40 KB 上限整条拒收）照样兼容。
const MAX_BODY_BYTES = 196608;
const MAX_LOG_BYTES = 98304;
function log_tail(string $text): string {
    if (strlen($text) <= MAX_LOG_BYTES) return $text;
    $tail = mb_strcut($text, strlen($text) - MAX_LOG_BYTES, null, 'UTF-8');
    while (strlen($tail) > MAX_LOG_BYTES) $tail = mb_substr($tail, 1, null, 'UTF-8');
    return $tail;
}
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
if (!is_string($in['id'] ?? null) || !is_string($in['log'] ?? null) ||
    !is_string($in['ver'] ?? '') || !is_string($in['batch'] ?? '')) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'bad fields']);
    exit;
}
$id = clip_id($in['id']);
if (strlen($id) < 8) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'bad id']);
    exit;
}
$batch = $in['batch'] ?? '';
if ($batch !== '' && !preg_match('/^[a-f0-9]{32}$/D', $batch)) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'bad batch']);
    exit;
}
$log = mb_convert_encoding($in['log'], 'UTF-8', 'UTF-8');
if ($batch !== '' && strlen($log) > MAX_LOG_BYTES) {
    http_response_code(413);
    echo json_encode(['ok' => false, 'error' => 'log too large']);
    exit;
}
$log = log_tail($log);
$dir = __DIR__ . '/data/logs';
if (!is_dir($dir) && !@mkdir($dir, 0750, true) && !is_dir($dir)) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'log dir']);
    exit;
}
$path = $dir . '/' . $id . '.txt';
$lock = fopen($dir . '/' . $id . '.lock', 'c');
if ($lock === false || !flock($lock, LOCK_EX)) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'lock']);
    exit;
}
$metaPath = $dir . '/' . $id . '.json';
$metaRaw = is_file($metaPath) ? file_get_contents($metaPath) : '{}';
$meta = $metaRaw === false ? null : json_decode($metaRaw, true);
if (!is_array($meta)) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'metadata read']);
    flock($lock, LOCK_UN);
    fclose($lock);
    exit;
}
$receipts = is_array($meta['receipts'] ?? null) ? $meta['receipts'] : [];
$hash = hash('sha256', $log);
if ($batch !== '' && isset($receipts[$batch])) {
    $receipt = $receipts[$batch];
    if (($receipt['sha256'] ?? '') !== $hash) {
        http_response_code(409);
        echo json_encode(['ok' => false, 'error' => 'batch conflict']);
    } else {
        echo json_encode(['ok' => true, 'batch' => $batch, 'bytes' => $receipt['bytes'], 'sha256' => $hash, 'duplicate' => true]);
    }
    flock($lock, LOCK_UN);
    fclose($lock);
    exit;
}
$previous = is_file($path) ? file_get_contents($path) : '';
if ($previous === false) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'history read']);
    flock($lock, LOCK_UN);
    fclose($lock);
    exit;
}
$previous = log_tail($previous);
$version = preg_replace('/[^A-Za-z0-9._-]/', '', substr($in['ver'] ?? '', 0, 80));
$history = $previous . "\n--- received " . gmdate('c') . ' version=' . $version . ' batch=' . $batch . " ---\n" . $log;
$history = log_tail($history);
if ($batch !== '') $receipts[$batch] = ['bytes' => strlen($log), 'sha256' => $hash];
$meta = ['last' => time(), 'ver' => $version, 'batch' => $batch, 'bytes' => strlen($log),
    'sha256' => $hash, 'receipts' => array_slice($receipts, -128, null, true)];
// Keep the previous files intact on write failure; clients retry the same batch after a lost receipt.
function atomic_write(string $path, string $content): bool {
    $temporary = tempnam(dirname($path), '.log-');
    if ($temporary === false) return false;
    $stream = fopen($temporary, 'wb');
    if ($stream === false) { unlink($temporary); return false; }
    $written = fwrite($stream, $content);
    $flushed = $written === strlen($content) && fflush($stream) && fsync($stream);
    fclose($stream);
    $ok = $flushed && rename($temporary, $path);
    if (!$ok && is_file($temporary)) unlink($temporary);
    return $ok;
}
$ok = atomic_write($path, $history) && atomic_write($metaPath, json_encode($meta, JSON_UNESCAPED_SLASHES));
flock($lock, LOCK_UN);
fclose($lock);
if (!$ok) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'write']);
    exit;
}
echo json_encode(['ok' => true, 'batch' => $batch, 'bytes' => strlen($log), 'sha256' => $hash]);
