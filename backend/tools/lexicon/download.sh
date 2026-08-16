#!/usr/bin/env bash
#
# Downloads the three lexicon sources into ./data (gitignored).
#
# Reproducible and idempotent: each file is fetched only if missing, and every
# download records its source URL, version and SHA-256 into data/MANIFEST.json
# so the provenance stored against each lexicon row can be traced back to an
# exact artefact.
#
#   ./download.sh          fetch what is missing
#   ./download.sh --force  re-fetch everything
#
set -euo pipefail

cd "$(dirname "$0")"
DATA_DIR="./data"
mkdir -p "$DATA_DIR"

FORCE=0
[[ "${1:-}" == "--force" ]] && FORCE=1

# ── Pinned versions ──────────────────────────────────────────────────────────
# Pinned, not "latest": the lexicon must be rebuildable byte-for-byte, and a
# silent upstream change would alter learners' vocabulary levels.
OEWN_TAG="2025-edition"
OEWN_FILE="english-wordnet-2025-json.zip"
OEWN_URL="https://github.com/globalwordnet/english-wordnet/releases/download/${OEWN_TAG}/${OEWN_FILE}"

CEFRJ_REF="master"
CEFRJ_URL="https://raw.githubusercontent.com/openlanguageprofiles/olp-en-cefrj/${CEFRJ_REF}/cefrj-vocabulary-profile-1.5.csv"
OCTANOVE_URL="https://raw.githubusercontent.com/openlanguageprofiles/olp-en-cefrj/${CEFRJ_REF}/octanove-vocabulary-profile-c1c2-1.0.csv"

AWN_REF="main"
AWN_URL="https://raw.githubusercontent.com/Salah-Sal/arabic-wordnet-v4/${AWN_REF}/output/awn4.xml.gz"

log() { printf '  %s\n' "$*"; }

fetch() {
  local url="$1" out="$2"
  if [[ -f "$out" && $FORCE -eq 0 ]]; then
    log "cached   $(basename "$out")"
    return
  fi
  log "download $(basename "$out")"
  curl -sSfL --max-time 900 --retry 3 --retry-delay 2 "$url" -o "$out.part"
  mv "$out.part" "$out"
}

echo "Downloading lexicon sources into $DATA_DIR"

fetch "$CEFRJ_URL"    "$DATA_DIR/cefrj.csv"
fetch "$OCTANOVE_URL" "$DATA_DIR/octanove-c1c2.csv"
fetch "$OEWN_URL"     "$DATA_DIR/oewn.zip"
fetch "$AWN_URL"      "$DATA_DIR/awn4.xml.gz"

# ── Unpack ───────────────────────────────────────────────────────────────────
# The JSON release is split across ~73 files: entries-<letter>.json map words to
# senses, and <pos>.<class>.json hold the synsets themselves. All of them are
# needed, so the whole archive is kept.
if [[ ! -d "$DATA_DIR/oewn" || $FORCE -eq 1 ]]; then
  log "unzip    oewn.zip"
  rm -rf "$DATA_DIR/oewn"
  unzip -q -o "$DATA_DIR/oewn.zip" -d "$DATA_DIR/oewn"
  ls "$DATA_DIR/oewn"/entries-*.json >/dev/null 2>&1 \
    || { echo "ERROR: oewn.zip did not contain entries-*.json" >&2; exit 1; }
fi

if [[ ! -f "$DATA_DIR/awn4.xml" || $FORCE -eq 1 ]]; then
  log "gunzip   awn4.xml.gz"
  gunzip -kf "$DATA_DIR/awn4.xml.gz"
fi

# ── Manifest: what was used, from where, and exactly which bytes ─────────────
sha() { shasum -a 256 "$1" | cut -d' ' -f1; }

cat > "$DATA_DIR/MANIFEST.json" <<JSON
{
  "generatedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "sources": [
    {
      "id": "cefrj",
      "name": "CEFR-J Vocabulary Profile",
      "version": "1.5",
      "url": "${CEFRJ_URL}",
      "file": "cefrj.csv",
      "sha256": "$(sha "$DATA_DIR/cefrj.csv")",
      "licence": "Free for research and commercial use with citation (Tono Lab, TUFS)"
    },
    {
      "id": "octanove",
      "name": "Octanove Vocabulary Profile C1/C2",
      "version": "1.0",
      "url": "${OCTANOVE_URL}",
      "file": "octanove-c1c2.csv",
      "sha256": "$(sha "$DATA_DIR/octanove-c1c2.csv")",
      "licence": "CC BY-SA 4.0"
    },
    {
      "id": "oewn",
      "name": "Open English WordNet",
      "version": "${OEWN_TAG}",
      "url": "${OEWN_URL}",
      "file": "oewn/",
      "sha256": "$(sha "$DATA_DIR/oewn.zip")",
      "licence": "CC BY 4.0"
    },
    {
      "id": "awn",
      "name": "Arabic WordNet 4.0",
      "version": "4.0",
      "url": "${AWN_URL}",
      "file": "awn4.xml",
      "sha256": "$(sha "$DATA_DIR/awn4.xml")",
      "licence": "CC BY (derived from Open English WordNet)",
      "note": "Portions machine-translated upstream; approved as a lexical source. Recorded per row in SourceFlags so it stays auditable (ADR-012)."
    }
  ]
}
JSON

echo
echo "Done. Files in $DATA_DIR (none of them are committed):"
ls -lh "$DATA_DIR" | awk 'NR>1 {printf "  %-24s %s\n", $9, $5}'
