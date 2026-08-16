#!/usr/bin/env python3
"""The ladder-schedule parity gate: MtLadder.BuildLadder (C#) vs build_ladder (Python).

    dotnet run --project tests          # writes tests/golden_ladder.csv
    python3 research/compare_mirror.py  # reads it back

`tests/golden_ladder.csv` is the C# test run's own output -- see
`tests/ExitTests.cs`'s "golden ladder CSV" section, which is the single source
of truth for both the fixture INPUTS (hardcoded in FIXTURES below, matching
that file's three `MtLadder.BuildLadder(...)` calls verbatim) and the expected
OUTPUTS (the CSV rows). If ExitTests.cs's calls ever change, FIXTURES must
change with them -- there is no way to recover the inputs from the CSV alone,
since it records only what BuildLadder returned, not what it was given.

Every price is checked to 1e-9; quantity, is_structural and is_runner exactly.
No tolerance flag. A mismatch means one engine is wrong -- fix that engine,
never this number.
"""
import csv
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "propsim")))
import mean_tick  # noqa: E402  (path must be set up first)

CSV_PATH = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                          "..", "tests", "golden_ladder.csv"))
TOL = 1e-9

# Verbatim from tests/ExitTests.cs's golden-ladder section (lines ~169-184).
FIXTURES = {
    "rung1_only": dict(
        direc=1, entry=18000.0, stop_points=10.0, rung1_r=1.0, rung2_r=3.0,
        rung3_fallback_r=4.0, structural_price=18055.0, runner_price=18120.0,
        contracts=1, tick_size=0.25, min_rung_ticks=15,
    ),
    "all_rungs": dict(
        direc=1, entry=18000.13, stop_points=10.0, rung1_r=1.0, rung2_r=3.0,
        rung3_fallback_r=4.0, structural_price=float("nan"), runner_price=18120.07,
        contracts=4, tick_size=0.25, min_rung_ticks=15,
    ),
    "stop_between_rungs": dict(
        direc=1, entry=18000.0, stop_points=10.0, rung1_r=1.0, rung2_r=3.0,
        rung3_fallback_r=4.0, structural_price=18032.0, runner_price=18120.0,
        contracts=4, tick_size=0.25, min_rung_ticks=15,
    ),
}


def load_golden(path):
    """fixture -> list of row dicts, in CSV order (already rung_index order)."""
    if not os.path.exists(path):
        raise SystemExit(
            "golden_ladder.csv not found at %s -- run `dotnet run --project tests` first" % path)
    out = {}
    with open(path, newline="") as fh:
        for row in csv.DictReader(fh):
            out.setdefault(row["fixture"], []).append(row)
    return out


def compare_fixture(name, golden_rows, params):
    fails = []
    plan = mean_tick.build_ladder(**params)

    if not plan["valid"]:
        return ["%s: build_ladder produced an INVALID plan" % name]

    if len(plan["rungs"]) != len(golden_rows):
        return ["%s: %d rungs (python) vs %d rungs (C#)"
                % (name, len(plan["rungs"]), len(golden_rows))]

    exp_stop = float(golden_rows[0]["stop"])
    if abs(plan["stop_price"] - exp_stop) > TOL:
        fails.append("%s: stop_price %.10f (python) vs %.10f (C#)"
                      % (name, plan["stop_price"], exp_stop))

    for rung, gold in zip(plan["rungs"], golden_rows):
        where = "%s rung %s" % (name, gold["rung_index"])
        if rung["index"] != int(gold["rung_index"]):
            fails.append("%s: index %d (python) vs %s (C#)" % (where, rung["index"], gold["rung_index"]))
        if abs(rung["price"] - float(gold["price"])) > TOL:
            fails.append("%s: price %.10f (python) vs %.10f (C#)"
                          % (where, rung["price"], float(gold["price"])))
        if rung["quantity"] != int(gold["qty"]):
            fails.append("%s: qty %d (python) vs %s (C#)" % (where, rung["quantity"], gold["qty"]))
        exp_struct = gold["is_structural"] == "true"
        if rung["is_structural"] != exp_struct:
            fails.append("%s: is_structural %s (python) vs %s (C#)"
                          % (where, rung["is_structural"], gold["is_structural"]))
        exp_runner = gold["is_runner"] == "true"
        if rung["is_runner"] != exp_runner:
            fails.append("%s: is_runner %s (python) vs %s (C#)"
                          % (where, rung["is_runner"], gold["is_runner"]))
    return fails


def main():
    golden = load_golden(CSV_PATH)

    unknown = set(golden) - set(FIXTURES)
    missing = set(FIXTURES) - set(golden)
    fails = []
    if unknown:
        fails.append("golden_ladder.csv has fixture(s) not in FIXTURES: %s" % sorted(unknown))
    if missing:
        fails.append("FIXTURES has fixture(s) missing from golden_ladder.csv: %s" % sorted(missing))

    for name, params in FIXTURES.items():
        if name not in golden:
            continue
        fails += compare_fixture(name, golden[name], params)

    if fails:
        print("PARITY: FAIL (%d)" % len(fails))
        for f in fails:
            print("  FAIL", f)
        return 1

    print("PARITY OK -- %d fixtures, %d rungs, all prices within %.0e, all quantities and flags exact"
          % (len(golden), sum(len(v) for v in golden.values()), TOL))
    return 0


if __name__ == "__main__":
    sys.exit(main())
