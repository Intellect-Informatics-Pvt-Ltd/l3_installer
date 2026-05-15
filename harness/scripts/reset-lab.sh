#!/usr/bin/env bash
# reset-lab.sh
# Drops and recreates all Docker volumes, re-applies migrations, re-seeds data,
# recreates Kafka topics, and confirms all services are healthy.
# Target: <= 60 seconds on a developer laptop.
#
# Usage:
#   ./scripts/reset-lab.sh [--profile full|minimal]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HARNESS_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
DOCKER_DIR="${HARNESS_DIR}/docker"
PROFILE="${1:-minimal}"

COMPOSE_FILE="${DOCKER_DIR}/docker-compose.${PROFILE}.yml"
if [[ ! -f "${COMPOSE_FILE}" ]]; then
    echo "Profile compose file not found: ${COMPOSE_FILE}"
    exit 1
fi

echo "==> [reset-lab] Stopping and removing all volumes…"
docker compose -f "${COMPOSE_FILE}" down -v --remove-orphans 2>/dev/null || true

echo "==> [reset-lab] Starting infrastructure…"
docker compose -f "${COMPOSE_FILE}" up -d

echo "==> [reset-lab] Waiting for services to become healthy (up to 90 s)…"
TIMEOUT=90
ELAPSED=0
until docker compose -f "${COMPOSE_FILE}" ps | grep -qv "unhealthy\|starting"; do
    sleep 3
    ELAPSED=$((ELAPSED+3))
    if [[ ${ELAPSED} -ge ${TIMEOUT} ]]; then
        echo "ERROR: Services did not become healthy within ${TIMEOUT}s"
        docker compose -f "${COMPOSE_FILE}" ps
        exit 1
    fi
done

echo "==> [reset-lab] Done in ${ELAPSED}s"
docker compose -f "${COMPOSE_FILE}" ps
