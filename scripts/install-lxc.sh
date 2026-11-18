#!/usr/bin/env bash
# LabelBridge relay installer for Linux (LXC / VM / bare metal, systemd).
#
# Installs the LabelBridge relay as a systemd service.
# - Creates user/dirs, installs the self-contained relay binary
# - Generates /etc/labelbridge/relay.env (with a random admin secret on first install)
# - Installs and starts labelbridge-relay.service
#
# What this installs (current build):
#   * Relay HTTP API for LabelBridge host/client (store-and-forward print jobs)
#   * GET /health and a basic status page at /
# Not yet included in this build: a full admin web UI and server-side printer
# discovery/selection. See docs/lxc-install.md and README for architecture notes.
#
# Usage:
#   sudo bash scripts/install-lxc.sh [path/to/LabelBridge.Relay]
#   sudo bash scripts/install-lxc.sh --uninstall [--purge]
#
# Run from a repo checkout that contains dist/relay-linux-x64/LabelBridge.Relay,
# or from a directory that contains the LabelBridge.Relay binary.

set -euo pipefail

APP_NAME="labelbridge-relay"
SERVICE_USER="labelbridge"
SERVICE_GROUP="labelbridge"
INSTALL_DIR="/opt/labelbridge"
DATA_DIR="/var/lib/labelbridge"
CONFIG_DIR="/etc/labelbridge"
ENV_FILE="${CONFIG_DIR}/relay.env"
SERVICE_FILE="/etc/systemd/system/${APP_NAME}.service"
BIN_NAME="LabelBridge.Relay"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

info()  { echo -e "${CYAN}==>${NC} $*"; }
ok()    { echo -e "${GREEN}OK${NC}  $*"; }
warn()  { echo -e "${YELLOW}WARN${NC} $*"; }
err()   { echo -e "${RED}ERROR${NC} $*" >&2; }
die()   { err "$*"; exit 1; }

usage() {
  cat <<'EOF'
LabelBridge relay installer (Linux, systemd)

Install / upgrade:
  sudo bash scripts/install-lxc.sh [path/to/LabelBridge.Relay]

Uninstall:
  sudo bash scripts/install-lxc.sh --uninstall           # keep data & config
  sudo bash scripts/install-lxc.sh --uninstall --purge   # remove everything

Notes:
  - Installs a self-contained linux-x64 relay binary (no .NET runtime required).
  - Configuration: /etc/labelbridge/relay.env
  - Data (channels/queues): /var/lib/labelbridge
  - Service: systemd unit labelbridge-relay.service
EOF
}

need_root() {
  [[ ${EUID:-$(id -u)} -eq 0 ]] || die "Run as root (e.g. sudo bash scripts/install-lxc.sh)"
}

