# Generates the real, versioned EF Core migration for ApexERP.
#
# Why this exists: the migration files (C# code EF Core uses to create/update the database
# schema) must be generated once, by a machine that can reach nuget.org to restore packages,
# and then committed to source control like any other code. This script does that using a
# throwaway .NET SDK container -- you do NOT need the .NET SDK installed on your own machine.
#
# Usage (from the ApexERP root folder, the one containing docker-compose.yml):
#   .\scripts\generate-migrations.ps1
#
# Re-run any time you change an entity class under backend/src/ApexERP.Domain/Entities,
# giving it a descriptive name, e.g.:
#   .\scripts\generate-migrations.ps1 -MigrationName AddCustomerLoyaltyPoints

param(
    [string]$MigrationName = "InitialCreate"
)

$ErrorActionPreference = "Stop"
$backendPath = Join-Path $PSScriptRoot "..\backend"
$resolvedPath = (Resolve-Path $backendPath).Path

Write-Host "Generating migration '$MigrationName' using a throwaway .NET 8 SDK container..." -ForegroundColor Cyan
Write-Host "Mounting: $resolvedPath -> /src" -ForegroundColor DarkGray

docker run --rm `
  -v "${resolvedPath}:/src" `
  -w /src `
  mcr.microsoft.com/dotnet/sdk:8.0 `
  bash -c "dotnet tool install --global dotnet-ef --version 8.* > /dev/null 2>&1; export PATH=`"`$PATH:/root/.dotnet/tools`"; dotnet restore ApexERP.sln && dotnet ef migrations add $MigrationName -p src/ApexERP.Infrastructure -s src/ApexERP.API -o Data/Migrations"

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Migration files written to backend/src/ApexERP.Infrastructure/Data/Migrations/" -ForegroundColor Green
    Write-Host "Commit these files -- they are the versioned schema history for the project." -ForegroundColor Green
    Write-Host "Next: docker compose up --build (the API applies migrations automatically on startup)." -ForegroundColor Green
} else {
    Write-Host "Migration generation failed -- see the error output above." -ForegroundColor Red
}
