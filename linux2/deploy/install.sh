#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

PREFIX="${PHONESHELL_PREFIX:-/opt/phoneshell}"
CONFIG_PATH="${PHONESHELL_CONFIG:-/etc/phoneshell/config.json}"
PORT="${PHONESHELL_PORT:-19090}"
WEB_PANEL="${PHONESHELL_WEB_PANEL:-true}"
MODE="${PHONESHELL_MODE:-standalone}"
PUBLIC_HOST="${PHONESHELL_PUBLIC_HOST:-}"
RELAY_URL="${PHONESHELL_RELAY_URL:-}"
GROUP_SECRET="${PHONESHELL_GROUP_SECRET:-}"
PANEL_PORT="${PHONESHELL_PANEL_PORT:-9090}"
SKIP_BUILD=0
SKIP_WEB=0
NON_INTERACTIVE=0

usage() {
  cat <<EOF
PhoneShell Linux Install

Usage: sudo ./install.sh [options]

Options:
  --prefix <dir>       Install prefix (default: /opt/phoneshell)
  --config <path>      Config file path (default: /etc/phoneshell/config.json)
  --port <port>        Listen port (default: 19090)
  --web-panel <bool>   Enable web panel (true/false, default: true)
  --panel-port <port>  Web panel port (default: 9090)
  --mode <mode>        standalone|server|client (default: standalone)
  --public-host <host> Public hostname (optional)
  --skip-build         Skip TypeScript build
  --skip-web           Skip web build
  --non-interactive    Do not prompt
  --help               Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --prefix) PREFIX="$2"; shift 2;;
    --config) CONFIG_PATH="$2"; shift 2;;
    --port) PORT="$2"; shift 2;;
    --web-panel) WEB_PANEL="$2"; shift 2;;
    --panel-port) PANEL_PORT="$2"; shift 2;;
    --mode) MODE="$2"; shift 2;;
    --public-host) PUBLIC_HOST="$2"; shift 2;;
    --skip-build) SKIP_BUILD=1; shift 1;;
    --skip-web) SKIP_WEB=1; shift 1;;
    --non-interactive) NON_INTERACTIVE=1; shift 1;;
    --help|-h) usage; exit 0;;
    *) echo "Unknown option: $1"; usage; exit 1;;
  esac
done

if [[ $(id -u) -ne 0 ]]; then
  echo "Please run as root (sudo)."
  exit 1
fi

if [[ $NON_INTERACTIVE -eq 0 ]]; then
  read -r -p "Install prefix [${PREFIX}]: " input
  if [[ -n "${input}" ]]; then PREFIX="${input}"; fi

  read -r -p "Config path [${CONFIG_PATH}]: " input
  if [[ -n "${input}" ]]; then CONFIG_PATH="${input}"; fi

  read -r -p "Listen port [${PORT}]: " input
  if [[ -n "${input}" ]]; then PORT="${input}"; fi

  read -r -p "Enable web panel (true/false) [${WEB_PANEL}]: " input
  if [[ -n "${input}" ]]; then WEB_PANEL="${input}"; fi

  read -r -p "Mode (standalone/server/client) [${MODE}]: " input
  if [[ -n "${input}" ]]; then MODE="${input}"; fi

  if [[ "$(echo "${WEB_PANEL}" | tr '[:upper:]' '[:lower:]')" == "true" ]]; then
    read -r -p "Web panel port [${PANEL_PORT}]: " input
    if [[ -n "${input}" ]]; then PANEL_PORT="${input}"; fi
  fi

  read -r -p "Public host (optional) [${PUBLIC_HOST}]: " input
  if [[ -n "${input}" ]]; then PUBLIC_HOST="${input}"; fi

  if [[ "$(echo "${MODE}" | tr '[:upper:]' '[:lower:]')" == "client" ]]; then
    read -r -p "Relay URL (ws://host:port/ws/) [${RELAY_URL}]: " input
    if [[ -n "${input}" ]]; then RELAY_URL="${input}"; fi
    read -r -p "Group secret (optional) [${GROUP_SECRET}]: " input
    if [[ -n "${input}" ]]; then GROUP_SECRET="${input}"; fi
  fi
fi

if ! command -v node >/dev/null 2>&1; then
  echo "Node.js is required but not found. Install Node.js >= 18 and retry."
  exit 1
fi
if ! command -v npm >/dev/null 2>&1; then
  echo "npm is required but not found. Install npm and retry."
  exit 1
fi

PORT_NUM="${PORT}"
if ! [[ "$PORT_NUM" =~ ^[0-9]+$ ]] || [[ "$PORT_NUM" -lt 1 ]] || [[ "$PORT_NUM" -gt 65535 ]]; then
  echo "Invalid port: ${PORT}"
  exit 1
fi

WEB_PANEL_LOWER="$(echo "${WEB_PANEL}" | tr '[:upper:]' '[:lower:]')"
if [[ "$WEB_PANEL_LOWER" != "true" && "$WEB_PANEL_LOWER" != "false" ]]; then
  echo "Invalid web-panel value: ${WEB_PANEL}. Use true/false."
  exit 1