have_cmd() { command -v "$1" >/dev/null 2>&1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

find_binary() {
  local arg="${1:-}"
  local candidates=()
  if [[ -n "$arg" && "$arg" != --* ]]; then
    candidates+=("$arg")
  fi
  candidates+=(
    "${SCRIPT_DIR}/${BIN_NAME}"
    "${SCRIPT_DIR}/../dist/relay-linux-x64/${BIN_NAME}"
    "${SCRIPT_DIR}/../${BIN_NAME}"
    "${PWD}/${BIN_NAME}"
  )
  local c
  for c in "${candidates[@]}"; do
    if [[ -f "$c" ]]; then
      readlink -f "$c"
      return 0
    fi
  done
  return 1
}

rand_secret() {
  if have_cmd openssl; then
    openssl rand -base64 32 | tr -d '\r\n/+=' | cut -c1-40
  else
    tr -dc 'A-Za-z0-9' </dev/urandom | head -c 40
  fi
}

get_env_value() {
  local key="$1" file="$2" val=""
  if [[ -f "$file" ]]; then
    val="$(grep -E "^${key}=" "$file" | tail -n1 | cut -d= -f2- || true)"
  fi
  printf '%s' "$val"
}

detect_ip() {
  local ip=""
  if have_cmd hostname; then
    ip="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
  fi
  if [[ -z "$ip" ]] && have_cmd ip; then
    ip="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}' || true)"
  fi
  printf '%s' "${ip:-<this-host>}"
}

uninstall() {
  local purge="${1:-false}"
  need_root
  info "Uninstalling ${APP_NAME}"
  if have_cmd systemctl; then
    systemctl stop "${APP_NAME}" 2>/dev/null || true
    systemctl disable "${APP_NAME}" 2>/dev/null || true
  fi
  rm -f "${SERVICE_FILE}"
  if have_cmd systemctl; then systemctl daemon-reload 2>/dev/null || true; fi

  rm -rf "${INSTALL_DIR}"

  if [[ "$purge" == "true" ]]; then
    rm -rf "${CONFIG_DIR}" "${DATA_DIR}"
    if id -u "${SERVICE_USER}" >/dev/null 2>&1; then userdel "${SERVICE_USER}" 2>/dev/null || true; fi
    ok "Removed service, binaries, config and data."
  else
    ok "Removed service and binaries."
    echo "  Kept config: ${CONFIG_DIR}"
    echo "  Kept data  : ${DATA_DIR}"
    echo "  Re-run with --purge to remove everything."
  fi
}

install_main() {
  local src_bin="$1"
  need_root

  [[ -f "$src_bin" ]] || die "Binary not found: ${src_bin}"
  have_cmd systemctl || die "systemd is required (systemctl not found)."
  if ! pidof systemd >/dev/null 2>&1 && [[ ! -d /run/systemd/system ]]; then
    warn "systemd may not be running in this environment; service install can fail."
  fi

  local arch
  arch="$(uname -m 2>/dev/null || echo unknown)"
  if [[ "$arch" != "x86_64" && "$arch" != "amd64" ]]; then
    warn "This binary targets linux-x64; current architecture is '${arch}'."
  fi

  info "Installing LabelBridge relay"
  echo "  Source : ${src_bin}"
  echo "  Install: ${INSTALL_DIR}/${BIN_NAME}"

  # user/group
  if ! getent group "${SERVICE_GROUP}" >/dev/null 2>&1; then
    groupadd --system "${SERVICE_GROUP}"
  fi
  if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd --system --gid "${SERVICE_GROUP}" --home-dir "${INSTALL_DIR}" \
      --no-create-home --shell /usr/sbin/nologin "${SERVICE_USER}" 2>/dev/null \
    || useradd --system --gid "${SERVICE_GROUP}" --home-dir "${INSTALL_DIR}" \
      --no-create-home --shell /bin/false "${SERVICE_USER}"
  fi

  # stop running service before replacing binary
  local was_active="false"
  if systemctl is-active --quiet "${APP_NAME}" 2>/dev/null; then
    was_active="true"
    info "Stopping running service for upgrade"
    systemctl stop "${APP_NAME}"
  fi

  install -d -m 0755 "${INSTALL_DIR}" "${CONFIG_DIR}"
  install -d -o "${SERVICE_USER}" -g "${SERVICE_GROUP}" -m 0750 "${DATA_DIR}"
  install -d -o "${SERVICE_USER}" -g "${SERVICE_GROUP}" -m 0750 "${DATA_DIR}/channels"

  install -m 0755 "${src_bin}" "${INSTALL_DIR}/${BIN_NAME}"
  ok "Binary installed"

  # config/env (preserve existing)
  local existing_secret admin_secret created_env="false"
  existing_secret="$(get_env_value "LB_ADMIN_SECRET" "$ENV_FILE")"
  if [[ -f "$ENV_FILE" ]]; then
    ok "Keeping existing config ${ENV_FILE}"
    admin_secret="$existing_secret"
  else
    admin_secret="${LB_ADMIN_SECRET:-$(rand_secret)}"
    cat > "${ENV_FILE}" <<EOF
# LabelBridge relay configuration
# Edit and run: sudo systemctl restart ${APP_NAME}

# HTTP listener (terminate TLS with a reverse proxy in front for internet use)
LB_LISTEN=http://+:8080/

# Persistent data (channels, queued jobs)
LB_DATA=${DATA_DIR}

# Required in X-LB-Admin when creating channels from the Windows host app.
# Treat as a secret. Change it, then restart the service.
LB_ADMIN_SECRET=${admin_secret}

# Limits / retention
LB_RETENTION_HOURS=168
LB_MAX_MB=64
LB_MAX_JOBS=1000
EOF
    chmod 0640 "${ENV_FILE}"
    chown "root:${SERVICE_GROUP}" "${ENV_FILE}" || true
    created_env="true"
    ok "Created config ${ENV_FILE} (new admin secret generated)"
  fi

  # systemd unit (overwrite on upgrade)
  cat > "${SERVICE_FILE}" <<EOF
[Unit]
Description=LabelBridge print relay
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_GROUP}
WorkingDirectory=${INSTALL_DIR}
EnvironmentFile=-${ENV_FILE}
ExecStart=${INSTALL_DIR}/${BIN_NAME}
Restart=always
RestartSec=3
# hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=${DATA_DIR}
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

  systemctl daemon-reload
  systemctl enable "${APP_NAME}" >/dev/null 2>&1 || true
  systemctl restart "${APP_NAME}"
  ok "Service enabled and started"

  # health check
  local health_ok="false"
  sleep 1
  if "${INSTALL_DIR}/${BIN_NAME}" --health-check "http://127.0.0.1:8080/health" >/dev/null 2>&1; then
    health_ok="true"
  fi

  local ip listen_port
  ip="$(detect_ip)"
  listen_port="$(get_env_value "LB_LISTEN" "$ENV_FILE")"
  [[ -n "$listen_port" ]] || listen_port="http://+:8080/"

  echo
  echo -e "${GREEN}LabelBridge relay installed.${NC}"
  echo
  echo "  Service    : ${APP_NAME} ($(systemctl is-active "${APP_NAME}" 2>/dev/null || echo unknown))"
  echo "  Binary     : ${INSTALL_DIR}/${BIN_NAME}"
  echo "  Config     : ${ENV_FILE}"
  echo "  Data       : ${DATA_DIR}"
  echo "  Listen     : ${listen_port}"
  echo "  Health     : http://127.0.0.1:8080/health (local)"
  echo "  Status URL : http://${ip}:8080/  (basic status page)"
  if [[ "$health_ok" == "true" ]]; then
    echo -e "  Health chk : ${GREEN}OK${NC}"
  else
    warn "Health check did not respond yet; check: journalctl -u ${APP_NAME} -n 50 --no-pager"
  fi
  echo
  if [[ -n "$admin_secret" ]]; then
    echo "  Admin secret (for creating channels in the Windows host app):"
    echo "    ${admin_secret}"
    echo "  Stored in: ${ENV_FILE} (LB_ADMIN_SECRET)"
  else
    echo "  Admin secret: (not set) - edit ${ENV_FILE} and restart to require one."
  fi
  echo
  echo "Next steps:"
  echo "  1. Put HTTPS in front of this relay for internet use (Caddy/nginx/Traefik/Cloudflare)."
  echo "     Long-poll proxying: disable buffering and allow read timeout >= 120s."
  echo "  2. On the printer-side Windows PC run LabelBridge (Host) -> Connection tab:"
  echo "     set Relay URL (https://...), enter the admin secret, Test relay, Create channel."
  echo "  3. Copy the pairing code to remote Windows PCs running LabelBridge (Client)."
  echo
  echo "Notes (current build):"
  echo "  - This installs the relay service + HTTP API + basic status/health page."
  echo "  - A full admin web UI and server-side printer discovery/selection are not yet"
  echo "    implemented. Printer selection is done in the Windows host/client app today."
  echo
  echo "Manage:"
  echo "  systemctl status ${APP_NAME}"
  echo "  journalctl -u ${APP_NAME} -f"
  echo "  sudo bash scripts/install-lxc.sh --uninstall        # keep data/config"
  echo "  sudo bash scripts/install-lxc.sh --uninstall --purge"
}

main() {
  local mode="install" purge="false" bin_arg=""

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --uninstall) mode="uninstall"; shift ;;
      --purge) purge="true"; shift ;;
      -h|--help) usage; exit 0 ;;
      --*) die "Unknown option: $1 (use --help)" ;;
      *) bin_arg="$1"; shift ;;
    esac
  done

  if [[ "$mode" == "uninstall" ]]; then
    uninstall "$purge"
    exit 0
  fi

  local src_bin
  if ! src_bin="$(find_binary "$bin_arg")"; then
    err "Could not find ${BIN_NAME}."
    echo "Provide the path to the binary, or run from a checkout with dist/relay-linux-x64/${BIN_NAME}."
    echo "Build it on Windows: powershell -ExecutionPolicy Bypass -File installer\\Build.ps1 -SkipZip"
    exit 1
  fi

  install_main "$src_bin"
}

main "$@"