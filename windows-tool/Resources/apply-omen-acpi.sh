#!/usr/bin/env bash
# After Ubuntu/etc install, still in the live session ("Continue testing"):
#   sudo mount /dev/nvme0n1p2 /mnt
#   sudo bash /cdrom/apply-omen-acpi.sh /mnt
#
# Does ONE thing: prepend omen-acpi.cpio onto every installed initrd
# (same bytes / method as the live USB).
set -euo pipefail

die() { echo "error: $*" >&2; exit 1; }
log() { echo "==> $*"; }

[[ "${EUID}" -eq 0 ]] || die "run as root: sudo bash $0 /mnt"

ROOT="${1:-}"
if [[ -z "$ROOT" ]]; then
  for c in /target /mnt /installed; do
    [[ -f "$c/etc/os-release" ]] && ROOT=$c && break
  done
fi
[[ -n "$ROOT" && -f "$ROOT/etc/os-release" ]] || die "usage: $0 /mnt  (mount installed root first)"
ROOT="$(readlink -f "$ROOT")"

SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "$0" 2>/dev/null || echo "$0")")" && pwd)"
CPIO=""
for c in "$SCRIPT_DIR/omen-acpi.cpio" /cdrom/omen-acpi.cpio \
         /lib/live/mount/medium/omen-acpi.cpio /run/live/medium/omen-acpi.cpio \
         /omen-prep/omen-acpi.cpio; do
  [[ -f "$c" ]] && CPIO=$c && break
done
[[ -n "$CPIO" ]] || die "omen-acpi.cpio not found on the omen-prep USB"

log "Target $ROOT"
log "CPIO   $CPIO ($(wc -c < "$CPIO") bytes)"

# Scrub panic leftovers from older omen-prep experiments.
for f in "$ROOT/etc/default/grub" "$ROOT/etc/default/grub.d"/*; do
  [[ -f "$f" ]] || continue
  sed -i -E 's/[[:space:]]*init=\/omen-prep\/[^[:space:]]*//g' "$f" || true
  sed -i -E '/^GRUB_EARLY_INITRD_LINUX_CUSTOM=/d' "$f" || true
done
rm -f "$ROOT/boot/omen-acpi.img" "$ROOT/boot/acpi_override" \
      "$ROOT/etc/default/grub.d/"*omen-acpi* 2>/dev/null || true

mkdir -p "$ROOT/etc/omen-prep" "$ROOT/etc/kernel/postinst.d"
cp -f "$CPIO" "$ROOT/etc/omen-prep/omen-acpi.cpio"

# Future kernels: prepend again after initramfs is rebuilt.
cat > "$ROOT/etc/kernel/postinst.d/zz-omen-acpi" << 'EOF'
#!/bin/sh
set -e
CPIO=/etc/omen-prep/omen-acpi.cpio
[ -f "$CPIO" ] || exit 0
for initrd in "/boot/initrd.img-$1" "/boot/initramfs-$1.img"; do
  [ -f "$initrd" ] || continue
  grep -a -q 'kernel/firmware/acpi/dsdt.aml' "$initrd" 2>/dev/null && continue
  tmp=$(mktemp)
  cat "$CPIO" "$initrd" > "$tmp"
  chmod --reference="$initrd" "$tmp" 2>/dev/null || chmod 644 "$tmp"
  mv -f "$tmp" "$initrd"
done
exit 0
EOF
chmod 755 "$ROOT/etc/kernel/postinst.d/zz-omen-acpi"

OK=0
shopt -s nullglob
for img in "$ROOT/boot"/initrd.img-* "$ROOT/boot"/initramfs-*.img "$ROOT/boot"/initramfs-linux*.img; do
  [[ -f "$img" ]] || continue
  if grep -a -q 'kernel/firmware/acpi/dsdt.aml' "$img" 2>/dev/null; then
    log "already OK: $(basename "$img")"
    OK=1
    continue
  fi
  tmp=$(mktemp "${img}.omen.XXXXXX")
  cat "$CPIO" "$img" > "$tmp"
  chmod --reference="$img" "$tmp" 2>/dev/null || chmod 644 "$tmp"
  mv -f "$tmp" "$img"
  log "prepended → $(basename "$img")"
  OK=1
done
shopt -u nullglob

[[ "$OK" -eq 1 ]] || die "no initrd under $ROOT/boot — is /boot mounted?"
log "Done. Reboot."
