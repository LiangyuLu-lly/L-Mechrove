<?php
// 问题反馈：客户端「问题反馈」窗口在用户点「发送」时 POST 一次（用户描述 + 可选联系方式 +
// 可选的系统信息与脱敏日志）。没有账号体系，所以防滥用靠：请求体上限、字段上限、每 IP 每小时条数上限、
// 总条数上限（超出删最旧）。存到 data/feedback/（Caddy 对 /api/data* 一律 404）。
declare(strict_types=1);
header('Content-Type: application/json; charset=utf-8');
header('X-Content-Type-Options: nosniff');

const MAX_BODY_BYTES = 262144;       // 256 KB（日志 128 KB + 系统信息 32 KB + JSON 转义余量）
const MAX_MESSAGE_CHARS = 4000;
const MAX_CONTACT_CHARS = 120;
const MAX_SYSINFO_BYTES = 32768;
const MAX_LOG_BYTES = 131072;
const MAX_PER_IP_PER_HOUR = 6;
const MAX_FILES = 3000;

function fail(int $code, string $error): void {
    http_response_code($code);
    echo json_encode(['ok' => false, 'error' => $error]);
    exit;
}

function clip_token($v, int $max = 80): string {
    $s = trim((string)$v);
    $s = preg_replace('/[^A-Za-z0-9 ._\-+#()]/', '', $s) ?? '';
    if (strlen($s) > $max) $s = substr($s, 0, $max);
    return $s;
}

/** UTF-8 文本清洗：非法字节丢弃，去掉除换行/制表以外的控制字符。 */
function clean_text($v): string {
    $s = mb_convert_encoding((string)$v, 'UTF-8', 'UTF-8');
    $s = str_replace("\r\n", "\n", $s);
    return preg_replace('/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/u', '', $s) ?? '';
}

function head_chars(string $s, int $max): string {
    return mb_strlen($s, 'UTF-8') > $max ? mb_substr($s, 0, $max, 'UTF-8') : $s;
}

/** 保留末尾 $maxBytes 字节（日志看最近的部分），按字符边界切。 */
function tail_bytes(string $s, int $maxBytes): string {
    if (strlen($s) <= $maxBytes) return $s;
    return mb_strcut($s, strlen($s) - $maxBytes, $maxBytes, 'UTF-8');
}

function head_bytes(string $s, int $maxBytes): string {
    return strlen($s) <= $maxBytes ? $s : mb_strcut($s, 0, $maxBytes, 'UTF-8');
}

if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') fail(405, 'POST only');
$length = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
if ($length > MAX_BODY_BYTES) fail(413, 'too large');
$raw = file_get_contents('php://input', false, null, 0, MAX_BODY_BYTES + 1);
if ($raw === false || strlen($raw) < 8 || strlen($raw) > MAX_BODY_BYTES) fail(400, 'bad body');
$in = json_decode($raw, true);
if (!is_array($in)) fail(400, 'bad json');

$id = clip_token($in['id'] ?? '', 64);
if (strlen($id) < 8) fail(400, 'bad id');
$message = trim(head_chars(clean_text($in['message'] ?? ''), MAX_MESSAGE_CHARS));
if (mb_strlen($message, 'UTF-8') < 4) fail(400, 'empty message');

$dir = __DIR__ . '/data/feedback';
if (!is_dir($dir) && !mkdir($dir, 0750, true)) fail(500, 'data dir');

// 每 IP 每小时上限（文件锁保证并发下计数一致）。
$ip = clip_token($_SERVER['REMOTE_ADDR'] ?? '', 45);
$now = time();
$limitPath = $dir . '/.ratelimit.json';
$fh = fopen($limitPath, 'c+');
if ($fh === false || !flock($fh, LOCK_EX)) fail(500, 'lock');
$limits = json_decode(stream_get_contents($fh) ?: '{}', true);
if (!is_array($limits)) $limits = [];
foreach ($limits as $key => $stamps) {
    $kept = array_values(array_filter(is_array($stamps) ? $stamps : [], fn($t) => $now - (int)$t < 3600));
    if ($kept) $limits[$key] = $kept; else unset($limits[$key]);
}
$recent = $limits[$ip] ?? [];
if (count($recent) >= MAX_PER_IP_PER_HOUR) {
    flock($fh, LOCK_UN);
    fclose($fh);
    fail(429, 'rate limited');
}
$recent[] = $now;
$limits[$ip] = $recent;
rewind($fh);
ftruncate($fh, 0);
fwrite($fh, json_encode($limits));
fflush($fh);
flock($fh, LOCK_UN);
fclose($fh);

// 总量上限：超出时删最旧的（文件名以时间开头，按名排序即按时间排序）。
$existing = glob($dir . '/*.json') ?: [];
sort($existing, SORT_STRING);
while (count($existing) >= MAX_FILES) {
    @unlink(array_shift($existing));
}

$ticket = gmdate('Ymd-His', $now) . '-' . bin2hex(random_bytes(3));
$record = [
    'ticket' => $ticket,
    'time' => $now,
    'ip' => $ip,
    'id' => $id,
    'ver' => clip_token($in['ver'] ?? ''),
    'model' => clip_token($in['model'] ?? ''),
    'project' => clip_token($in['project'] ?? ''),
    'gpu' => clip_token($in['gpu'] ?? ''),
    'message' => $message,
    'contact' => trim(head_chars(clean_text($in['contact'] ?? ''), MAX_CONTACT_CHARS)),
    'sysinfo' => head_bytes(clean_text($in['sysinfo'] ?? ''), MAX_SYSINFO_BYTES),
    'log' => tail_bytes(clean_text($in['log'] ?? ''), MAX_LOG_BYTES),
];
$json = json_encode($record, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
if ($json === false || file_put_contents($dir . '/' . $ticket . '.json', $json, LOCK_EX) === false) fail(500, 'write');
echo json_encode(['ok' => true, 'ticket' => $ticket]);
