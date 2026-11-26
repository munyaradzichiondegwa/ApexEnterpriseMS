#!/usr/bin/env bash
# Deploys a tagged image set to a host over SSH, waits for readiness, and rolls back automatically if the new version does not come up.
# Used by .github/workflows/deploy.yml; can also be run by hand.
#
# Environment: DEPLOY_HOST, DEPLOY_USER, DEPLOY_PATH (folder on the host holding docker-compose.yml, docker-compose.prod.yml and .env),
#              IMAGE_TAG (the version to deploy), SSH_KEY_FILE (private key), HEALTH_URL (e.g. https://erp.example.com/backend/health/ready)
set -euo pipefail
: "${DEPLOY_HOST:?}" "${DEPLOY_USER:?}" "${DEPLOY_PATH:?}" "${IMAGE_TAG:?}" "${SSH_KEY_FILE:?}" "${HEALTH_URL:?}"
ssh_cmd=(ssh -i "$SSH_KEY_FILE" -o StrictHostKeyChecking=yes "$DEPLOY_USER@$DEPLOY_HOST")

previous="$("${ssh_cmd[@]}" "cd '$DEPLOY_PATH' && grep -E '^IMAGE_TAG=' .env | cut -d= -f2 || true")"
echo "[deploy] current: ${previous:-none}  ->  new: $IMAGE_TAG"

switch() {   # $1 = tag
  "${ssh_cmd[@]}" "cd '$DEPLOY_PATH' && (grep -q '^IMAGE_TAG=' .env && sed -i 's/^IMAGE_TAG=.*/IMAGE_TAG=$1/' .env || echo 'IMAGE_TAG=$1' >> .env) \
    && docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env pull \
    && docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env up -d --remove-orphans"
}
ready() {
  for i in $(seq 1 40); do curl -fsS --max-time 5 "$HEALTH_URL" >/dev/null 2>&1 && return 0; sleep 5; done; return 1
}

# A backup is taken first so a bad migration can be recovered from.
"${ssh_cmd[@]}" "docker exec apexerp-backup bash /scripts/backup.sh" || echo "[deploy] WARNING: pre-deploy backup did not run"
switch "$IMAGE_TAG"
if ready; then echo "[deploy] new version is ready"; exit 0; fi

echo "[deploy] new version did not become ready"
if [[ -n "$previous" ]]; then echo "[deploy] rolling back to $previous"; switch "$previous"; ready && echo "[deploy] rolled back OK" || echo "[deploy] ROLLBACK ALSO FAILED: intervene now"; fi
exit 1
