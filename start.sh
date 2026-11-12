#!/usr/bin/env bash
# ============================================================================
# FoldEdge one-command launcher (All-in-One server brain + Web UI)
#   ./start.sh
# The script builds/starts the container, waits for /api/health, then prints
# the dashboard address. FVP (the Windows renderer) is intentionally NOT here.
# ============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

CYAN='\033[36m'; GREEN='\033[32m'; YELLOW='\033[33m'; RED='\033[31m'
BOLD='\033[1m'; DIM='\033[2m'; RESET='\033[0m'

fail() { printf "${RED}✖ %s${RESET}\n" "$1" >&2; exit 1; }
info() { printf "${CYAN}▸${RESET} %s\n" "$1"; }

command -v docker >/dev/null 2>&1 || fail "Docker is not installed. Install Docker Engine or Docker Desktop first."
docker info >/dev/null 2>&1 || fail "Docker engine is not running. Start Docker, then retry."

COMPOSE=""
if docker compose version >/dev/null 2>&1; then
  COMPOSE="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE="docker-compose"
else
  fail "Neither 'docker compose' nor 'docker-compose' is available."
fi

info "Building and starting FoldEdge (All-in-One)…"
$COMPOSE up -d --build

info "Waiting for the health endpoint…"
READY=0
for _ in $(seq 1 60); do
  if command -v curl >/dev/null 2>&1; then
    if curl -fsS "http://127.0.0.1:9000/api/health" >/dev/null 2>&1; then
      READY=1
      break
    fi
  else
    if $COMPOSE ps 2>/dev/null | grep -q foldedge; then
      READY=1
      break
    fi
  fi
  sleep 2
done

printf "\n"
printf "${CYAN}${BOLD}  ┌────────────────────────────────────────────────────────────┐${RESET}\n"
printf "${CYAN}${BOLD}  │${RESET}  ${GREEN}${BOLD}FOLDEDGE IS ONLINE${RESET}\n"
printf "${CYAN}${BOLD}  ├────────────────────────────────────────────────────────────┤${RESET}\n"
printf "${CYAN}${BOLD}  │${RESET}  Web UI   ${BOLD}http://127.0.0.1:9000/dashboard${RESET}\n"
printf "${CYAN}${BOLD}  │${RESET}  API      http://127.0.0.1:9000/api/status\n"
printf "${CYAN}${BOLD}  │${RESET}  Logs     ${DIM}docker compose logs -f foldedge${RESET}\n"
printf "${CYAN}${BOLD}  │${RESET}  First run: create the Owner account in the browser.\n"
printf "${CYAN}${BOLD}  └────────────────────────────────────────────────────────────┘${RESET}\n"

if [ "$READY" -ne 1 ]; then
  printf "${YELLOW}⚠ Health check did not confirm within 120s. Run: docker compose logs -f foldedge${RESET}\n\n"
else
  printf "${DIM}  Keep port 9000 private; the dashboard has no built-in auth proxy.${RESET}\n\n"
fi
