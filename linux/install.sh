#!/usr/bin/env bash
# MicMeter kurulum (Fedora)
set -e
cd "$(dirname "$0")"

need=()
python3 - <<'PY' 2>/dev/null || need+=(python3-gobject gtk3)
import gi; gi.require_version("Gtk", "3.0"); from gi.repository import Gtk
PY
python3 -c 'import cairo' 2>/dev/null || need+=(python3-cairo)
command -v pactl >/dev/null || need+=(pulseaudio-utils)   # parec + pactl (mikrofon listesi)

if [ ${#need[@]} -gt 0 ]; then
  echo "Eksik paketler kuruluyor: ${need[*]}"
  sudo dnf install -y "${need[@]}"
fi

install -Dm755 micmeter.py "$HOME/.local/bin/micmeter"
mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/micmeter.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=MicMeter
Comment=Mikrofon seviye göstergesi
Exec=$HOME/.local/bin/micmeter
Icon=audio-input-microphone
Categories=AudioVideo;Utility;
DESK

pkill -f "$HOME/.local/bin/micmeter" 2>/dev/null || true
nohup "$HOME/.local/bin/micmeter" >/dev/null 2>&1 &
echo "Tamam - MicMeter kuruldu ve başlatıldı. Uygulamalar menüsünde 'MicMeter' olarak da var."
