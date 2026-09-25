<?php
declare(strict_types=1);

function applyCorsHeaders(): void
{
    $origin = $_SERVER['HTTP_ORIGIN'] ?? '';
    if ($origin === 'https://l-mechrevo.cn') {
        header('Access-Control-Allow-Origin: https://l-mechrevo.cn');
        header('Vary: Origin');
    }
}

applyCorsHeaders();

if (($_SERVER['REQUEST_METHOD'] ?? '') === 'OPTIONS') {
    http_response_code(204);
    exit;
}

header('Content-Type: application/json; charset=utf-8');
header('X-Content-Type-Options: nosniff');
header('Cache-Control: no-cache');

$path = __DIR__ . '/data/update.json';
if (!is_file($path)) {
    echo json_encode([
        'ok' => true,
        'data' => [
            'update_available' => false,
            'channel' => 'beta',
            'is_latest' => true,
        ],
    ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

$raw = file_get_contents($path);
if ($raw === false || $raw === '') {
    http_response_code(500);
    echo json_encode(['ok' => false]);
    exit;
}

echo $raw;
