#!/usr/bin/env bash
#
# Builds the one-file version: the API and the interface in a single executable
# that somebody can be handed and double-click.
#
# There is no second implementation of anything here. The same program is
# published self-contained, with the built interface beside it, so no runtime
# and no web server has to be installed to run it.
#
#   ./desktop.sh                 for this machine
#   ./desktop.sh win-x64         for Windows, which is where the code being
#                                modernised usually lives
#
set -euo pipefail

CIBLE="${1:-$(dotnet --info | awk -F': *' '/^ RID:/ {print $2}')}"
RACINE="$(cd "$(dirname "$0")" && pwd)"
INTERFACE="$RACINE/src/LegacyLens.Api/wwwroot"
SORTIE="$RACINE/build/desktop/$CIBLE"

echo "==> interface"
(cd "$RACINE/web" && npm run build --silent >/dev/null)

rm -rf "$INTERFACE"
mkdir -p "$INTERFACE"
cp -r "$RACINE/web/dist/web/browser/." "$INTERFACE/" 2>/dev/null \
  || cp -r "$RACINE/web/dist/web/." "$INTERFACE/"

echo "==> exécutable ($CIBLE)"
rm -rf "$SORTIE"
dotnet publish "$RACINE/src/LegacyLens.Api/LegacyLens.Api.csproj" \
    --configuration Release \
    --runtime "$CIBLE" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    --output "$SORTIE" \
    --nologo --verbosity quiet

# L'interface voyage à côté de l'exécutable plutôt que dedans : le fichier
# unique n'embarque pas wwwroot, et un dossier lisible à côté est de toute façon
# plus facile à inspecter pour qui reçoit le binaire.
rm -rf "$SORTIE/wwwroot"
cp -r "$INTERFACE" "$SORTIE/wwwroot"
rm -rf "$INTERFACE"

# Ce que la publication laisse et qui n'a rien à faire dans un dossier qu'on
# tend à quelqu'un : la description IIS et la carte des ressources statiques,
# que rien ne lit hors d'un serveur.
rm -f "$SORTIE/web.config" "$SORTIE/LegacyLens.Api.staticwebassets.endpoints.json"

# Le nom du programme, pas celui du projet. Ce qu'on reçoit s'appelle
# LegacyLens, pas LegacyLens.Api.
for EXT in "" ".exe"; do
  [ -f "$SORTIE/LegacyLens.Api$EXT" ] && mv "$SORTIE/LegacyLens.Api$EXT" "$SORTIE/LegacyLens$EXT"
done

# Les catalogues, qui sont de la donnée et pas du code : ce qui remplace quoi, et
# les décisions que le code ne peut pas prendre. Cherchés à côté du binaire, et
# sans eux la même analyse rend d'autres chiffres sans que rien ne le dise.
cp -r "$RACINE/data" "$SORTIE/data"

BINAIRE="$(find "$SORTIE" -maxdepth 1 -type f -name 'LegacyLens' -o -maxdepth 1 -type f -name 'LegacyLens.exe' | head -1)"
echo
echo "  $BINAIRE"
echo "  $(du -sh "$SORTIE" | cut -f1) au total, interface comprise"
echo
echo "  Le modèle n'est pas dedans. Sans Ollama, tout ce qui n'en demande pas"
echo "  fonctionne : la carte, le classement des risques, les conversions, ce qui"
echo "  bloque le portage et l'évaluation. Les questions-réponses, non."
