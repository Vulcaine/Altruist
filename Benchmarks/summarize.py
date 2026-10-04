#!/usr/bin/env python3
"""
Builds SUMMARY.md for one benchmark run from BenchmarkDotNet JSON reports.

Usage: summarize.py <results-dir> [machine-info-file]

<results-dir> is the BDN artifacts directory (the one containing results/*-report-full-compressed.json),
or the results/ folder itself. Standard library only.

Every claimed number, and every derived number, is computed here from the measured means;
the formulas are printed into the summary next to the values (see also
BENCHMARK_RESULTS.md -> "Reproducing the numbers").
"""
import glob
import json
import os
import sys

# Tick budgets used for CCU estimates (Hz -> ms).
TICK_RATES = [20, 30, 60, 128]

# --------------------------------------------------------------------------- data loading


def load(results_dir):
    pattern_dirs = [os.path.join(results_dir, "results"), results_dir]
    files = []
    for d in pattern_dirs:
        files = sorted(glob.glob(os.path.join(d, "*-report-full-compressed.json")))
        if not files:
            files = sorted(glob.glob(os.path.join(d, "*-report-full.json")))
        if not files:
            files = sorted(glob.glob(os.path.join(d, "*-report*.json")))
        if files:
            break
    rows = {}
    host = None
    for f in files:
        with open(f, encoding="utf-8") as fh:
            doc = json.load(fh)
        host = host or doc.get("HostEnvironmentInfo")
        for b in doc.get("Benchmarks", []):
            stats = b.get("Statistics") or {}
            mem = b.get("Memory") or {}
            key = (b.get("Type"), b.get("Method"), normalize_params(b.get("Parameters", "")))
            rows[key] = {
                "mean_ns": stats.get("Mean"),
                "alloc_b": mem.get("BytesAllocatedPerOperation"),
                "title": b.get("MethodTitle", b.get("Method")),
                "params": b.get("Parameters", ""),
            }
    return rows, host, files


def normalize_params(p):
    if not p:
        return ""
    parts = sorted(x for x in p.split("&") if x)
    return "&".join(parts)


class Results:
    def __init__(self, rows):
        self.rows = rows

    def get(self, type_, method, **params):
        key = (type_, method, normalize_params("&".join(f"{k}={v}" for k, v in params.items())))
        r = self.rows.get(key)
        return r["mean_ns"] if r else None

    def alloc(self, type_, method, **params):
        key = (type_, method, normalize_params("&".join(f"{k}={v}" for k, v in params.items())))
        r = self.rows.get(key)
        return r["alloc_b"] if r else None


# --------------------------------------------------------------------------- formatting


def fmt_time(ns):
    if ns is None:
        return "n/a"
    if ns < 0:
        return "-" + fmt_time(-ns)
    if ns < 1_000:
        return f"{ns:.1f} ns"
    if ns < 1_000_000:
        return f"{ns / 1_000:.1f} µs"
    return f"{ns / 1_000_000:.3f} ms"


def fmt_bytes(b):
    if b is None:
        return "n/a"
    if b < 1024:
        return f"{b} B"
    return f"{b / 1024:.1f} KB"


def fmt_int(x):
    return "n/a" if x is None else f"{int(round(x)):,}"


def mul(a, b):
    return None if a is None or b is None else a * b


def add(*xs):
    return None if any(x is None for x in xs) else sum(xs)


# --------------------------------------------------------------------------- derived figures


def interp_visibility(r, players, npcs):
    """Linear interpolation of VisibilityBenchmarks tick over NpcCount (measured at 100 and 1000)."""
    lo = r.get("VisibilityBenchmarks", "TickSteadyState", PlayerCount=players, NpcCount=100)
    hi = r.get("VisibilityBenchmarks", "TickSteadyState", PlayerCount=players, NpcCount=1000)
    if lo is None or hi is None:
        return None
    return lo + (hi - lo) * (npcs - 100) / 900


