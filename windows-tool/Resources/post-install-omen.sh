#!/usr/bin/env bash
# Run on the installed system (omen-prep USB mounted):
#   sudo bash /run/media/$USER/*/post-install-omen.sh
#   sudo bash …/post-install-omen.sh --rgb-only
#   sudo bash …/post-install-omen.sh --acpi-only
#   sudo bash …/post-install-omen.sh --gpu-only
#
# Fedora: dracut acpi_override (same approach as
#   https://github.com/suikan4github/u9311-acpi-patch-fedora ).
# Ubuntu/Debian: GRUB early initrd with omen-acpi.cpio.
# Others: initramfs prepend fallback.
# Also forces Intel iGPU probe (i915) when the kernel leaves the panel on
# simpledrm — common on Alder Lake Omen + recent Fedora kernels.
set -euo pipefail

die() { echo "error: $*" >&2; exit 1; }
log() { echo "==> $*"; }
warn() { echo "warn: $*" >&2; }
bytes() { wc -c < "$1" | tr -d '[:space:]'; }

DO_ACPI=1
DO_RGB=1
DO_GPU=1
for arg in "$@"; do
  case "$arg" in
    --rgb-only)  DO_ACPI=0; DO_GPU=0 ;;
    --acpi-only) DO_RGB=0; DO_GPU=0 ;;
    --gpu-only)  DO_ACPI=0; DO_RGB=0; DO_GPU=1 ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
  esac
done

[[ "${EUID}" -eq 0 ]] || die "run as root: sudo bash $0"

SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "$0" 2>/dev/null || echo "$0")")" && pwd)"
KVER="$(uname -r)"
OMEN_DIR="/etc/omen-prep"
ACPI_SHARE="/usr/share/omen-prep/acpi"
DRACUT_CONF="/usr/lib/dracut/dracut.conf.d/99-omen-acpi.conf"
BOOT_CPIO="/boot/omen-acpi.cpio"
REPO_URL="${OMEN_RGB_KEYBOARD_REPO:-https://github.com/OmenLinux/omen-rgb-keyboard.git}"
WORKDIR="$(mktemp -d /tmp/omen-post-install.XXXXXX)"

IS_FEDORA=0
IS_DEBIAN=0
grep -qiE '^ID=fedora' /etc/os-release 2>/dev/null && IS_FEDORA=1
[[ -f /etc/fedora-release ]] && IS_FEDORA=1
grep -qiE '^(ID|ID_LIKE)=.*(debian|ubuntu)' /etc/os-release 2>/dev/null && IS_DEBIAN=1

cleanup() { rm -rf "${WORKDIR}"; }
trap cleanup EXIT

# ── locate dsdt.aml / omen-acpi.cpio on USB ──────────────────────────────────

find_file() {
  local name="$1" c f
  for c in \
    "${SCRIPT_DIR}/${name}" \
    /run/media/*/*/"${name}" /media/*/*/"${name}" \
    "${OMEN_DIR}/${name}" /boot/"${name}"
  do
    for f in $c; do
      [[ -f "$f" ]] && { echo "$f"; return 0; }
    done
  done
  return 1
}

extract_dsdt_from_cpio() {
  local cpio="$1" out="$2" stage="${WORKDIR}/cpio-extract"
  rm -rf "${stage}"
  mkdir -p "${stage}"
  (cd "${stage}" && cpio -id --quiet < "$cpio")
  local aml
  aml=$(find "${stage}" -name 'dsdt.aml' -o -name 'DSDT.aml' | head -n1)
  [[ -n "$aml" && -f "$aml" ]] || return 1
  cp -f "$aml" "$out"
}

ensure_dsdt() {
  mkdir -p "${OMEN_DIR}" "${ACPI_SHARE}"
  local src
  if src="$(find_file dsdt.aml)" || src="$(find_file DSDT.aml)"; then
    cp -f "$src" "${ACPI_SHARE}/dsdt.aml"
    log "DSDT from ${src}"
  elif src="$(find_file omen-acpi.cpio)"; then
    extract_dsdt_from_cpio "$src" "${ACPI_SHARE}/dsdt.aml" \
      || die "could not extract dsdt.aml from ${src}"
    cp -f "$src" "${OMEN_DIR}/omen-acpi.cpio"
    log "DSDT extracted from ${src}"
  else
    die "Mount the omen-prep USB (need dsdt.aml or omen-acpi.cpio)."
  fi
  [[ -s "${ACPI_SHARE}/dsdt.aml" ]] || die "dsdt.aml empty"
  chmod 644 "${ACPI_SHARE}/dsdt.aml"
  # Keep a copy for non-Fedora paths / debugging
  cp -f "${ACPI_SHARE}/dsdt.aml" "${OMEN_DIR}/dsdt.aml"
}

