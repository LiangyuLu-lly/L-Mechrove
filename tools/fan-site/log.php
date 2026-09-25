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
if ($raw === false || strlen($raw) < 8 || strlen($raw) > 40000) {
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

$log = (string)($in['log'] ?? '');
if (strlen($log) > 24576) $log = substr($log, -24576);

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