def legacy_overhead(r):
    """Component sum for 50 players / 500 NPCs, as in the original 'Combined Tick Budget' table."""
    sync = mul(550, r.get("SyncBenchmarks", "SyncChangesPosition"))
    ai = mul(500, r.get("AIBenchmarks", "UpdateNoTransition"))
    vis = interp_visibility(r, 50, 500)
    collision = r.get("CollisionBenchmarks", "TickFull", EntityCount=500)
    combat = add(mul(50, r.get("CombatBenchmarks", "SingleAttack", EntityCount=100)),
                 mul(5, r.get("CombatBenchmarks", "SweepSphere", EntityCount=1000)))
    world = add(r.get("WorldBenchmarks", "FilterSyncEntities", ObjectCount=1000),
                r.get("WorldBenchmarks", "FilterAIEntities", ObjectCount=1000),
                r.get("WorldBenchmarks", "DistanceCheck", ObjectCount=1000))
    parts = [
        ("Entity sync (550 entities)", "550 × SyncBenchmarks.SyncChangesPosition", sync),
        ("AI behavior (500 NPCs)", "500 × AIBenchmarks.UpdateNoTransition", ai),
        ("Visibility (50 × 500)", "lerp(Visibility.TickSteadyState[50,100], [50,1000]) at 500 NPCs", vis),
        ("Combat", "50 × CombatBenchmarks.SingleAttack[100] + 5 × SweepSphere[1000]", combat),
        ("Collision (500 entities)", "CollisionBenchmarks.TickFull[500]", collision),
        ("World iteration", "WorldBenchmarks Filter{Sync,AI}Entities[1000] + DistanceCheck[1000]", world),
    ]
    total = add(*[p[2] for p in parts])
    return parts, total


def fulltick_curve(r, npcs, method="FullTick"):
    pts = []
    for p in (50, 200, 500, 1000):
        v = r.get("ScalabilityBenchmarks", method, PlayerCount=p, NpcCount=npcs)
        if v is not None:
            pts.append((p, v))
    return pts


def ccu_from_curve(pts, budget_ns):
    """
    Piecewise-linear over measured (players, tick_ns) points; beyond the last point, extrapolate with
    the slope of the last segment (the most conservative measured marginal cost when growth is convex).
    Returns the player count whose full tick equals budget_ns.
    """
    if len(pts) < 2:
        return None
    if pts[0][1] >= budget_ns:
        return pts[0][0] * budget_ns / pts[0][1]
    for (p0, t0), (p1, t1) in zip(pts, pts[1:]):
        if t0 <= budget_ns <= t1 and t1 > t0:
            return p0 + (budget_ns - t0) * (p1 - p0) / (t1 - t0)
    (p0, t0), (p1, t1) = pts[-2], pts[-1]
    slope = (t1 - t0) / (p1 - p0)
    if slope <= 0:
        return None
    return p1 + (budget_ns - t1) / slope


def legacy_marginal_player_ns(r):
    """Mean per-player visibility cost at 50 players over NpcCount ∈ {100, 1000}."""
    a = r.get("VisibilityBenchmarks", "TickSteadyState", PlayerCount=50, NpcCount=100)
    b = r.get("VisibilityBenchmarks", "TickSteadyState", PlayerCount=50, NpcCount=1000)
    if a is None or b is None:
        return None
    return (a / 50 + b / 50) / 2


