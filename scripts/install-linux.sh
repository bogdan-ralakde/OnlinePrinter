#!/usr/bin/env bash
# Online Printer Server installer for Linux (systemd: VM / bare metal / container).
#
# Installs LabelBridge as a systemd service. If a local binary/bundle is not
# provided or found next to this script, it automatically downloads the latest
# release asset from GitHub (repository must be public for unauthenticated download).
#
# Quick install (latest release):
#   curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-linux.sh | sudo bash
#
# Install a specific release:
#   curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-linux.sh | sudo LABELBRIDGE_VERSION=v1.1.0 bash
#
# Local install (checkout or extracted bundle):
#   sudo bash scripts/install-linux.sh
#   sudo bash scripts/install-linux.sh --local /path/to/LabelBridge.Relay
#
# Uninstall:
#   sudo bash scripts/install-linux.sh --uninstall [--purge]
#
# Compatibility: also available as scripts/install-lxc.sh.
#
# Environment:
#   LABELBRIDGE_REPO     GitHub repo (default: bogdan-ralakde/OnlinePrinter)
#   LABELBRIDGE_VERSION  Release tag or "latest" (default: latest)
#   LABELBRIDGE_ASSET    Asset filename (default: labelbridge-relay-linux-x64.tar.gz)
#
# Server provides: web UI + admin auth, client approval, printer config/scan/test,
# direct TCP/9100 printing, durable job queue, and driver/profile hosting APIs.
# Binary/service names are kept for upgrade compatibility.

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

REPO="${LABELBRIDGE_REPO:-bogdan-ralakde/OnlinePrinter}"
VERSION="${LABELBRIDGE_VERSION:-latest}"
ASSET="${LABELBRIDGE_ASSET:-labelbridge-relay-linux-x64.tar.gz}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}==>${NC} $*" >&2; }
ok()    { echo -e "${GREEN}OK${NC}  $*" >&2; }
warn()  { echo -e "${YELLOW}WARN${NC} $*" >&2; }
err()   { echo -e "${RED}ERROR${NC} $*" >&2; }
die()   { err "$*"; exit 1; }

usage() {
  cat <<EOF
LabelBridge installer (Linux, systemd)

Install latest release (auto-download):
  curl -fsSL https://raw.githubusercontent.com/${REPO}/master/scripts/install-linux.sh | sudo bash

Install from local binary/bundle:
  sudo bash install-linux.sh [--local /path/to/${BIN_NAME}]

Uninstall:
  sudo bash install-linux.sh --uninstall           # keep config/data
  sudo bash install-linux.sh --uninstall --purge   # remove everything

Env: LABELBRIDGE_REPO=${REPO}  LABELBRIDGE_VERSION=${VERSION}  LABELBRIDGE_ASSET=${ASSET}
EOF
}

need_root() { [[ ${EUID:-$(id -u)} -eq 0 ]] || die "Run as root (sudo bash install-linux.sh)"; }
have_cmd()  { command -v "$1" >/dev/null 2>&1; }

