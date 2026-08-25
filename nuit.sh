#!/usr/bin/env bash
#
# The whole thing, mutated, while nobody is watching.
#
# Three projects, one after the other, because a full run is thousands of
# mutants and hours. The digest at the end is what to read in the morning: the
# score is a number and the survivors are the work.
set -uo pipefail

RACINE="$(cd "$(dirname "$0")" && pwd)"
SORTIE="$RACINE/build/mutation"
mkdir -p "$SORTIE"

DIGEST="$SORTIE/survivants.md"
: > "$DIGEST"

echo "# Mutation, nuit du $(date '+%Y-%m-%d')" >> "$DIGEST"

for PROJET in LegacyLens.Analysis LegacyLens.Characterization LegacyLens.Api; do
  echo
  echo "########## $PROJET"

  DEBUT=$(date +%s)

  "$RACINE/mutate.sh" '**/*.cs' "$PROJET" > "$SORTIE/$PROJET.log" 2>&1
  CODE=$?

  FIN_=$(date +%s)

  SCORE="$(grep -oE 'final mutation score is [0-9.]+ %' "$SORTIE/$PROJET.log" | tail -1)"
  TUES="$(grep -oE '^Killed: +[0-9]+' "$SORTIE/$PROJET.log" | tail -1)"
  VIVANTS="$(grep -oE '^Survived: +[0-9]+' "$SORTIE/$PROJET.log" | tail -1)"

  {
    echo
    echo "## $PROJET"
    echo
    echo "- ${SCORE:-pas de score, voir le journal}"
    echo "- ${TUES:-?} / ${VIVANTS:-?}"
    echo "- $(( (FIN_ - DEBUT) / 60 )) minutes, code de sortie $CODE"
    echo
    echo '```'
    sed -n '/  Survivants :/,$p' "$SORTIE/$PROJET.log" | tail -n +3
    echo '```'
  } >> "$DIGEST"

  echo "  $SCORE  en $(( (FIN_ - DEBUT) / 60 )) min"
done

echo
echo "Digest : $DIGEST"
