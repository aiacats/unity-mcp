# Auto-format staged C# files using dotnet format.
# Requires `dotnet` and a .sln on PATH / in repo. Failures do not block commits.
staged_cs=$(git diff --cached --name-only --diff-filter=ACM | grep -E '\.cs$' || true)
if [ -z "$staged_cs" ]; then
    exit 0
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[pre-commit] dotnet not found on PATH; skipping format."
    exit 0
fi

sln=$(ls *.sln 2>/dev/null | head -n 1)
if [ -z "$sln" ]; then
    sln=$(ls */*.sln 2>/dev/null | head -n 1)
fi
if [ -z "$sln" ]; then
    echo "[pre-commit] no .sln found; skipping format."
    exit 0
fi

include_args=""
for f in $staged_cs; do
    include_args="$include_args $f"
done

echo "[pre-commit] dotnet format $sln on staged C# files..."
dotnet format "$sln" --include $include_args --no-restore --verbosity quiet || \
    echo "[pre-commit] dotnet format reported issues; continuing commit."

# Re-stage any auto-formatted files
echo "$staged_cs" | xargs git add
