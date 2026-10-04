#!/usr/bin/env bash
# Reproducible Altruist benchmark run.
#
#   ./run.sh                 full run (reference job: 5 warmup + 20 iterations), all suites
#   ./run.sh --quick         same suites with BenchmarkDotNet's ShortRun job (--job short)
#   ./run.sh --dry           smoke test: BDN Dry job (1 iteration), checks everything runs
#   ./run.sh [opts] -- ARGS  extra BenchmarkDotNet args, e.g. -- --filter '*Sync*'
#
# Any argument that is not one of the options above is also passed to BenchmarkDotNet.
# Output: results/<yyyymmdd-HHMMSS>-<host>/ with machine.txt, BDN reports
# (results/*-report-github.md, *.csv, *.json, *.html), the BDN log and SUMMARY.md.
# Works on macOS and Linux; needs bash, the .NET SDK and python3.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

JOB_ARGS=()
BDN_ARGS=()
MODE="full"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --quick) JOB_ARGS=(--job short); MODE="quick"; shift ;;
    --dry)   JOB_ARGS=(--job dry);   MODE="dry";   shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    --) shift; BDN_ARGS+=("$@"); break ;;
    *) BDN_ARGS+=("$1"); shift ;;
  esac
done

has_filter=0
for a in ${BDN_ARGS[@]+"${BDN_ARGS[@]}"}; do
  case "$a" in --filter|-f|--filter=*) has_filter=1 ;; esac
done
FILTER_ARGS=()
[[ $has_filter -eq 0 ]] && FILTER_ARGS=(--filter '*')

command -v dotnet >/dev/null || { echo "dotnet SDK not found on PATH" >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 not found on PATH (needed for the summary)" >&2; exit 1; }

HOST="$(hostname -s 2>/dev/null || hostname)"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$SCRIPT_DIR/results/$STAMP-$HOST"
[[ "$MODE" != "full" ]] && OUT="$OUT-$MODE"
mkdir -p "$OUT"

# ---------------------------------------------------------------- machine info
{
  echo "date:        $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host:        $HOST"
  echo "mode:        $MODE"
  echo "command:     $0 ${JOB_ARGS[*]-} ${FILTER_ARGS[*]-} ${BDN_ARGS[*]-}"
  case "$(uname -s)" in
    Darwin)
      echo "os:          $(sw_vers -productName) $(sw_vers -productVersion) ($(sw_vers -buildVersion)), $(uname -srm)"
      echo "cpu:         $(sysctl -n machdep.cpu.brand_string 2>/dev/null || echo unknown)"
      echo "cores:       $(sysctl -n hw.physicalcpu) physical / $(sysctl -n hw.logicalcpu) logical"
      echo "ram:         $(( $(sysctl -n hw.memsize) / 1024 / 1024 / 1024 )) GiB"
      echo "power:       $(pmset -g batt 2>/dev/null | head -1 | sed "s/.*'\(.*\)'.*/\1/" || echo unknown)"
      ;;
    Linux)
      os_name="$(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME" || echo Linux)"
      echo "os:          $os_name, $(uname -srm)"
      echo "cpu:         $(grep -m1 'model name' /proc/cpuinfo 2>/dev/null | cut -d: -f2- | sed 's/^ //' || lscpu | grep 'Model name' | cut -d: -f2- | sed 's/^ *//')"
      phys="$(lscpu -p=core,socket 2>/dev/null | grep -v '^#' | sort -u | wc -l | tr -d ' ')"
      echo "cores:       ${phys:-?} physical / $(nproc) logical"
      echo "ram:         $(awk '/MemTotal/ {printf "%.0f GiB", $2/1024/1024}' /proc/meminfo)"
      gov="/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"
      [[ -r "$gov" ]] && echo "governor:    $(cat "$gov")"
      ;;
    *) echo "os:          $(uname -a)" ;;
  esac
  echo "load avg:    $(uptime | sed 's/.*load average[s]*: //') (should be low: a busy machine makes the numbers noisy)"
  echo "dotnet sdk:  $(dotnet --version)"
  echo "runtimes:    $(dotnet --list-runtimes | grep Microsoft.NETCore.App | awk '{print $2}' | tr '\n' ' ')"
  if git -C "$SCRIPT_DIR" rev-parse HEAD >/dev/null 2>&1; then
    dirty=""
    [[ -n "$(git -C "$SCRIPT_DIR/.." status --porcelain 2>/dev/null)" ]] && dirty=" (working tree dirty)"
    echo "git commit:  $(git -C "$SCRIPT_DIR" rev-parse HEAD) [$(git -C "$SCRIPT_DIR" rev-parse --abbrev-ref HEAD)]$dirty"
  fi
} > "$OUT/machine.txt"
cat "$OUT/machine.txt"
echo

# ---------------------------------------------------------------- build + run
echo "==> Building Release"
dotnet build "$SCRIPT_DIR/Benchmarks.csproj" -c Release --nologo -v quiet

echo "==> Running BenchmarkDotNet ($MODE) -> $OUT"
set +e
dotnet run --project "$SCRIPT_DIR/Benchmarks.csproj" -c Release --no-build -- \
  ${FILTER_ARGS[@]+"${FILTER_ARGS[@]}"} \
  ${JOB_ARGS[@]+"${JOB_ARGS[@]}"} \
  --exporters json github csv html \
  --artifacts "$OUT" \
  ${BDN_ARGS[@]+"${BDN_ARGS[@]}"} 2>&1 | tee "$OUT/console.log"
bdn_status=${PIPESTATUS[0]}
set -e

# ---------------------------------------------------------------- summary
echo
echo "==> Summarizing"
python3 "$SCRIPT_DIR/summarize.py" "$OUT" "$OUT/machine.txt" > /dev/null
echo "Summary: $OUT/SUMMARY.md"
grep -A14 '^## Claims' "$OUT/SUMMARY.md" || true
exit "$bdn_status"
