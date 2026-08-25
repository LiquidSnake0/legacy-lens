#!/usr/bin/env bash
#
# Mutation testing, by a tool rather than by hand.
#
# Every milestone here has ended with a mutation check: break the thing on
# purpose, run the tests, see which ones notice. It was done by hand, one
# mutation at a time, which is slow, tests one case where a tool tests
# hundreds, and has already gone wrong: in M18 the source was restored without
# rebuilding, and two measurements were published from the stale binary.
#
# Stryker does not have that failure mode. It is a development dependency and
# never ships: the product does not know it exists.
#
# A whole project is 4,287 mutants and takes hours, so this takes a file or a
# pattern and runs in minutes:
#
#   ./mutate.sh 'Features.cs'
#   ./mutate.sh '**/*.cs' LegacyLens.Characterization    # a whole project
#   ./mutate.sh '**/*.cs'                                # the long one
#
# Read the survivors, not the score. A surviving mutant is either a test
# nobody wrote or a change that makes no difference, and telling those two
# apart is the only part of this a person has to do.
set -euo pipefail

MOTIF="${1:-**/*.cs}"
PROJET="${2:-LegacyLens.Analysis}"
RACINE="$(cd "$(dirname "$0")" && pwd)"

dotnet tool restore >/dev/null

cd "$RACINE/tests/LegacyLens.Tests"

dotnet dotnet-stryker \
  --project "$PROJET.csproj" \
  --mutate "$MOTIF" \
  --reporter progress \
  --reporter json

RAPPORT="$(ls -dt StrykerOutput/*/reports/mutation-report.json | head -1)"

echo
echo "  Survivants :"
echo

python3 - "$RAPPORT" <<'PY'
import json, io, sys

report = json.load(io.open(sys.argv[1], encoding='utf-8'))

for path, info in report['files'].items():
    lines = info['source'].split('\n')

    for mutant in info['mutants']:
        if mutant['status'] != 'Survived':
            continue

        line = mutant['location']['start']['line']
        print(f"    {path.split('/')[-1]}:{line}  {mutant['mutatorName']}")
        print(f"      {lines[line - 1].strip()[:100]}")
PY