SCRIPT_DIR=""
if [[ -n "${BASH_SOURCE[0]:-}" && "${BASH_SOURCE[0]}" != "bash" && -e "${BASH_SOURCE[0]}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fi

TMPDIR_DL=""
cleanup() { [[ -n "$TMPDIR_DL" && -d "$TMPDIR_DL" ]] && rm -rf "$TMPDIR_DL" || true; }
trap cleanup EXIT

rand_secret() {
  if have_cmd openssl; then openssl rand -base64 32 | tr -d '\r\n/+=' | cut -c1-40;
  else tr -dc 'A-Za-z0-9' </dev/urandom | head -c 40; fi
}
get_env_value() { local k="$1" f="$2"; [[ -f "$f" ]] && grep -E "^${k}=" "$f" | tail -n1 | cut -d= -f2- || true; }
detect_ip() {
  local ip=""; if have_cmd hostname; then ip="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"; fi
  if [[ -z "$ip" ]] && have_cmd ip; then ip="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}' || true)"; fi
  printf '%s' "${ip:-<this-host>}"
}

fetch() {
  local url="$1" out="${2:-}"
  if have_cmd curl; then
    if [[ -n "$out" ]]; then curl -fL --retry 3 --connect-timeout 15 -o "$out" "$url"; else curl -fsSL --retry 3 --connect-timeout 15 "$url"; fi
  elif have_cmd wget; then
    if [[ -n "$out" ]]; then wget -qO "$out" "$url"; else wget -qO- "$url"; fi
  else
    die "curl or wget is required."
  fi
}

find_local_binary() {
  local arg="${1:-}"; local c
  local candidates=()
  if [[ -n "$arg" ]]; then
    if [[ -d "$arg" ]]; then
      candidates+=("$arg/$BIN_NAME" "$arg/bin/$BIN_NAME")
      local found_in_dir
      found_in_dir="$(find "$arg" -maxdepth 3 -type f -name "$BIN_NAME" 2>/dev/null | head -n1 || true)"
      [[ -n "$found_in_dir" ]] && candidates+=("$found_in_dir")
    else
      candidates+=("$arg")
    fi
  fi
  [[ -n "$SCRIPT_DIR" ]] && candidates+=("$SCRIPT_DIR/$BIN_NAME" "$SCRIPT_DIR/../dist/relay-linux-x64/$BIN_NAME" "$SCRIPT_DIR/../$BIN_NAME")
  candidates+=("$PWD/$BIN_NAME" "./$BIN_NAME")
  for c in "${candidates[@]}"; do
    [[ -f "$c" ]] || continue
    readlink -f "$c" 2>/dev/null || echo "$c"; return 0
  done
  return 1
}

download_release() {
  local found=""
  {
    TMPDIR_DL="$(mktemp -d /tmp/labelbridge-install.XXXXXX)"
    local base="https://github.com/${REPO}/releases"
    local asset_url="" api="https://api.github.com/repos/${REPO}/releases/latest"
    [[ "$VERSION" != "latest" ]] && api="https://api.github.com/repos/${REPO}/releases/tags/${VERSION}"

    info "Resolving release ${VERSION} from ${REPO}"
    local json=""
    if json="$(fetch "$api" 2>/dev/null)"; then
      asset_url="$(printf '%s' "$json" | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*'"${ASSET}"'"' | head -n1 | sed -E 's/.*"(https:[^"]+)".*/\1/' || true)"
      [[ -z "$asset_url" ]] && asset_url="$(printf '%s' "$json" | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*linux-x64[^"]*\.tar\.gz"' | head -n1 | sed -E 's/.*"(https:[^"]+)".*/\1/' || true)"
    fi
    if [[ -z "$asset_url" ]]; then
      if [[ "$VERSION" == "latest" ]]; then asset_url="${base}/latest/download/${ASSET}"; else asset_url="${base}/download/${VERSION}/${ASSET}"; fi
    fi

    info "Downloading: ${asset_url}"
    local archive="${TMPDIR_DL}/${ASSET}"
    if ! fetch "$asset_url" "$archive"; then
      die "Download failed. Ensure '${REPO}' is public and release asset '${ASSET}' exists (version: ${VERSION}). Or install locally: sudo bash install-linux.sh --local /path/to/${BIN_NAME}"
    fi

    case "$archive" in
      *.tar.gz|*.tgz) have_cmd tar || die "tar required"; tar -xzf "$archive" -C "$TMPDIR_DL" ;;
      *.zip)
        if have_cmd unzip; then unzip -q "$archive" -d "$TMPDIR_DL"; elif have_cmd busybox; then busybox unzip -q "$archive" -d "$TMPDIR_DL"; else die "unzip required"; fi ;;
      *) cp "$archive" "${TMPDIR_DL}/${BIN_NAME}" ;;
    esac
    found="$(find "$TMPDIR_DL" -type f -name "$BIN_NAME" | head -n1 || true)"
    [[ -n "$found" ]] || die "Archive did not contain ${BIN_NAME}"
    chmod +x "$found"
  } >&2
  printf '%s\n' "$found"
}

uninstall() {
  local purge="${1:-false}"; need_root
  info "Uninstalling ${APP_NAME}"
  if have_cmd systemctl; then systemctl stop "$APP_NAME" 2>/dev/null || true; systemctl disable "$APP_NAME" 2>/dev/null || true; fi
  rm -f "$SERVICE_FILE"; have_cmd systemctl && systemctl daemon-reload 2>/dev/null || true
  rm -rf "$INSTALL_DIR"
  if [[ "$purge" == "true" ]]; then
    rm -rf "$CONFIG_DIR" "$DATA_DIR"
    id -u "$SERVICE_USER" >/dev/null 2>&1 && userdel "$SERVICE_USER" 2>/dev/null || true
    ok "Removed service, binaries, config and data."
  else
    ok "Removed service and binaries. Kept: $CONFIG_DIR $DATA_DIR"
  fi
}