# Tear down older omen-prep experiments (prepend hooks, GRUB early, BLS hacks).
scrub_old_acpi_methods() {
  log "Removing old omen-prep ACPI hooks / GRUB early / BLS hacks…"
  rm -f \
    /etc/kernel/postinst.d/zz-omen-acpi \
    /etc/kernel/postinst.d/omen-acpi \
    /etc/initramfs/post-update.d/omen-acpi \
    /etc/kernel/install.d/99-omen-acpi.install \
    /etc/kernel/install.d/99-omen-acpi-prepend.install \
    /etc/kernel/install.d/91-omen-early-initrd.install \
    "${OMEN_DIR}/prepend-initrd.sh" \
    /etc/default/grub.d/omen-acpi.cfg \
    /etc/dracut.conf.d/99-omen-acpi.conf

  if [[ -f /etc/default/grub ]]; then
    sed -i -E '/^GRUB_EARLY_INITRD_LINUX_CUSTOM=.*omen-acpi/d' /etc/default/grub || true
    sed -i -E 's/[[:space:]]*init=\/omen-prep\/[^[:space:]]*//g' /etc/default/grub || true
  fi
  command -v grub2-editenv >/dev/null 2>&1 && grub2-editenv - unset early_initrd 2>/dev/null || true

  local dir e
  for dir in /boot/loader/entries /boot/efi/loader/entries; do
    [[ -d "$dir" ]] || continue
    for e in "$dir"/*.conf; do
      [[ -f "$e" ]] || continue
      grep -q 'omen-acpi.cpio' "$e" 2>/dev/null || continue
      grep -v -E '^initrd[[:space:]]+/omen-acpi\.cpio$' "$e" > "${e}.omenstrip"
      mv -f "${e}.omenstrip" "$e"
    done
  done
}

# ── Fedora: dracut acpi_override (Fujitsu U9311 method) ──────────────────────

install_acpi_fedora() {
  log "Fedora: installing DSDT via dracut acpi_override…"
  ensure_dsdt
  scrub_old_acpi_methods

  mkdir -p /usr/lib/dracut/dracut.conf.d
  # Same knobs as u9311-acpi-patch-fedora's 99-acpi-override.conf
  cat > "${DRACUT_CONF}" << EOF
# omen-prep — ACPI DSDT override (do not prepend into initramfs by hand)
acpi_override="yes"
acpi_table_dir="${ACPI_SHARE}"
EOF
  chmod 644 "${DRACUT_CONF}"
  log "Wrote ${DRACUT_CONF}"

  command -v dracut >/dev/null 2>&1 || die "dracut not found"
  log "Regenerating initramfs with dracut --force (all kernels)…"
  # Rebuild every installed kernel image so none keep a broken manual prepend.
  if dracut --force --regenerate-all 2>/dev/null; then
    :
  else
    dracut --force -k "${KVER}" || dracut --force
  fi

  log "Fedora ACPI done. After reboot check: journalctl -k | grep -i 'Table Upgrade'"
}

# ── Ubuntu/Debian: GRUB early initrd ─────────────────────────────────────────

build_cpio_from_dsdt() {
  local aml="$1" out="$2" stage="${WORKDIR}/acpi-cpio" sz pad
  rm -rf "${stage}"
  mkdir -p "${stage}/kernel/firmware/acpi"
  cp -f "$aml" "${stage}/kernel/firmware/acpi/dsdt.aml"
  (cd "${stage}" && find kernel | cpio -o -H newc --quiet) > "${out}.raw"
  sz=$(bytes "${out}.raw")
  pad=$(( (512 - (sz % 512)) % 512 ))
  cp -f "${out}.raw" "${out}"
  [[ "$pad" -gt 0 ]] && dd if=/dev/zero bs=1 count="$pad" status=none >> "${out}"
  rm -f "${out}.raw"
}

install_acpi_debian() {
  log "Debian/Ubuntu: installing DSDT via GRUB early initrd…"
  ensure_dsdt
  scrub_old_acpi_methods

  local src
  if src="$(find_file omen-acpi.cpio)"; then
    cp -f "$src" "${BOOT_CPIO}"
  else
    build_cpio_from_dsdt "${ACPI_SHARE}/dsdt.aml" "${BOOT_CPIO}"
  fi
  chmod 644 "${BOOT_CPIO}"

  if [[ -f /etc/default/grub ]]; then
    sed -i -E '/^GRUB_EARLY_INITRD_LINUX_CUSTOM=/d' /etc/default/grub || true
    printf '\nGRUB_EARLY_INITRD_LINUX_CUSTOM="omen-acpi.cpio"\n' >> /etc/default/grub
  fi
  mkdir -p /etc/default/grub.d
  echo 'GRUB_EARLY_INITRD_LINUX_CUSTOM="omen-acpi.cpio"' > /etc/default/grub.d/omen-acpi.cfg

  if command -v update-grub >/dev/null 2>&1; then
    update-grub
  elif command -v grub-mkconfig >/dev/null 2>&1; then
    grub-mkconfig -o /boot/grub/grub.cfg
  fi
}

# ── Fallback: one-shot prepend (Arch etc.) ───────────────────────────────────

install_acpi_prepend_fallback() {
  log "Fallback: prepend omen-acpi.cpio onto initramfs…"
  ensure_dsdt
  scrub_old_acpi_methods

  local src
  if src="$(find_file omen-acpi.cpio)"; then
    cp -f "$src" "${BOOT_CPIO}"
  else
    build_cpio_from_dsdt "${ACPI_SHARE}/dsdt.aml" "${BOOT_CPIO}"
  fi

  local csz img
  csz=$(bytes "${BOOT_CPIO}")
  shopt -s nullglob
  for img in /boot/initramfs-"${KVER}".img /boot/initrd.img-"${KVER}" \
             /boot/initramfs-*.img /boot/initrd.img-*; do
    [[ -f "$img" ]] || continue
    case "$img" in *rescue*|*Rescue*) continue ;; esac
    if dd if="$img" bs="$csz" count=1 status=none 2>/dev/null | cmp -s "${BOOT_CPIO}" -; then
      log "already OK: $(basename "$img")"
      continue
    fi
    [[ -f "${img}.omenbak" ]] || cp -a "$img" "${img}.omenbak"
    cat "${BOOT_CPIO}" "$img" > "${img}.omennew"
    mv -f "${img}.omennew" "$img"
    log "prepended → $(basename "$img")"
  done
  shopt -u nullglob

  # Best-effort hook for future kernels
  mkdir -p /etc/kernel/install.d
  cat > /etc/kernel/install.d/99-omen-acpi-prepend.install << 'EOF'
#!/usr/bin/bash
[[ "$1" == "add" ]] || exit 0
[[ "$2" == *rescue* ]] && exit 0
CPIO=/boot/omen-acpi.cpio
INITRD=/boot/initramfs-$2.img
[[ -f "$CPIO" && -f "$INITRD" ]] || exit 0
csz=$(wc -c < "$CPIO" | tr -d '[:space:]')
dd if="$INITRD" bs="$csz" count=1 status=none 2>/dev/null | cmp -s "$CPIO" - && exit 0
cat "$CPIO" "$INITRD" > "${INITRD}.new" && mv -f "${INITRD}.new" "$INITRD"
EOF
  chmod 755 /etc/kernel/install.d/99-omen-acpi-prepend.install
}

install_acpi() {
  if [[ "$IS_FEDORA" -eq 1 ]]; then
    install_acpi_fedora
  elif [[ "$IS_DEBIAN" -eq 1 ]]; then
    install_acpi_debian
  else
    install_acpi_prepend_fallback
  fi
}

# ── Intel iGPU: avoid simpledrm slideshow on hybrid Omens ────────────────────
# Recent kernels hand Alder Lake (e.g. 8086:4688) to xe, which then refuses to
# bind without force_probe — leaving the laptop panel on software simpledrm
# while nvidia-drm owns the discrete GPU with no connector. Prefer mature i915.

INTEL_GPU_MODPROBE="/etc/modprobe.d/omen-intel-igpu.conf"
INTEL_GPU_ARGS_I915="i915.force_probe"
INTEL_GPU_ARGS_XE="xe.force_probe"

find_intel_igpu() {
  # Prints: <pci_id_hex> <driver|none>   e.g. "4688 none"
  local d vendor class device driver
  for d in /sys/bus/pci/devices/*; do
    [[ -f "${d}/vendor" && -f "${d}/class" && -f "${d}/device" ]] || continue
    vendor="$(cat "${d}/vendor" 2>/dev/null || true)"
    class="$(cat "${d}/class" 2>/dev/null || true)"
    [[ "$vendor" == "0x8086" ]] || continue
    # VGA (0x030000) or Display controller (0x038000)
    case "$class" in
      0x030000|0x038000) ;;
      *) continue ;;
    esac
    device="$(cat "${d}/device")"
    device="${device#0x}"
    driver="none"
    if [[ -L "${d}/driver" ]]; then
      driver="$(basename "$(readlink -f "${d}/driver")")"
    fi
    echo "${device} ${driver}"
    return 0
  done
  return 1
}

set_kernel_cmdline_intel_gpu() {
  local id="$1"
  local add_args="${INTEL_GPU_ARGS_I915}=${id} ${INTEL_GPU_ARGS_XE}=!${id}"
  local remove_args="${INTEL_GPU_ARGS_I915}=${id} ${INTEL_GPU_ARGS_I915}=!${id} ${INTEL_GPU_ARGS_XE}=${id} ${INTEL_GPU_ARGS_XE}=!${id}"

  if command -v grubby >/dev/null 2>&1; then
    # Drop any prior force_probe for this id, then set i915-preferred pair.
    grubby --update-kernel=ALL --remove-args="${remove_args}" 2>/dev/null || true
    grubby --update-kernel=ALL --args="${add_args}" \
      || warn "grubby failed — modprobe.d still applied"
    return 0
  fi

  if [[ -f /etc/default/grub ]]; then
    # Strip old omen/i915/xe force_probe tokens, then append.
    local cur
    cur="$(grep -E '^GRUB_CMDLINE_LINUX=' /etc/default/grub | tail -n1 || true)"
    if [[ -n "$cur" ]]; then
      local val="${cur#GRUB_CMDLINE_LINUX=}"
      val="${val#\"}"
      val="${val%\"}"
      val="$(echo "$val" | sed -E \
        -e 's/(^| )i915\.force_probe=[^ ]*//g' \
        -e 's/(^| )xe\.force_probe=[^ ]*//g' \
        -e 's/  +/ /g' -e 's/^ //' -e 's/ $//')"
      val="${val} ${add_args}"
      sed -i -E "s|^GRUB_CMDLINE_LINUX=.*|GRUB_CMDLINE_LINUX=\"${val}\"|" /etc/default/grub
    else
      printf '\nGRUB_CMDLINE_LINUX="%s"\n' "${add_args}" >> /etc/default/grub
    fi
    if command -v update-grub >/dev/null 2>&1; then
      update-grub
    elif command -v grub2-mkconfig >/dev/null 2>&1; then
      grub2-mkconfig -o /boot/grub2/grub.cfg 2>/dev/null \
        || grub2-mkconfig -o /boot/efi/EFI/fedora/grub.cfg 2>/dev/null || true
    elif command -v grub-mkconfig >/dev/null 2>&1; then
      grub-mkconfig -o /boot/grub/grub.cfg 2>/dev/null || true
    fi
  else
    warn "No grubby/grub — wrote ${INTEL_GPU_MODPROBE} only"
  fi
}

