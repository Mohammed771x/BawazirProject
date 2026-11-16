"""Redacted secret scan used in QA campaign 20261111-0121-d365f09 (prompt 04).
Prints path, pattern name and the first 10 hex of SHA-256 of each match — never the value.
Run from the repository root:  python3 -I reports/qa/20261111-0121-d365f09/raw/secret_scan.py
"""
import hashlib, re, subprocess
PATTERNS = {
    'google_api_key': r'AIza[0-9A-Za-z_\-]{35}', 'groq_key': r'gsk_[0-9A-Za-z]{20,}',
    'openai_like': r'\bsk-[A-Za-z0-9]{20,}', 'private_key': r'-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    'pg_url_with_password': r'postgres(ql)?://[^:\s/]+:[^@\s]{6,}@',
    'jwt_signing_key_literal': r'SigningKey"\s*:\s*"[^"]{20,}"', 'aws_key': r'AKIA[0-9A-Z]{16}',
}
def scan(text, where):
    for name, p in PATTERNS.items():
        for m in re.finditer(p, text):
            yield where, name, hashlib.sha256(m.group(0).encode()).hexdigest()[:10]
skip = ('.png', '.jpg', '.apk', '.ttf', '.otf', '.ico', '.gz', '.zip', '.jar', '.so', '.pdf', '.mp3', '.wav')
files = [f for f in subprocess.run(['git', 'ls-files'], capture_output=True, text=True).stdout.split('\n') if f and not f.endswith(skip)]
head = [h for f in files for h in scan(open(f, errors='ignore').read(), f)]
print('HEAD:', len(head)); [print('  ', h) for h in head]
log = subprocess.run(['git', 'log', '-p', '--all', '--no-color', '-U0'], capture_output=True, text=True, errors='ignore').stdout
seen, commit = set(), None
for line in log.split('\n'):
    if line.startswith('commit '): commit = line[7:15]
    elif line.startswith('+') and not line.startswith('+++'):
        seen.update((n, fp, commit) for _, n, fp in scan(line, commit))
print('history:', len(seen)); [print('  ', h) for h in sorted(seen)]
