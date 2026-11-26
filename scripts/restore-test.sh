#!/usr/bin/env bash
# Proves a backup can actually be restored (APEXERP spec 29 / acceptance AR-013). A backup nobody has restored is a hope, not a backup.
#
# Restores the newest backup into a scratch database, checks it, measures how long the restore took (your real RTO for this data
# size) and how old the backup is (your real RPO), prints a report and removes the scratch database. Exits non-zero on any failure,
# so it can run in CI or cron and page someone.
#
# Environment: PGHOST PGPORT PGUSER PGPASSWORD (a user allowed to CREATE/DROP DATABASE), BACKUP_DIR, BACKUP_PASSPHRASE (if encrypted),
#              MAX_BACKUP_AGE_HOURS (default 26: fails if the newest backup is older), SCRATCH_DB (default apexerp_restore_test)
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-./backups}"
SCRATCH="${SCRATCH_DB:-apexerp_restore_test}"
MAX_AGE_H="${MAX_BACKUP_AGE_HOURS:-26}"

latest="$(ls -1t "$BACKUP_DIR"/apexerp-*.dump "$BACKUP_DIR"/apexerp-*.dump.enc 2>/dev/null | head -n1 || true)"
[[ -n "$latest" ]] || { echo "[restore-test] FAIL: no backups found in $BACKUP_DIR"; exit 1; }

age_h=$(( ( $(date +%s) - $(stat -c %Y "$latest") ) / 3600 ))
echo "[restore-test] newest backup: $latest (${age_h} h old)"
[[ $age_h -le $MAX_AGE_H ]] || { echo "[restore-test] FAIL: newest backup is older than ${MAX_AGE_H} h, so the recovery point objective is already missed"; exit 1; }

( cd "$BACKUP_DIR" && sha256sum --check --status "$(basename "$latest").sha256" ) || { echo "[restore-test] FAIL: checksum mismatch, the backup file is damaged or altered"; exit 1; }
echo "[restore-test] checksum OK"

work="$latest"; tmp=""
if [[ "$latest" == *.enc ]]; then
  : "${BACKUP_PASSPHRASE:?BACKUP_PASSPHRASE is required to restore an encrypted backup}"
  tmp="$(mktemp)"; trap '[[ -n "$tmp" ]] && shred -u "$tmp" 2>/dev/null || rm -f "$tmp"' EXIT
  openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass env:BACKUP_PASSPHRASE -in "$latest" -out "$tmp"
  work="$tmp"
fi

psql -v ON_ERROR_STOP=1 -q -d postgres -c "DROP DATABASE IF EXISTS \"$SCRATCH\"" -c "CREATE DATABASE \"$SCRATCH\""
start=$(date +%s)
pg_restore --no-owner --no-privileges --exit-on-error --dbname "$SCRATCH" "$work"
secs=$(( $(date +%s) - start ))
echo "[restore-test] restored in ${secs} s"

q() { psql -At -d "$SCRATCH" -c "$1"; }
tables=$(q "select count(*) from information_schema.tables where table_schema='public' and table_type='BASE TABLE'")
[[ "$tables" -gt 0 ]] || { echo "[restore-test] FAIL: the restored database has no tables"; exit 1; }

# The ledger must still balance after a restore: total debits equal total credits.
if [[ "$(q "select to_regclass('public.\"JournalLines\"') is not null")" == "t" ]]; then
  diff="$(q "select coalesce(sum(\"BaseDebit\"),0) - coalesce(sum(\"BaseCredit\"),0) from \"JournalLines\"")"
  [[ "$diff" == "0" || "$diff" == "0.000000" ]] || { echo "[restore-test] FAIL: restored ledger is out of balance by $diff"; exit 1; }
  echo "[restore-test] ledger balances ($(q "select count(*) from \"JournalLines\"") lines)"
fi
# The audit-log guard must have survived the restore.
if [[ "$(q "select to_regclass('public.\"AuditLogs\"') is not null")" == "t" ]]; then
  triggers="$(q "select count(*) from pg_trigger where tgrelid = 'public.\"AuditLogs\"'::regclass and not tgisinternal")"
  [[ "$triggers" -ge 1 ]] || echo "[restore-test] WARNING: the audit-log append-only trigger is missing from the restored database"
fi

psql -q -d postgres -c "DROP DATABASE \"$SCRATCH\""
echo "[restore-test] PASS: ${tables} tables, backup age ${age_h} h (RPO evidence), restore time ${secs} s (RTO evidence)"