regen_initramfs_for_gpu() {
  # xe/i915 often load from initrd; modprobe.d must be baked in.
  if command -v dracut >/dev/null 2>&1; then
    log "Rebuilding initramfs so Intel force_probe is available early…"
    if dracut --force --regenerate-all 2>/dev/null; then
      :
    else
      dracut --force -k "${KVER}" || dracut --force
    fi
  elif command -v update-initramfs >/dev/null 2>&1; then
    update-initramfs -u
  fi
}

install_intel_igpu() {
  local info id driver
  info="$(find_intel_igpu || true)"
  if [[ -z "$info" ]]; then
    log "No Intel iGPU found — skip GPU probe fix"
    return 0
  fi
  id="${info%% *}"
  driver="${info#* }"

  if [[ "$driver" == "i915" || "$driver" == "xe" ]]; then
    log "Intel iGPU ${id} already bound to ${driver}"
    # Still persist config so a later kernel handoff does not regress.
  else
    log "Intel iGPU ${id} unbound (driver=${driver}) — forcing i915 (fixes simpledrm slideshow)"
  fi

  mkdir -p /etc/modprobe.d
  cat > "${INTEL_GPU_MODPROBE}" << EOF
# omen-prep — bind Intel iGPU with i915 (avoid xe refuse → simpledrm)
options i915 force_probe=${id}
options xe force_probe=!${id}
EOF
  chmod 644 "${INTEL_GPU_MODPROBE}"
  log "Wrote ${INTEL_GPU_MODPROBE}"

  set_kernel_cmdline_intel_gpu "${id}"
  # Full post-install already rebuilds via Fedora ACPI/dracut — avoid twice.
  if [[ "$DO_ACPI" -eq 1 && "$IS_FEDORA" -eq 1 ]]; then
    log "Initramfs rebuild deferred to ACPI/dracut step"
  else
    regen_initramfs_for_gpu
  fi

  log "Intel iGPU fix applied. After reboot: lspci -nnk -d 8086:${id}"
  log "  expect: Kernel driver in use: i915"
}

