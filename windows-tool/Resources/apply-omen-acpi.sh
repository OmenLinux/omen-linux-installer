#!/usr/bin/env bash
# Live session after install ("Continue testing"):
#   sudo mount /dev/<root> /mnt
#   sudo mount /dev/<boot> /mnt/boot   # if separate (Fedora often)
#   sudo bash /cdrom/apply-omen-acpi.sh /mnt
#
# Fedora: install DSDT + dracut acpi_override into the target (U9311 method).
# Others: one-shot prepend + GRUB early where applicable.
# After first boot run post-install-omen.sh for RGB + permanence check.
set -euo pipefail

die() { echo "error: $*" >&2; exit 1; }
log() { echo "==> $*"; }
bytes() { wc -c < "$1" | tr -d '[:space:]'; }

[[ "${EUID}" -eq 0 ]] || die "run as root: sudo bash $0 /mnt"

ROOT="${1:-}"
if [[ -z "$ROOT" ]]; then
  for c in /target /mnt /installed; do
    [[ -f "$c/etc/os-release" ]] && ROOT=$c && break
  done
fi
[[ -n "$ROOT" && -f "$ROOT/etc/os-release" ]] || die "usage: $0 /mnt"
ROOT="$(readlink -f "$ROOT")"
[[ -d "$ROOT/boot" ]] || die "$ROOT/boot missing — mount boot at $ROOT/boot"

IS_FEDORA=0
grep -qiE '^ID=fedora' "$ROOT/etc/os-release" 2>/dev/null && IS_FEDORA=1
[[ -f "$ROOT/etc/fedora-release" ]] && IS_FEDORA=1

SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "$0" 2>/dev/null || echo "$0")")" && pwd)"
CPIO=""
AML=""
for c in "$SCRIPT_DIR/omen-acpi.cpio" /cdrom/omen-acpi.cpio \
         /lib/live/mount/medium/omen-acpi.cpio /run/live/medium/omen-acpi.cpio; do
  [[ -f "$c" ]] && CPIO=$c && break
done
for c in "$SCRIPT_DIR/dsdt.aml" /cdrom/dsdt.aml \
         /lib/live/mount/medium/dsdt.aml /run/live/medium/dsdt.aml; do
  [[ -f "$c" ]] && AML=$c && break
done
[[ -n "$CPIO" || -n "$AML" ]] || die "omen-acpi.cpio / dsdt.aml not found on USB"

log "Target $ROOT (fedora=${IS_FEDORA})"

# Scrub old experiments in the installed system.
for f in "$ROOT/etc/default/grub" "$ROOT/etc/default/grub.d"/*; do
  [[ -f "$f" ]] || continue
  sed -i -E 's/[[:space:]]*init=\/omen-prep\/[^[:space:]]*//g' "$f" || true
  sed -i -E '/^GRUB_EARLY_INITRD_LINUX_CUSTOM=.*omen-acpi/d' "$f" || true
done
rm -f "$ROOT/boot/omen-acpi.img" "$ROOT/boot/acpi_override" \
      "$ROOT/etc/default/grub.d/omen-acpi.cfg" \
      "$ROOT/etc/kernel/postinst.d/zz-omen-acpi" \
      "$ROOT/etc/kernel/postinst.d/omen-acpi" \
      "$ROOT/etc/initramfs/post-update.d/omen-acpi" \
      "$ROOT/etc/kernel/install.d/99-omen-acpi.install" \
      "$ROOT/etc/kernel/install.d/99-omen-acpi-prepend.install" \
      "$ROOT/etc/kernel/install.d/91-omen-early-initrd.install" \
      "$ROOT/etc/dracut.conf.d/99-omen-acpi.conf" \
      "$ROOT/etc/omen-prep/prepend-initrd.sh" 2>/dev/null || true
chroot "$ROOT" grub2-editenv - unset early_initrd 2>/dev/null || true

mkdir -p "$ROOT/etc/omen-prep" "$ROOT/usr/share/omen-prep/acpi" "$ROOT/boot"

# Resolve DSDT bytes into the target.
if [[ -n "$AML" ]]; then
  cp -f "$AML" "$ROOT/usr/share/omen-prep/acpi/dsdt.aml"
