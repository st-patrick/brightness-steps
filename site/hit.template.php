<?php
// Minimal, self-hosted page analytics. Counts events per day and where visitors
// came from. No cookies, no IP addresses, no identifiers, no third party -
// there is nothing here that could identify a person even if it leaked.
//
// Security posture follows ../shared-config/headless-json-storage.md:
//  - Event names come from a hard allowlist, so a caller can never invent keys
//    and grow the file arbitrarily.
//  - Referrers are reduced to a bare hostname and character-filtered; the raw
//    URL (which can carry query strings) is never stored.
//  - Data lives in ./data/, which auto-gets a deny-all .htaccess, so the file
//    is never web-fetchable. Filename is a hard constant - no user input
//    reaches a path, so there is no traversal.
//  - Fixed schema + re-encode, size caps, atomic write (tmp + rename).
//  - Reading the numbers needs an admin key that is NOT in the page JS.
declare(strict_types=1);

const ADMIN_KEY  = '__ADMIN_KEY__';
const DATA_DIR   = __DIR__ . '/data';
const STORE_FILE = DATA_DIR . '/analytics.json';
const MAX_BYTES  = 262144;    // 256 KB of counters is years of traffic
const MAX_HOSTS  = 200;       // distinct referrer hosts kept
const MAX_DAYS   = 400;

// Anything not in this list is discarded.
const EVENTS = ['view', 'download', 'portable', 'github', 'donate'];

header('Content-Type: application/json');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');

$method = $_SERVER['REQUEST_METHOD'] ?? 'GET';

function ensureDataDir(): bool {
    if (!is_dir(DATA_DIR) && !@mkdir(DATA_DIR, 0700, true) && !is_dir(DATA_DIR)) return false;
    $ht = DATA_DIR . '/.htaccess';
    if (!is_file($ht)) {
        // Deny ALL web access, guarded so it cannot 500 on any Apache layout.
        @file_put_contents($ht, <<<HT
Options -Indexes
<IfModule mod_authz_core.c>
  Require all denied
</IfModule>
<IfModule !mod_authz_core.c>
  <IfModule mod_access_compat.c>
    Order allow,deny
    Deny from all
  </IfModule>
</IfModule>
HT);
    }
    return true;
}

if (!ensureDataDir()) {
    http_response_code(500);
    echo json_encode(['error' => 'storage unavailable']);
    exit;
}

// --- read the numbers (admin only) ------------------------------------------
if ($method === 'GET') {
    $key = $_GET['stats'] ?? '';
    if (!is_string($key) || !hash_equals(ADMIN_KEY, $key)) {
        http_response_code(404);          // do not advertise that stats exist
        echo json_encode(['error' => 'not found']);
        exit;
    }
    if (!is_file(STORE_FILE)) { echo '{}'; exit; }
    readfile(STORE_FILE);
    exit;
}

if ($method !== 'POST') { http_response_code(405); echo json_encode(['error' => 'method']); exit; }

// --- record ------------------------------------------------------------------
$event = $_POST['e'] ?? '';
if (!is_string($event) || !in_array($event, EVENTS, true)) {
    http_response_code(204);              // ignore quietly; never echo the input back
    exit;
}

// Referrer reduced to a hostname. Never store the full URL - query strings can
// carry anything, including things the visitor did not mean to send.
$host = '';
$ref = $_POST['r'] ?? '';
if (is_string($ref) && $ref !== '' && strlen($ref) < 2000) {
    $parsed = parse_url($ref, PHP_URL_HOST);
    if (is_string($parsed)) {
        $parsed = strtolower($parsed);
        if (preg_match('/^[a-z0-9.-]{1,60}$/', $parsed)) $host = $parsed;
    }
}
if ($host === '') $host = 'direct';

$data = [];
if (is_file(STORE_FILE)) {
    $raw = file_get_contents(STORE_FILE, false, null, 0, MAX_BYTES);
    $decoded = $raw === false ? null : json_decode($raw, true);
    if (is_array($decoded)) $data = $decoded;
}

$day = gmdate('Y-m-d');
if (!isset($data['days']) || !is_array($data['days'])) $data['days'] = [];
if (!isset($data['refs']) || !is_array($data['refs'])) $data['refs'] = [];
if (!isset($data['totals']) || !is_array($data['totals'])) $data['totals'] = [];

$data['days'][$day][$event] = (int)($data['days'][$day][$event] ?? 0) + 1;
$data['totals'][$event] = (int)($data['totals'][$event] ?? 0) + 1;

// Only count a referrer once per view, and stop accepting new hosts once full
// so a flood of junk referrers cannot grow the file without bound.
if ($event === 'view') {
    if (isset($data['refs'][$host]) || count($data['refs']) < MAX_HOSTS) {
        $data['refs'][$host] = (int)($data['refs'][$host] ?? 0) + 1;
    }
}

// Rebuild to a fixed shape: only known event keys, only integers.
$clean = ['days' => [], 'refs' => [], 'totals' => [], 'updated' => gmdate('c')];
$days = $data['days'];
krsort($days);
$days = array_slice($days, 0, MAX_DAYS, true);
foreach ($days as $d => $counts) {
    if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', (string)$d) || !is_array($counts)) continue;
    foreach (EVENTS as $e)
        if (isset($counts[$e])) $clean['days'][(string)$d][$e] = max(0, (int)$counts[$e]);
}
foreach ($data['refs'] as $h => $n) {
    if (!is_string($h) || !preg_match('/^[a-z0-9.-]{1,60}$/', $h)) continue;
    $clean['refs'][$h] = max(0, (int)$n);
}
arsort($clean['refs']);
$clean['refs'] = array_slice($clean['refs'], 0, MAX_HOSTS, true);
foreach (EVENTS as $e)
    if (isset($data['totals'][$e])) $clean['totals'][$e] = max(0, (int)$data['totals'][$e]);

$encoded = json_encode($clean, JSON_UNESCAPED_SLASHES);
if ($encoded === false || strlen($encoded) > MAX_BYTES) { http_response_code(204); exit; }

$tmp = STORE_FILE . '.' . bin2hex(random_bytes(4)) . '.tmp';
if (@file_put_contents($tmp, $encoded, LOCK_EX) !== false) {
    @rename($tmp, STORE_FILE);
} else {
    @unlink($tmp);
}

http_response_code(204);      // nothing to say back to the page