# ── RGB keyboard ─────────────────────────────────────────────────────────────

install_keyboard_deps() {
  log "Installing build deps for running kernel ${KVER}…"
  if command -v dnf >/dev/null 2>&1; then
    # Install toolchains first — never let this pull a different kernel-core.
    dnf -y install dkms git alsa-lib-devel gcc make elfutils-libelf-devel kernel-headers \
      --exclude='kernel-core*' --exclude='kernel-modules*' --exclude='kernel-modules-core*' \
      || dnf -y install dkms git alsa-lib-devel gcc make elfutils-libelf-devel kernel-headers
    # Exact devel for *this* uname -r only (kernel-devel-uname-r can upgrade the kernel).
    if ! dnf -y install "kernel-devel-${KVER}"; then
      die "No kernel-devel for ${KVER}. Reboot into a kernel that has matching devel, then re-run with --rgb-only.
Installed kernels: $(rpm -q kernel-core 2>/dev/null | tr '\n' ' ')"
    fi
  elif command -v apt-get >/dev/null 2>&1; then
    apt-get update
    apt-get install -y dkms git build-essential "linux-headers-${KVER}" libasound2-dev || \
      apt-get install -y dkms git build-essential "linux-headers-${KVER}" libasound2t64
  elif command -v pacman >/dev/null 2>&1; then
    local hdr=linux-headers
    case "${KVER}" in *-lts*) hdr=linux-lts-headers ;; *-zen*) hdr=linux-zen-headers ;; esac
    pacman -Sy --noconfirm --needed dkms git base-devel alsa-lib "$hdr"
  else
    die "Unknown package manager"
  fi
  [[ -e /lib/modules/${KVER}/build ]] || die "headers missing for ${KVER}"
}

