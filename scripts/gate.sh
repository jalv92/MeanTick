#!/usr/bin/env bash
# The real compile gate for MeanTick.
#
# `nt8c check <file>` compiles ONE file in isolation. MeanTickCore.cs uses MtBar /
# MtMath / MtSwing etc., all defined in the sibling MeanTickTypes.cs — and the shell
# file (MeanTickStrategy.cs) reaches across the same way, PLUS its own
# `using MeanTickCore;` line reports a false CS0246 on the namespace itself (nt8c
# can't see that a sibling file declares it — documented at design.md:93-94, same
# pattern as VeeSnapStrategy.cs:58). Do not "fix" a shell file by collapsing
# namespaces; fix the gate instead. So a per-file `nt8c check` reports false
# CS0246/CS0103 on every one of these names, on every edit. Two tools prove two
# different things and neither alone is the whole gate:
#   - `dotnet run --project tests` compiles the pure ninjascript/*.cs files TOGETHER
#     (real C# semantics, no false cross-file errors) and runs the asserts. That is
#     the actual cross-file compile check for the pure layer.
#   - `nt8c check` proves something dotnet cannot: compatibility with NT8's OWN
#     reference set, inside the single bin/Custom assembly where NT8 ships its own
#     types under common names (a CS0101 clash) — and, once the shell file exists,
#     that it resolves real NinjaTrader types at all.
#
# So: run both. Suppress ONLY the specific, enumerated false positives — a CS0246/
# CS0103 whose quoted symbol is one of MeanTick's OWN types, gathered by scanning
# ninjascript/*.cs for its own declarations, not hand-kept. Any other CS0246/CS0103
# (a typo, a wrong NT8 API name, a symbol nobody declared) fails the gate exactly as
# loud as before — this is a name allow-list, not a blanket ignore of an error code.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

status=0

echo "== dotnet run --project tests =="
if ! dotnet run --project tests; then
    status=1
fi

echo
echo "== nt8c check (ninjascript/*.cs against the NT8 reference set) =="
export PATH="$HOME/.local/bin:$PATH"

# Strip `//` comments first -- prose can otherwise smuggle a word into the list
# (e.g. "...removes the whole class of bug" contributed `of`). And NinjaTrader must
# NEVER be suppressed, even though `namespace NinjaTrader.NinjaScript.Strategies` in
# the shell matches the same pattern: it is the one name `nt8c check` exists to
# catch a real failure on (NT8's reference set resolving nothing), so a genuine
# CS0246 on it would go silently green without this exclusion.
siblings="$(sed 's|//.*||' ninjascript/*.cs \
    | grep -hoE '\b(class|struct|enum|namespace)\s+[A-Za-z_][A-Za-z0-9_]*' \
    | awk '{print $2}' | sort -u | grep -vx 'NinjaTrader')"
echo "sibling allow-list: $(echo "$siblings" | tr '\n' ' ')"

for f in ninjascript/*.cs; do
    outfile="$(mktemp)"
    nt8c check "$f" --agent > "$outfile" 2>&1 || true
    if ! SIBLINGS="$siblings" python3 - "$f" "$outfile" <<'PY'
import json, os, re, sys

fpath, outfile = sys.argv[1], sys.argv[2]
siblings = set(os.environ.get("SIBLINGS", "").split())
raw = open(outfile).read()
try:
    d = json.loads(raw)
except Exception:
    print(raw)
    sys.exit(1)

errs = d.get("results", {}).get("errors", []) or []
kept, suppressed = [], []
for e in errs:
    m = re.search(r"'([A-Za-z_][A-Za-z0-9_]*)'", e.get("message", ""))
    sym = m.group(1) if m else None
    if e.get("code") in ("CS0246", "CS0103") and sym in siblings:
        suppressed.append(sym)
    else:
        kept.append(e)

base = os.path.basename(fpath)
if kept:
    print("%s: %d real error(s), %d suppressed sibling reference(s)"
          % (base, len(kept), len(suppressed)))
    for e in kept:
        print("  %s(%s,%s): %s %s"
              % (base, e.get("line"), e.get("col"), e.get("code"), e.get("message")))
    sys.exit(1)

print("%s: OK%s" % (base, "" if not suppressed
                     else " (suppressed sibling refs: %s)" % ", ".join(sorted(set(suppressed)))))
PY
    then
        status=1
    fi
    rm -f "$outfile"
done

exit $status
