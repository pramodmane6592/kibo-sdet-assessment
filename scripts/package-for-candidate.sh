#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────
# package-for-candidate.sh
# Creates a clean zip file of the SDET challenge for a candidate.
# Excludes interviewer-only files, build artifacts, and IDE configs.
# ─────────────────────────────────────────────────────────────────

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$REPO_ROOT/dist"
TIMESTAMP=$(date +%Y%m%d)
ZIP_NAME="Kibo-SDET-Challenge-${TIMESTAMP}.zip"

echo "📦 Packaging SDET Challenge for candidates..."
echo "   Source: $REPO_ROOT"

# Create dist directory
mkdir -p "$OUTPUT_DIR"

# Build the zip, excluding internal/build files
cd "$REPO_ROOT"
zip -r "$OUTPUT_DIR/$ZIP_NAME" . \
    -x "INTERVIEWER_NOTES.md" \
    -x "README.md" \
    -x "scripts/*" \
    -x "dist/*" \
    -x ".git/*" \
    -x ".gitignore" \
    -x "**/bin/*" \
    -x "**/obj/*" \
    -x ".vs/*" \
    -x ".vscode/*" \
    -x ".idea/*" \
    -x ".DS_Store" \
    -x "*.user" \
    -x "TestResults/*" \
    -x "coverage*" \
    -x "msbuild.*"

echo ""
echo "✅ Done! Candidate package created:"
echo "   $OUTPUT_DIR/$ZIP_NAME"
echo ""
echo "   Contents excluded:"
echo "     - INTERVIEWER_NOTES.md, README.md"
echo "     - scripts/"
echo "     - dist/"
echo "     - .git/, .gitignore"
echo "     - bin/, obj/ (build artifacts)"
echo "     - IDE configs (.vs, .vscode, .idea)"
echo ""
echo "   Give the candidate this zip along with SETUP.md (included in zip)."