install_rgb_keyboard() {
  install_keyboard_deps

  if dkms status 2>/dev/null | grep -q 'omen-rgb-keyboard'; then
    log "Removing existing omen-rgb-keyboard from DKMS for clean reinstall…"
    dkms remove omen-rgb-keyboard/1.4 --all 2>/dev/null || \
      dkms remove omen-rgb-keyboard --all 2>/dev/null || true
  fi

  log "Cloning ${REPO_URL}…"
  rm -rf "${WORKDIR}/rgb"
  git clone --depth=1 "${REPO_URL}" "${WORKDIR}/rgb"
  cd "${WORKDIR}/rgb"

  lsmod | grep -q '^hp_wmi' && modprobe -r hp_wmi 2>/dev/null || true
  if [[ -f hp_wmi-blacklist.conf ]]; then
    cp -f hp_wmi-blacklist.conf /etc/modprobe.d/blacklist-hp-wmi.conf
  else
    echo 'blacklist hp_wmi' > /etc/modprobe.d/blacklist-hp-wmi.conf
  fi

  log "Upstream install.sh…"
  bash ./install.sh
  depmod -a "${KVER}" || true
  if modprobe omen_rgb_keyboard; then
    log "omen_rgb_keyboard loaded"
  else
    warn "modprobe failed — Secure Boot may require MOK enrollment"
  fi
}