elif [[ -n "$CPIO" ]]; then
  stage=$(mktemp -d)
  (cd "$stage" && cpio -id --quiet < "$CPIO")
  aml=$(find "$stage" -iname 'dsdt.aml' | head -n1)
  [[ -n "$aml" ]] || die "no dsdt.aml inside $CPIO"
  cp -f "$aml" "$ROOT/usr/share/omen-prep/acpi/dsdt.aml"
  rm -rf "$stage"
fi
chmod 644 "$ROOT/usr/share/omen-prep/acpi/dsdt.aml"
[[ -n "$CPIO" ]] && cp -f "$CPIO" "$ROOT/etc/omen-prep/omen-acpi.cpio"
cp -f "$ROOT/usr/share/omen-prep/acpi/dsdt.aml" "$ROOT/etc/omen-prep/dsdt.aml"
log "DSDT → $ROOT/usr/share/omen-prep/acpi/dsdt.aml ($(bytes "$ROOT/usr/share/omen-prep/acpi/dsdt.aml") bytes)"

if [[ "$IS_FEDORA" -eq 1 ]]; then
  mkdir -p "$ROOT/usr/lib/dracut/dracut.conf.d"
  cat > "$ROOT/usr/lib/dracut/dracut.conf.d/99-omen-acpi.conf" << 'EOF'
# omen-prep — same mechanism as u9311-acpi-patch-fedora
acpi_override="yes"
acpi_table_dir="/usr/share/omen-prep/acpi"
EOF
  log "Wrote dracut acpi_override config"

  if [[ -x "$ROOT/usr/bin/dracut" ]] || [[ -x "$ROOT/usr/sbin/dracut" ]]; then
    log "Running dracut --force in target (best-effort)…"
    chroot "$ROOT" dracut --force --regenerate-all 2>/dev/null \
      || chroot "$ROOT" dracut --force 2>/dev/null \
      || log "WARN: dracut in chroot failed — after first boot run: sudo dracut --force --regenerate-all"
  fi

  log "Done (Fedora). Reboot, then: sudo bash /run/media/\$USER/*/post-install-omen.sh"
  exit 0
fi

# Non-Fedora: keep cpio + one-shot prepend for current kernels.
[[ -n "$CPIO" ]] || die "omen-acpi.cpio required for non-Fedora apply"
cp -f "$CPIO" "$ROOT/boot/omen-acpi.cpio"
chmod 644 "$ROOT/boot/omen-acpi.cpio"

if [[ -f "$ROOT/etc/default/grub" ]]; then
  sed -i -E '/^GRUB_EARLY_INITRD_LINUX_CUSTOM=/d' "$ROOT/etc/default/grub" || true
  printf '\nGRUB_EARLY_INITRD_LINUX_CUSTOM="omen-acpi.cpio"\n' >> "$ROOT/etc/default/grub"
fi
mkdir -p "$ROOT/etc/default/grub.d"
echo 'GRUB_EARLY_INITRD_LINUX_CUSTOM="omen-acpi.cpio"' > "$ROOT/etc/default/grub.d/omen-acpi.cfg"
chroot "$ROOT" update-grub 2>/dev/null || chroot "$ROOT" grub-mkconfig -o /boot/grub/grub.cfg 2>/dev/null || true

CSZ=$(bytes "$CPIO")
OK=0
shopt -s nullglob
for img in "$ROOT/boot"/initramfs-*.img "$ROOT/boot"/initrd.img-*; do
  [[ -f "$img" ]] || continue
  case "$img" in *rescue*|*Rescue*) continue ;; esac
  if dd if="$img" bs="$CSZ" count=1 status=none 2>/dev/null | cmp -s "$CPIO" -; then
    log "already OK: $(basename "$img")"
    OK=1
    continue
  fi
  cat "$CPIO" "$img" > "${img}.omennew"
  mv -f "${img}.omennew" "$img"
  log "prepended → $(basename "$img")"
  OK=1
done
shopt -u nullglob

[[ "$OK" -eq 1 ]] || die "no initramfs under $ROOT/boot"
log "Done. Reboot, then run post-install-omen.sh for RGB."
