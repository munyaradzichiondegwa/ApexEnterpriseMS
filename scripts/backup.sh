#!/usr/bin/env bash
# ApexERP database backup (APEXERP spec 29: backups and recovery).
#
# Writes a compressed, consistent pg_dump of the whole database, optionally encrypts it, records a checksum, and deletes
# backups older than the retention period. Run it from cron / the backup container at least daily (the interval is your RPO).
#
# Environment:
#   PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE   connection (standard libpq variables)
#   BACKUP_DIR            where to write (default ./backups)
#   BACKUP_PASSPHRASE     if set, the dump is encrypted with AES-256 (store this passphrase separately from the backups!)
#   BACKUP_RETENTION_DAYS how long to keep local backups (default 14)
#   BACKUP_COPY_CMD       optional command run after a successful backup with the file path as its argument, to copy it
#                         off the machine (e.g. "rclone copy {} remote:apexerp-backups"). A backup on the same disk as the
#                         database does not survive losing that disk.
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-./backups}"
RETENTION="${BACKUP_RETENTION_DAYS:-14}"
DB="${PGDATABASE:?PGDATABASE is required}"
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
file="$BACKUP_DIR/apexerp-${DB}-${stamp}.dump"

echo "[backup] dumping ${DB} -> ${file}"
pg_dump --format=custom --compress=6 --no-owner --no-privileges --file "$file" "$DB"

if [[ -n "${BACKUP_PASSPHRASE:-}" ]]; then
  openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -pass env:BACKUP_PASSPHRASE -in "$file" -out "$file.enc"
  shred -u "$file" 2>/dev/null || rm -f "$file"
  file="$file.enc"
  echo "[backup] encrypted -> ${file}"
fi

( cd "$BACKUP_DIR" && sha256sum "$(basename "$file")" > "$(basename "$file").sha256" )
chmod 600 "$file" "$file.sha256"
echo "[backup] $(du -h "$file" | cut -f1) written, checksum recorded"

if [[ -n "${BACKUP_COPY_CMD:-}" ]]; then
  echo "[backup] copying off-site"
  eval "${BACKUP_COPY_CMD//\{\}/\"$file\"}"
fi

find "$BACKUP_DIR" -type f \( -name 'apexerp-*.dump' -o -name 'apexerp-*.dump.enc' -o -name 'apexerp-*.sha256' \) -mtime +"$RETENTION" -delete
echo "[backup] done (kept ${RETENTION} days)"