install_main() {
  local src_bin="$1"; need_root
  [[ -f "$src_bin" ]] || die "Binary not found: $src_bin"
  have_cmd systemctl || die "systemd required (systemctl not found)."

  local arch; arch="$(uname -m 2>/dev/null || echo unknown)"
  [[ "$arch" == "x86_64" || "$arch" == "amd64" ]] || warn "Binary targets linux-x64; arch is '$arch'."

  info "Installing LabelBridge"
  echo "  Source : $src_bin"
  echo "  Target : $INSTALL_DIR/$BIN_NAME"

  getent group "$SERVICE_GROUP" >/dev/null 2>&1 || groupadd --system "$SERVICE_GROUP"
  if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --gid "$SERVICE_GROUP" --home-dir "$INSTALL_DIR" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER" 2>/dev/null \
      || useradd --system --gid "$SERVICE_GROUP" --home-dir "$INSTALL_DIR" --no-create-home --shell /bin/false "$SERVICE_USER"
  fi

  systemctl is-active --quiet "$APP_NAME" 2>/dev/null && { info "Stopping running service"; systemctl stop "$APP_NAME"; }

  install -d -m 0755 "$INSTALL_DIR" "$CONFIG_DIR"
  install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$DATA_DIR"
  install -m 0755 "$src_bin" "$INSTALL_DIR/$BIN_NAME"
  ok "Binary installed"

  local admin_secret existing_secret
  existing_secret="$(get_env_value "LB_ADMIN_SECRET" "$ENV_FILE")"
  if [[ -f "$ENV_FILE" ]]; then
    admin_secret="$existing_secret"; ok "Keeping existing config $ENV_FILE"
  else
    admin_secret="${LB_ADMIN_SECRET:-}"
    if [[ -z "$admin_secret" && -t 0 && -t 1 ]]; then
      echo
      echo "Initial admin password setup"
      read -r -s -p "Create admin password (leave blank to set in web UI on first run): " admin_secret || true; echo
      if [[ -n "$admin_secret" ]]; then
        local admin_secret_confirm
        read -r -s -p "Confirm admin password: " admin_secret_confirm || true; echo
        if [[ "$admin_secret" != "$admin_secret_confirm" ]]; then
          err "Passwords do not match."
          exit 1
        fi
      fi
    elif [[ -z "$admin_secret" ]]; then
      info "No LB_ADMIN_SECRET provided and not running interactively; admin password will be created in the web UI on first run."
    fi
    cat > "$ENV_FILE" <<EOF
# Online Printer Server configuration
# Optional: pre-set the admin password. If left blank, open the web UI on first run to create the admin password.
LB_LISTEN=http://+:8080/
LB_DATA=${DATA_DIR}
LB_ADMIN_SECRET=${admin_secret}
LB_RETENTION_HOURS=168
LB_MAX_MB=128
LB_MAX_JOBS=2000
LB_REPO=${REPO}
EOF
    chmod 0640 "$ENV_FILE"; chown "root:${SERVICE_GROUP}" "$ENV_FILE" 2>/dev/null || true
    ok "Created config $ENV_FILE"
  fi

  cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=Online Printer Server
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
  systemctl enable "$APP_NAME" >/dev/null 2>&1 || true
  systemctl restart "$APP_NAME"
  ok "Service enabled and started"

  sleep 1
  local health_ok="false" ip listen_port
  "$INSTALL_DIR/$BIN_NAME" --health-check "http://127.0.0.1:8080/health" >/dev/null 2>&1 && health_ok="true" || true
  ip="$(detect_ip)"; listen_port="$(get_env_value "LB_LISTEN" "$ENV_FILE")"; [[ -n "$listen_port" ]] || listen_port="http://+:8080/"

  echo
  echo -e "${GREEN}Online Printer Server installed.${NC}"
  echo "  Service : $APP_NAME ($(systemctl is-active "$APP_NAME" 2>/dev/null || echo unknown))"
  echo "  Config  : $ENV_FILE"
  echo "  Data    : $DATA_DIR"
  echo "  Listen  : $listen_port"
  echo "  Web UI  : http://${ip}:8080/"
  echo "  Health  : http://${ip}:8080/health"
  [[ "$health_ok" == "true" ]] && echo -e "  Status  : ${GREEN}OK${NC}" || warn "Health not ready yet: journalctl -u $APP_NAME -n 80 --no-pager"
  echo "  Admin   : admin"
  if [[ -n "$admin_secret" ]]; then
    echo "  Initial admin password: (configured during install; stored in $ENV_FILE)"
  else
    echo "  Initial admin password: create it in the web UI on first run"
  fi
  echo
  echo "Manage: systemctl status $APP_NAME | journalctl -u $APP_NAME -f"
  echo "Next: open the web UI to complete initial admin setup/login (create password on first run if not preconfigured), configure printer, upload/select driver if needed, and approve Windows clients."
}

main() {
  local mode="install" purge="false" local_arg=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --uninstall) mode="uninstall"; shift ;;
      --purge) purge="true"; shift ;;
      --local) local_arg="${2:-}"; shift 2 ;;
      -h|--help) usage; exit 0 ;;
      --*) die "Unknown option: $1" ;;
      *) local_arg="$1"; shift ;;
    esac
  done
  if [[ "$mode" == "uninstall" ]]; then uninstall "$purge"; exit 0; fi

  local src_bin=""
  if src_bin="$(find_local_binary "$local_arg")"; then
    info "Using local binary: $src_bin"
  else
    info "No local binary found - downloading release (${REPO} ${VERSION})"
    src_bin="$(download_release)"
  fi
  install_main "$src_bin"
}

main "$@"