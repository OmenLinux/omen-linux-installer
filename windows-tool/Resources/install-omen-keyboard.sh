#!/usr/bin/env bash
# Post-install helper shipped on omen-prep patched ISOs.
# Run once after Linux is installed and you're booted into the new system:
#   sudo bash /path/to/install-omen-keyboard.sh
# (If this file is still on the install USB, mount it and use that path.)
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Please run as root:  sudo bash $0"
  exit 1
fi

REPO_URL="${OMEN_RGB_KEYBOARD_REPO:-https://github.com/OmenLinux/omen-rgb-keyboard.git}"
WORKDIR="$(mktemp -d /tmp/omen-rgb-keyboard.XXXXXX)"
cleanup() { rm -rf "${WORKDIR}"; }
trap cleanup EXIT

echo "==> Installing build dependencies (dkms, headers, git)…"
if command -v pacman >/dev/null 2>&1; then
  pacman -Sy --noconfirm --needed dkms git base-devel alsa-lib linux-headers || \
    pacman -Sy --noconfirm --needed dkms git base-devel alsa-lib
elif command -v apt-get >/dev/null 2>&1; then
  apt-get update
  apt-get install -y dkms git build-essential "linux-headers-$(uname -r)" libasound2-dev || \
    apt-get install -y dkms git build-essential "linux-headers-$(uname -r)" libasound2t64
elif command -v dnf >/dev/null 2>&1; then
  dnf install -y dkms git kernel-devel kernel-headers "@development-tools" alsa-lib-devel
else
  echo "Couldn't detect pacman/apt/dnf. Install dkms, git, kernel headers, then re-run."
  exit 1
fi

echo "==> Cloning ${REPO_URL}…"
git clone --depth=1 "${REPO_URL}" "${WORKDIR}/src"
cd "${WORKDIR}/src"

# Stop hp_wmi fighting this driver for WMI events (upstream recommendation).
if lsmod | grep -q '^hp_wmi'; then
  echo "==> Unloading hp_wmi…"
  modprobe -r hp_wmi 2>/dev/null || true
fi
if [[ -f hp_wmi-blacklist.conf ]]; then
  echo "==> Blacklisting hp_wmi…"
  cp -f hp_wmi-blacklist.conf /etc/modprobe.d/blacklist-hp-wmi.conf
elif [[ -f /etc/modprobe.d/blacklist-hp-wmi.conf ]]; then
  :
else
  echo 'blacklist hp_wmi' > /etc/modprobe.d/blacklist-hp-wmi.conf
fi

echo "==> Running upstream install.sh…"
bash ./install.sh

echo ""
echo "Done. RGB keyboard driver installed via DKMS."
echo "If Secure Boot blocked the module, enroll a MOK and rebuild — see omen-rgb-keyboard README."
