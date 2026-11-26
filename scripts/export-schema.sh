#!/usr/bin/env bash
# Writes the complete database schema as one idempotent SQL script, generated from the EF Core migrations (the single source of truth).
# Use it for DBAs, audits, or bootstrapping a database without the .NET SDK. It replaces the old hand-written schema.sql, which drifted.
#   ./scripts/export-schema.sh            -> backend/database/schema.generated.sql
set -euo pipefail
cd "$(dirname "$0")/../backend"
dotnet ef migrations script --idempotent -p src/ApexERP.Infrastructure -s src/ApexERP.API -o database/schema.generated.sql
echo "Wrote backend/database/schema.generated.sql"
