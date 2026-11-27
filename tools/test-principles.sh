#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Checks PRINCIPLES.md says what its tables say: the dependency table is whole, the stated layer score and weighted
# score match the layer table, the count is at or above the 75% floor, and every exception has a review date.
# Fails on a wrong number or a broken table; only warns (on stderr) when the weighted figure is under 75% or a review
# date has passed. Usage: tools/test-principles.sh [file]   (PRINCIPLES_TODAY=YYYY-MM-DD overrides today, for tests)
set -euo pipefail
cd "$(dirname "$0")/.."
F="${1:-PRINCIPLES.md}"
TODAY="${PRINCIPLES_TODAY:-$(date +%F)}"
fail() { echo "principles: $*" >&2; exit 1; }
[ -f "$F" ] || fail "no $F"

# rows of the table whose header line starts with $1 (header and rule excluded), one per line
rows() { awk -v h="$1" 'index($0, h) == 1 { on = 1; next } on && /^\|---/ { next } on && /^\|/ { print; next } on { exit }' "$F"; }

# every dependency row has the table's five columns (a stray "|" in a cell breaks the table)
[ -n "$(rows '| Dependency |')" ] || fail "no dependency table"
bad="$(rows '| Dependency |' | awk -F'|' 'NF != 7 { print NR ": " $2 }')"
[ -z "$bad" ] || fail "dependency rows without five columns: $bad"

# layers: count and weight
layers="$(rows '| Layer |')"
[ -n "$layers" ] || fail "no layer table"
read -r total good wtotal wgood < <(echo "$layers" | awk -F'|' '
  { s = $4; w = $5; gsub(/^ +| +$/, "", s); gsub(/^ +| +$/, "", w)
    if (s != "ours" && s != "commons" && s != "dependent") { print "BAD score: " $2 > "/dev/stderr"; bad = 1 }
    if (w !~ /^[123]$/) { print "BAD weight: " $2 > "/dev/stderr"; bad = 1 }
    t++; wt += w; if (s != "dependent") { g++; wg += w } }
  END { if (bad) exit 1; print t, g, wt, wg }') || fail "a layer has no valid score or weight"
pct() { echo $(( (200 * $1 + $2) / (2 * $2) )); }

stated="$(sed -n 's/^\*\*Now: \([0-9]*\) of \([0-9]*\) (\([0-9]*\)%)\.\*\*.*/\1 \2 \3/p' "$F" | head -1)"
[ "$stated" = "$good $total $(pct "$good" "$total")" ] \
  || fail "the layer table gives $good of $total ($(pct "$good" "$total")%), the text says '${stated:-nothing}'"
wstated="$(sed -n 's/^\*\*Weighted: \([0-9]*\) of \([0-9]*\) (\([0-9]*\)%)\.\*\*.*/\1 \2 \3/p' "$F" | head -1)"
[ "$wstated" = "$wgood $wtotal $(pct "$wgood" "$wtotal")" ] \
  || fail "the layer table weighs $wgood of $wtotal ($(pct "$wgood" "$wtotal")%), the text says '${wstated:-nothing}'"
[ $((good * 100)) -ge $((total * 75)) ] || fail "$good of $total layers is under the 75% floor"
[ $((wgood * 100)) -ge $((wtotal * 75)) ] || echo "principles: warning: the weighted figure, $wgood of $wtotal, is under 75%" >&2

# exceptions: each has a review date, and a date that has passed is said out loud
ex="$(rows '| Exception |')"
[ -n "$ex" ] || fail "no exceptions table"
late=0
while IFS='|' read -r _ what _ date _; do
  date="$(echo "$date" | tr -d ' ')"
  echo "$date" | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' || fail "no review date (YYYY-MM-DD) for: $what"
  if [[ "$date" < "$TODAY" ]]; then
    echo "principles: warning: review of '$(echo "$what" | sed 's/^ *//; s/ *$//')' was due $date: end it, or keep it with a new date and a reason" >&2
    late=$((late + 1))
  fi
done <<< "$ex"

echo "principles: $good of $total layers ($(pct "$good" "$total")%), weighted $wgood of $wtotal ($(pct "$wgood" "$wtotal")%), $(echo "$ex" | wc -l) exceptions dated, $late overdue"
