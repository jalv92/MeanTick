#!/usr/bin/env bash
# The real compile gate for MeanTick.
#
# `nt8c check <file>` compiles ONE file in isolation. Worse than the false CS0246/
# CS0103 that produces on every cross-file reference: once Roslyn can't resolve a
# type like MtLadder or MtSession, it stops checking the CALL into it too, so a
# dropped argument or a nonexistent member on that same line goes invisible. An
# allow-list that suppresses the unresolved-type error can't get that check back --
# it was never run. NinjaTrader compiles every .cs under Custom/ into ONE assembly,
# so the honest equivalent is to stage the repo in that shape and build it as one --
# same compiler, same references, real cross-file resolution, zero suppression
# needed. Mirrors projects/Trading/SizeMap/scripts/gate.sh.
#
# Two tools, two different proofs, neither alone is the whole gate:
#   - `dotnet run --project tests` compiles the pure files together and runs the
#     asserts -- the logic is correct, not just that it compiles. It also writes
#     tests/golden_ladder.csv, which compare_mirror.py reads next.
#   - `python3 research/compare_mirror.py` is the ladder-schedule parity gate
#     (C# MtLadder.BuildLadder vs the Python mirror). Nothing else runs it, and
#     this hook rewrites golden_ladder.csv on every ninjascript/*.cs edit, so
#     running the comparison right here is what keeps the CSV provably fresh at
#     the moment it's checked instead of just regenerated and never read.
#   - `nt8c build --custom-dir <staged Custom/>` proves the WHOLE project (pure +
#     shell) compiles against NT8's own reference set, inside the single
#     bin/Custom assembly -- the one thing dotnet cannot prove.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

status=0

echo "== dotnet run --project tests =="
if ! dotnet run --project tests; then
    status=1
fi

echo
echo "== python3 research/compare_mirror.py =="
if ! python3 research/compare_mirror.py; then
    status=1
fi

echo
echo "== nt8c build (ninjascript/*.cs staged as Custom/, NT8 reference set) =="
export PATH="$HOME/.local/bin:$PATH"

STAGE="$(mktemp -d)/Custom"
trap 'rm -rf "$(dirname "$STAGE")"' EXIT
mkdir -p "$STAGE/Strategies/MeanTick"
cp "$REPO"/ninjascript/*.cs "$STAGE/Strategies/MeanTick/"

nt8c build --custom-dir "$STAGE" --no-emit --agent > "$STAGE/../out.json" 2>&1 || true

if ! python3 - "$STAGE/../out.json" <<'PY'
import json, sys

raw = open(sys.argv[1]).read()
try:
    d = json.loads(raw)
except Exception:
    print(raw)
    sys.exit(1)

errs = d.get("results", {}).get("errors", []) or []
n = d.get("meta", {}).get("files_compiled", "?")
print("files compiled: %s | errors: %d" % (n, len(errs)))
for e in errs[:25]:
    print("  %s(%s,%s): %s %s" % (e.get("file", "?").split("/")[-1], e.get("line"),
                                  e.get("col"), e.get("code"), e.get("message")))
sys.exit(1 if errs else 0)
PY
then
    status=1
fi

exit $status