fi

PANEL_PORT_NUM="${PANEL_PORT}"
if ! [[ "$PANEL_PORT_NUM" =~ ^[0-9]+$ ]] || [[ "$PANEL_PORT_NUM" -lt 1 ]] || [[ "$PANEL_PORT_NUM" -gt 65535 ]]; then
  echo "Invalid panel port: ${PANEL_PORT}"
  exit 1
fi

MODE_LOWER="$(echo "${MODE}" | tr '[:upper:]' '[:lower:]')"
if [[ "$MODE_LOWER" != "standalone" && "$MODE_LOWER" != "server" && "$MODE_LOWER" != "client" ]]; then
  echo "Invalid mode: ${MODE}. Use standalone/server/client."
  exit 1
fi

NODE_BIN="$(command -v node)"

RELAY_SERVER=true
RELAY_CLIENT=false
if [[ "$MODE_LOWER" == "client" ]]; then
  RELAY_SERVER=false
  RELAY_CLIENT=true
fi

echo "[1/7] Installing dependencies..."
pushd "${SRC_DIR}" >/dev/null
npm ci
popd >/dev/null

if [[ $SKIP_BUILD -eq 0 ]]; then
  echo "[2/7] Building server..."
  pushd "${SRC_DIR}" >/dev/null
  npm run build
  popd >/dev/null
fi

if [[ $SKIP_WEB -eq 0 && -d "${SRC_DIR}/web" ]]; then
  echo "[3/7] Building web panel..."
  pushd "${SRC_DIR}/web" >/dev/null
  npm ci
  npm run build
  popd >/dev/null
else
  echo "[3/7] Skipping web panel build..."
fi

echo "[4/7] Installing to ${PREFIX}..."
mkdir -p "${PREFIX}"
if command -v rsync >/dev/null 2>&1; then
  rsync -a --delete "${SRC_DIR}/dist/" "${PREFIX}/dist/"
else
  rm -rf "${PREFIX}/dist"
  cp -a "${SRC_DIR}/dist" "${PREFIX}/dist"
fi
mkdir -p "${PREFIX}/web"
if [[ -d "${SRC_DIR}/web/dist" ]]; then
  if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete "${SRC_DIR}/web/dist/" "${PREFIX}/web/dist/"
  else
    rm -rf "${PREFIX}/web/dist"
    cp -a "${SRC_DIR}/web/dist" "${PREFIX}/web/dist"
  fi
fi
cp -f "${SRC_DIR}/package.json" "${SRC_DIR}/package-lock.json" "${PREFIX}/"

echo "[5/7] Installing runtime dependencies..."
pushd "${PREFIX}" >/dev/null
npm ci --omit=dev
popd >/dev/null

CONFIG_DIR="$(dirname "${CONFIG_PATH}")"
BASE_DIR="${CONFIG_DIR}"
mkdir -p "${CONFIG_DIR}"

if [[ ! -f "${CONFIG_PATH}" ]]; then
  cat > "${CONFIG_PATH}" <<EOF
{
  "displayName": "",
  "publicHost": "${PUBLIC_HOST}",
  "port": ${PORT_NUM},
  "panelPort": ${PANEL_PORT_NUM},
  "relayUrl": "${RELAY_URL}",
  "relayAuthToken": "",
  "groupSecret": "${GROUP_SECRET}",
  "defaultCols": 120,
  "defaultRows": 30,
  "modules": {
    "terminal": true,
    "relayServer": ${RELAY_SERVER},
    "relayClient": ${RELAY_CLIENT},
    "webPanel": ${WEB_PANEL_LOWER},
    "aiChat": false
  },
  "baseDirectory": "${BASE_DIR}",
  "mode": "${MODE_LOWER}"
}
EOF
  chmod 600 "${CONFIG_PATH}"
  echo "[config] Created ${CONFIG_PATH}"
else
  echo "[config] Existing config preserved at ${CONFIG_PATH}"
fi

echo "[6/7] Installing systemd service..."
SERVICE_PATH="/etc/systemd/system/phoneshell.service"
cat > "${SERVICE_PATH}" <<EOF
[Unit]
Description=PhoneShell Remote Terminal Service
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=${PREFIX}
ExecStart=${NODE_BIN} ${PREFIX}/dist/index.js --config ${CONFIG_PATH}
Restart=always
RestartSec=5
Environment=NODE_ENV=production

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable phoneshell
systemctl restart phoneshell

echo "[7/7] Installing helper command..."
install -m 755 "${SCRIPT_DIR}/phoneshell" /usr/local/bin/phoneshell

echo "Done. Service is running on port ${PORT_NUM}."
if [[ "${WEB_PANEL_LOWER}" == "true" ]]; then
  if [[ "${PANEL_PORT_NUM}" -ne "${PORT_NUM}" ]]; then
    echo "Panel: http://127.0.0.1:${PANEL_PORT_NUM}/panel/"
  else
    echo "Panel: http://127.0.0.1:${PORT_NUM}/panel/"
  fi
fi
echo "CLI: phoneshell local"
