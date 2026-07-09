#!/usr/bin/env bash
# Generates the real, versioned EF Core migration for ApexERP.
# See generate-migrations.ps1 for the full explanation. Usage:
#   ./scripts/generate-migrations.sh [MigrationName]   (defaults to InitialCreate)
set -euo pipefail

MIGRATION_NAME="${1:-InitialCreate}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_PATH="$(cd "$SCRIPT_DIR/../backend" && pwd)"

echo "Generating migration '$MIGRATION_NAME' using a throwaway .NET 8 SDK container..."

docker run --rm \
  -v "$BACKEND_PATH:/src" \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c "dotnet tool install --global dotnet-ef --version 8.* > /dev/null 2>&1; export PATH=\"\$PATH:/root/.dotnet/tools\"; dotnet restore ApexERP.sln && dotnet ef migrations add $MIGRATION_NAME -p src/ApexERP.Infrastructure -s src/ApexERP.API -o Data/Migrations"

echo ""
echo "Migration files written to backend/src/ApexERP.Infrastructure/Data/Migrations/"
echo "Commit these files -- they are the versioned schema history for the project."
echo "Next: docker compose up --build (the API applies migrations automatically on startup)."