print_status() {
  echo ""
  log "Status"
  echo "── ACPI ──"
  if [[ -f "${ACPI_SHARE}/dsdt.aml" ]]; then
    echo "  ${ACPI_SHARE}/dsdt.aml: $(bytes "${ACPI_SHARE}/dsdt.aml") bytes"
  else
    echo "  dsdt.aml: missing"
  fi
  if [[ "$IS_FEDORA" -eq 1 ]]; then
    [[ -f "${DRACUT_CONF}" ]] && echo "  dracut: ${DRACUT_CONF}" || echo "  dracut conf: missing"
    if grep -rqs 'acpi_override' /usr/lib/dracut/dracut.conf.d/ /etc/dracut.conf.d/ 2>/dev/null; then
      echo "  acpi_override: configured"
    fi
  fi
  if dmesg 2>/dev/null | grep -qiE 'Table Upgrade|ACPI:.*[Oo]verride'; then
    echo "  dmesg: override ACTIVE this boot"
  else
    echo "  dmesg: no override yet — reboot, then: journalctl -k | grep -i 'Table Upgrade'"
  fi
  echo "── RGB ──"
  lsmod 2>/dev/null | grep -E -q 'omen_rgb_keyboard|omen_rgb' \
    && echo "  module: loaded" || echo "  module: not loaded"
  [[ -d /sys/devices/platform/omen-rgb-keyboard ]] \
    && echo "  sysfs:  present" || echo "  sysfs:  missing"
  dkms status 2>/dev/null | grep -i omen || true
  mokutil --sb-state 2>/dev/null | grep -qi enabled \
    && echo "  Secure Boot: ON"
  echo "── GPU ──"
  local igpu
  igpu="$(find_intel_igpu || true)"
  if [[ -n "$igpu" ]]; then
    echo "  Intel iGPU: ${igpu%% *}  driver=${igpu#* }"
  else
    echo "  Intel iGPU: none detected"
  fi
  [[ -f "${INTEL_GPU_MODPROBE}" ]] && echo "  modprobe: ${INTEL_GPU_MODPROBE}"
  if grep -Eq 'i915\.force_probe=|xe\.force_probe=' /proc/cmdline 2>/dev/null; then
    echo "  cmdline: force_probe present this boot"
  else
    echo "  cmdline: no force_probe yet — reboot after GPU fix"
  fi
  if dmesg 2>/dev/null | grep -q 'simpledrm'; then
    if [[ "${igpu#* }" == "i915" || "${igpu#* }" == "xe" ]]; then
      echo "  display: native DRM bound (simpledrm was early only)"
    else
      echo "  display: stuck on simpledrm (slideshow) — need reboot with force_probe"
    fi
  fi
}

# ── main ─────────────────────────────────────────────────────────────────────

log "omen-prep post-install (kernel=${KVER}, fedora=${IS_FEDORA})"

# GPU before ACPI so the ACPI dracut rebuild also embeds modprobe.d.
if [[ "$DO_GPU" -eq 1 ]]; then
  install_intel_igpu
fi
if [[ "$DO_ACPI" -eq 1 ]]; then
  install_acpi
fi
if [[ "$DO_RGB" -eq 1 ]]; then
  install_rgb_keyboard
fi

print_status
echo ""
log "Reboot, then verify:"
echo "  journalctl -k | grep -i 'Table Upgrade'"
echo "  lspci -nnk -d 8086:4688   # expect Kernel driver in use: i915"
echo "  echo rainbow | sudo tee /sys/devices/platform/omen-rgb-keyboard/rgb_zones/animation_mode"