# --------------------------------------------------------------------------- report


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    results_dir = sys.argv[1]
    machine_file = sys.argv[2] if len(sys.argv) > 2 else None
    rows, host, files = load(results_dir)
    r = Results(rows)
    out = []
    w = out.append

    w("# Altruist benchmark summary\n")
    if machine_file and os.path.exists(machine_file):
        w("## Machine\n")
        w("```")
        with open(machine_file, encoding="utf-8") as fh:
            w(fh.read().rstrip())
        w("```\n")
    mode = ""
    if machine_file and os.path.exists(machine_file):
        for line in open(machine_file, encoding="utf-8"):
            if line.startswith("mode:"):
                mode = line.split(":", 1)[1].strip()
    if mode in ("dry", "quick"):
        w(f"> **Warning: {mode} run.** "
          + ("Dry = one cold-start invocation per benchmark; values only prove the benchmarks run and are not "
             "performance numbers." if mode == "dry" else
             "ShortRun (3 iterations) has wide error bars; use the full run for published numbers.") + "\n")
    if host:
        w(f"BenchmarkDotNet {host.get('BenchmarkDotNetVersion')} · {host.get('OsVersion')} · "
          f"{host.get('ProcessorName')} ({host.get('PhysicalCoreCount')} physical / "
          f"{host.get('LogicalCoreCount')} logical cores) · {host.get('RuntimeVersion')} {host.get('Architecture')}\n")
    measured = sum(1 for v in rows.values() if v["mean_ns"] is not None)
    w(f"Reports parsed: {len(files)} · benchmarks: {len(rows)} · with results: {measured}"
      + (f" · **failed/NA: {len(rows) - measured}**" if measured < len(rows) else "") + "\n")

    # ---- headline claims
    full_50_500 = r.get("ScalabilityBenchmarks", "FullTick", PlayerCount=50, NpcCount=500)
    parts, legacy_total = legacy_overhead(r)
    budget30 = 1e9 / 30
    curve500 = fulltick_curve(r, 500)
    ccu30 = ccu_from_curve(curve500, budget30)
    legacy_c = legacy_marginal_player_ns(r)
    legacy_ccu30 = None if legacy_c is None else 2 * budget30 / legacy_c

    def pct_budget(ns):
        return "" if ns is None else f" ({100 * ns / budget30:.1f}% of 33.3 ms)"

    w("## Claims\n")
    w("| Claim (README) | Benchmark | Measured |")
    w("|---|---|---|")
    w(f"| 0.9 ms total framework overhead per tick (50 players, 500 NPCs) | "
      f"`ScalabilityBenchmarks.FullTick` [PlayerCount=50, NpcCount=500] | "
      f"**{fmt_time(full_50_500)}**{pct_budget(full_50_500)} |")
    w(f"| ↳ same, legacy component-sum method | see *Per-tick overhead (legacy)* below | "
      f"{fmt_time(legacy_total)}{pct_budget(legacy_total)} |")
    w(f"| AI FSM 14 ns/entity, 0 alloc | `AIBenchmarks.UpdateNoTransition` | "
      f"**{fmt_time(r.get('AIBenchmarks', 'UpdateNoTransition'))}**, "
      f"{fmt_bytes(r.alloc('AIBenchmarks', 'UpdateNoTransition'))} |")
    w(f"| Combat 8 ns single attack | `CombatBenchmarks.SingleAttack` [EntityCount=100] | "
      f"**{fmt_time(r.get('CombatBenchmarks', 'SingleAttack', EntityCount=100))}**, "
      f"{fmt_bytes(r.alloc('CombatBenchmarks', 'SingleAttack', EntityCount=100))} |")
    w(f"| Collision 13 µs / 100 entities | `CollisionBenchmarks.TickFull` [EntityCount=100] | "
      f"**{fmt_time(r.get('CollisionBenchmarks', 'TickFull', EntityCount=100))}**, "
      f"{fmt_bytes(r.alloc('CollisionBenchmarks', 'TickFull', EntityCount=100))} |")
    w(f"| Visibility 118 µs, 10 players × 100 NPCs | `VisibilityBenchmarks.TickSteadyState` [PlayerCount=10, NpcCount=100] | "
      f"**{fmt_time(r.get('VisibilityBenchmarks', 'TickSteadyState', PlayerCount=10, NpcCount=100))}**, "
      f"{fmt_bytes(r.alloc('VisibilityBenchmarks', 'TickSteadyState', PlayerCount=10, NpcCount=100))} |")
    w(f"| Entity sync 249 ns/entity (position update) | `SyncBenchmarks.SyncChangesPosition` | "
      f"**{fmt_time(r.get('SyncBenchmarks', 'SyncChangesPosition'))}**, "
      f"{fmt_bytes(r.alloc('SyncBenchmarks', 'SyncChangesPosition'))} |")
    w(f"| ~6,600 CCU at 30 Hz | FullTick curve over PlayerCount [NpcCount=500] (see *CCU*) | "
      f"**{fmt_int(ccu30)}** |")
    w(f"| ↳ same, legacy formula (2 × 33.3 ms / per-player visibility cost) | see *CCU (legacy)* | "
      f"{fmt_int(legacy_ccu30)} |")
    w("")

    # ---- legacy breakdown
    w("## Per-tick overhead\n")
    w("**Direct measurement.** `ScalabilityBenchmarks.FullTick` runs one complete tick "
      "(movement, AI, sync + observer fan-out, visibility, collision, combat) for the given world; "
      "the `*Only` methods run one stage each with identical inputs.\n")
    w("| Stage (50 players / 500 NPCs) | Benchmark | Mean | Allocated |")
    w("|---|---|---|---|")
    for m, label in [("FullTick", "**Full tick**"), ("SyncOnly", "Sync (incl. movement)"), ("AIOnly", "AI"),
                     ("VisibilityOnly", "Visibility"), ("CollisionOnly", "Collision"), ("CombatOnly", "Combat")]:
        w(f"| {label} | `ScalabilityBenchmarks.{m}` | {fmt_time(r.get('ScalabilityBenchmarks', m, PlayerCount=50, NpcCount=500))} | "
          f"{fmt_bytes(r.alloc('ScalabilityBenchmarks', m, PlayerCount=50, NpcCount=500))} |")
    w("")
    w("**Per-tick overhead (legacy).** The original 0.9 ms figure was a hand-summed estimate. "
      "Same components, computed from the micro-benchmarks:\n")
    w("| System | Formula | Cost per tick |")
    w("|---|---|---|")
    for name, formula, v in parts:
        w(f"| {name} | {formula} | {fmt_time(v)} |")
    w(f"| **Total** | sum | **{fmt_time(legacy_total)}**{pct_budget(legacy_total)} |")
    w("")

    # ---- CCU
    w("## CCU\n")
    w("**Method (direct).** For a fixed NPC count, `FullTick` is measured at PlayerCount ∈ {50, 200, 500, 1000}. "
      "CCU(f) is the player count at which the full tick equals the tick budget 1000/f ms: piecewise-linear "
      "interpolation between measured points, and beyond 1000 players linear extrapolation with the slope of the "
      "500→1000 segment (the largest measured marginal per-player cost). Visibility runs `Parallel.For` and staggers "
      "observers internally, so this is multi-core wall time with staggering already included — no extra ×2.\n")
    for npcs in (500, 2000):
        pts = fulltick_curve(r, npcs)
        if not pts:
            continue
        w(f"FullTick, NpcCount={npcs}: " + ", ".join(f"{p} players = {fmt_time(t)}" for p, t in pts))
        if len(pts) >= 2:
            (p0, t0), (p1, t1) = pts[-2], pts[-1]
            w(f" · marginal cost (last segment) = {fmt_time((t1 - t0) / (p1 - p0))}/player")
        w("")
        w("| Tick rate | Budget | CCU |")
        w("|---|---|---|")
        for hz in TICK_RATES:
            w(f"| {hz} Hz | {1000 / hz:.1f} ms | {fmt_int(ccu_from_curve(pts, 1e9 / hz))} |")
        w("")
    w("**CCU (legacy).** The original table used `CCU_single = budget / c`, `CCU_stagger = 2 × CCU_single` with a "
      "hand-picked c ≈ 10 µs. The closest reproducible c is the mean per-player visibility cost at 50 players: "
      "c = (Vis[50,100]/50 + Vis[50,1000]/50) / 2 "
      f"= **{fmt_time(legacy_c)}** (≈10.9 µs on the original report data). Note the ×2 double-counts staggering, "
      "which is already inside the measured visibility tick.\n")
    w("| Tick rate | Budget | budget / c | 2 × budget / c |")
    w("|---|---|---|---|")
    for hz in TICK_RATES:
        b = 1e9 / hz
        single = None if legacy_c is None else b / legacy_c
        w(f"| {hz} Hz | {1000 / hz:.1f} ms | {fmt_int(single)} | {fmt_int(None if single is None else 2 * single)} |")
    w("")

    # ---- all rows
    w("## All measured benchmarks\n")
    w("| Benchmark | Params | Mean | Allocated |")
    w("|---|---|---|---|")
    for (t, m, _), v in sorted(rows.items(), key=lambda kv: (kv[0][0] or "", kv[0][1] or "", kv[0][2])):
        w(f"| `{t}.{m}` | {v['params'] or '—'} | {fmt_time(v['mean_ns'])} | {fmt_bytes(v['alloc_b'])} |")
    w("")

    text = "\n".join(out)
    dest = os.path.join(results_dir, "SUMMARY.md")
    with open(dest, "w", encoding="utf-8") as fh:
        fh.write(text + "\n")
    print(text)
    print(f"\nWrote {dest}", file=sys.stderr)


if __name__ == "__main__":
    main()
