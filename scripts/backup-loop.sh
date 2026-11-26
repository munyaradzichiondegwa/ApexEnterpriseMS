#!/usr/bin/env bash
# Runs scripts/backup.sh every day at BACKUP_AT (HH:MM, container time = UTC; default 01:30). Used by the "backup" service in docker-compose.prod.yml.
set -uo pipefail
AT="${BACKUP_AT:-01:30}"
while true; do
  now=$(date +%s)
  target=$(date -d "today $AT" +%s)
  [ "$target" -le "$now" ] && target=$(date -d "tomorrow $AT" +%s)
  echo "[backup-loop] next backup at $(date -d "@$target" -u '+%F %T') UTC"
  sleep $((target - now))
  bash /scripts/backup.sh || echo "[backup-loop] BACKUP FAILED"
done
