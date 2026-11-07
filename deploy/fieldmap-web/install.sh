#!/bin/sh
# Installs (or updates) the Field Map web server on Ubuntu 22.04/24.04. Run from the unpacked folder:
#   sudo ./install.sh
set -eu
HERE=$(cd "$(dirname "$0")/.." && pwd)
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
[ -x "$HERE/server/FieldMap.Web" ] || chmod +x "$HERE/server/FieldMap.Web" "$HERE/mapbuilder/MapBuilder"

id fieldmap >/dev/null 2>&1 || useradd --system --home /var/lib/fieldmap --shell /usr/sbin/nologin fieldmap
install -d -o fieldmap -g fieldmap /var/lib/fieldmap /var/lib/fieldmap/events
install -d /etc/fieldmap-web /opt/fieldmap-web

# Program files are replaced on every install; settings and data are kept.
rm -rf /opt/fieldmap-web/server /opt/fieldmap-web/mapbuilder
cp -r "$HERE/server" "$HERE/mapbuilder" /opt/fieldmap-web/
chmod +x /opt/fieldmap-web/server/FieldMap.Web /opt/fieldmap-web/mapbuilder/MapBuilder
[ -f /etc/fieldmap-web/appsettings.json ] || cp "$HERE/deploy/appsettings.json" /etc/fieldmap-web/appsettings.json
[ -f /etc/fieldmap-web/update.env ] || echo 'REGIONS="bc"' > /etc/fieldmap-web/update.env
chmod 640 /etc/fieldmap-web/appsettings.json
chgrp fieldmap /etc/fieldmap-web/appsettings.json

cp "$HERE/deploy/fieldmap-web.service" "$HERE/deploy/fieldmap-update.service" "$HERE/deploy/fieldmap-update.timer" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now fieldmap-web.service fieldmap-update.timer
systemctl restart fieldmap-web.service

echo
echo "Field Map web server is running on http://127.0.0.1:5080 (local only)."
if ! ls /var/lib/fieldmap/regions/*/current.json >/dev/null 2>&1; then
  echo "No map data yet. Build it now (the first BC build downloads and processes a lot of data, so it takes a while):"
  echo "  sudo -u fieldmap /opt/fieldmap-web/mapbuilder/MapBuilder update --region bc --data /var/lib/fieldmap"
  echo "  (or try the small sample first: sudo -u fieldmap /opt/fieldmap-web/mapbuilder/MapBuilder build-sample --data /var/lib/fieldmap)"
fi
echo "Next: put HTTPS in front of it (phones need HTTPS for GPS) - see the Caddyfile next to this script."
echo "Settings: /etc/fieldmap-web/appsettings.json (access code, regions). Weekly data update: /etc/fieldmap-web/update.env"
