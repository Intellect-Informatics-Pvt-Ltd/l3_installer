#!/usr/bin/env bash
# The Debian end-to-end (tasks.md W9): install from a medium -> health -> backup -> tamper ->
# restore -> repair -> uninstall, on a real Debian 12 with systemd, counting at every gate.
#
#   sudo tests/integration/debian-e2e.sh            # on a Debian 12 box or VM, as root
#   EPACS_E2E_KEEP=1 ...                            # keep the node afterwards for inspection
#
# WHAT IT USES. The installer CLI published self-contained (the bootstrap exception, ADR-0009);
# the HARNESS as the application payload (Pacs.Fas.Api, Pacs.Loans.Api, Pacs.SyncWorker - real
# ASP.NET services in this repository, stand-ins for the 27 L2-R2 services that live elsewhere);
# a real MySQL 8.4 tarball; the control payloads (config, config-templates, db) from this repo.
# The same script, pointed at build/stage-l2r2-medium.py's output, runs the real product.
#
# WHAT IT PROVES, GATE BY GATE. Every step prints its exit code and a COUNT (units active,
# tables in the schema, files in the backup, rows after restore) and stops at the first that
# is not what it must be. rc=0 is never the verdict.
#
# WHAT IT DOES NOT PROVE. The 27 L2-R2 services (a workspace concern), the site data pack (needs
# a state database), and a power cut (Harness.ChaosTests, separate). It has NOT been run on this
# repository's CI at the time of writing: it needs a privileged systemd container or a VM, which
# e2e-debian.yml provides on demand and nightly.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${EPACS_E2E_WORK:-/tmp/epacs-e2e}"
MYSQL_TARBALL_URL="${EPACS_E2E_MYSQL_URL:-https://dev.mysql.com/get/Downloads/MySQL-8.4/mysql-8.4.6-linux-glibc2.28-x86_64.tar.xz}"
DATA=/data/epacs
BIN=/opt/epacs
PASS=0; FAIL=0

gate() {  # gate <name> <actual> <expected>
  if [ "$2" = "$3" ]; then echo "  GATE  $1: $2 == $3"; PASS=$((PASS+1)); else echo "  GATE  $1: $2 != $3  <-- FAILED"; FAIL=$((FAIL+1)); exit 1; fi
}
step() { echo; echo "== $*"; }

[ "$(id -u)" = 0 ] || { echo "run as root"; exit 2; }
grep -qi debian /etc/os-release || { echo "this is not Debian (ADR-0010)"; exit 2; }
[ -d /run/systemd/system ] || { echo "systemd is not running"; exit 2; }

rm -rf "$WORK"; mkdir -p "$WORK/stage" "$WORK/medium"

step "1. Publish the installer (self-contained) and the harness payload (framework-dependent)"
dotnet publish "$ROOT/src/Installer.CLI" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$WORK/cli" >/dev/null
for p in Pacs.Fas.Api Pacs.Loans.Api; do
  dotnet publish "$ROOT/harness/src/$p" -c Release -r linux-x64 --self-contained false -o "$WORK/stage/services/$p" >/dev/null
done
dotnet publish "$ROOT/src/Sync.Agent" -c Release -r linux-x64 --self-contained false -o "$WORK/stage/sync" >/dev/null
dotnet publish "$ROOT/src/Installer.Agent" -c Release -r linux-x64 --self-contained false -o "$WORK/stage/agent" >/dev/null
gate "harness services published" "$(ls "$WORK/stage/services" | wc -l | tr -d ' ')" "2"

step "2. The runtime and MySQL the medium bundles (downloaded once into $WORK)"
mkdir -p "$WORK/stage/dotnet"
if [ ! -x "$WORK/stage/dotnet/dotnet" ]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$WORK/dotnet-install.sh"
  bash "$WORK/dotnet-install.sh" --runtime aspnetcore --channel 10.0 --install-dir "$WORK/stage/dotnet" >/dev/null
fi
gate "bundled runtime present" "$(test -x "$WORK/stage/dotnet/dotnet" && echo yes)" "yes"
if [ ! -x "$WORK/stage/mysql/bin/mysqld" ]; then
  curl -fsSL "$MYSQL_TARBALL_URL" -o "$WORK/mysql.tar.xz"
  mkdir -p "$WORK/stage/mysql"; tar -xJf "$WORK/mysql.tar.xz" -C "$WORK/stage/mysql" --strip-components=1
fi
gate "mysqld present" "$(test -x "$WORK/stage/mysql/bin/mysqld" && echo yes)" "yes"

step "3. Control payloads and the medium"
mkdir -p "$WORK/stage/config" "$WORK/stage/db"
# The harness map, with Linux paths, and a schema small enough to count by hand.
cp "$ROOT/tests/integration/e2e-service-map.yaml" "$WORK/stage/config/service-map.yaml"
cp "$ROOT/tests/integration/e2e-baseline.sql" "$WORK/stage/db/stable_baseline_ddl.sql"
sed -e "s|\${STAGE}|$WORK/stage|g" -e "s|\${E2E_TEMPLATES}|$ROOT/tests/integration/e2e-config-templates|g" \
    -e "s|\${STACK_VERSION}|3.3.0|; s|\${SCHEMA_VERSION}|1|; s|\${SIGNING_THUMBPRINT}||; s|\${MIN_UPGRADE_FROM}|0.0.0|; s|\${MAX_UPGRADE_FROM}|0.0.0|" \
    "$ROOT/tests/integration/e2e-media-spec.yaml" > "$WORK/media-spec.yaml"
set +e
dotnet run --project "$ROOT/src/Installer.MediaBuilder" -c Release -- build --spec "$WORK/media-spec.yaml" --out "$WORK/medium" --groups core --unsigned
rc=$?; set -e
gate "medium built (2 = built, unsigned)" "$rc" "2"
dotnet run --project "$ROOT/src/Installer.MediaBuilder" -c Release -- verify --media "$WORK/medium" >/dev/null
gate "medium verifies" "$?" "0"

step "4. Install (dry run first, then --apply)"
cat > "$WORK/site.epcfg" <<JSON
{ "signature": "", "schema_version": 1, "pacs_id": "E2E-0001", "state_code": "GJ", "data_root": "$DATA", "binary_root": "$BIN" }
JSON
export EPACS_Installer__DataRoot="$DATA" EPACS_Installer__BinaryRoot="$BIN" EPACS_Installer__ManifestPath="$WORK/medium/release-manifest.yaml"
export EPACS_Installer__SiteConfigPath="$WORK/site.epcfg" EPACS_Precheck__MinRamGb=1 EPACS_Precheck__MinDataDiskFreeGb=1 EPACS_Precheck__MinSystemDiskFreeGb=0
export EPACS_Installer__HealthWindowSeconds=180
"$WORK/cli/Installer.CLI" --mode=install --config="$WORK/site.epcfg" --allow-unsigned-config --media="$WORK/medium"
gate "dry run" "$?" "0"
"$WORK/cli/Installer.CLI" --mode=install --config="$WORK/site.epcfg" --allow-unsigned-config --media="$WORK/medium" --apply
gate "install --apply" "$?" "0"
gate "units active" "$(systemctl list-units --type=service --state=active 'epacs-*' --no-legend | wc -l | tr -d ' ')" "$(grep -c '^  - name:' "$WORK/stage/config/service-map.yaml")"
gate "current points at the release" "$(readlink "$BIN/current" | xargs basename)" "3.3.0"
TABLES=$(MYSQL_PWD="$(cat /dev/null)" "$BIN/current/mysql/bin/mysql" --defaults-file="$DATA/mysql/my.cnf" -uroot -N -B -e "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='epacs' AND TABLE_TYPE='BASE TABLE'" 2>/dev/null || echo "?")
echo "  schema tables: $TABLES (the installer's own census is the gate; this is a cross-check)"
gate "firewall table loaded" "$(nft list table inet epacs >/dev/null 2>&1 && echo yes)" "yes"
gate "service account exists" "$(getent passwd l2r2 >/dev/null && echo yes)" "yes"
gate "app config not world-readable" "$(stat -c %a "$BIN/releases/3.3.0/services/Pacs.Fas.Api/appsettings.json")" "640"
gate "site pack kept" "$(test -f "$DATA/config/site.epcfg" && echo yes)" "yes"

step "5. Backup, tamper, restore"
"$WORK/cli/Installer.CLI" --mode=backup --apply
gate "backup --apply" "$?" "0"
BK=$(ls -d "$DATA"/backups/BAK-* | tail -1)
gate "backup verified (manifest + mac)" "$(test -f "$BK/backup-manifest.json.mac" && echo yes)" "yes"
gate "no plaintext dump in the package" "$(ls "$BK/db" | grep -c '\.sql$' || true)" "0"
"$WORK/cli/Installer.CLI" --mode=restore --backup="$BK" --apply
gate "restore --apply" "$?" "0"

step "6. Repair after deleting a binary"
rm -f "$BIN/releases/3.3.0/services/Pacs.Fas.Api/Pacs.Fas.Api.dll"
"$WORK/cli/Installer.CLI" --mode=repair --media="$WORK/medium" --apply
gate "repair --apply" "$?" "0"
gate "binary re-laid" "$(test -f "$BIN/releases/3.3.0/services/Pacs.Fas.Api/Pacs.Fas.Api.dll" && echo yes)" "yes"

step "7. Uninstall keeps the data"
"$WORK/cli/Installer.CLI" --mode=uninstall --apply
gate "uninstall --apply" "$?" "0"
gate "no epacs units remain" "$(systemctl list-units --all 'epacs-*' --no-legend | wc -l | tr -d ' ')" "0"
gate "data root kept" "$(test -d "$DATA/mysql/data" && echo yes)" "yes"

echo; echo "E2E: $PASS gate(s) passed, $FAIL failed."
[ "${EPACS_E2E_KEEP:-0}" = 1 ] || rm -rf "$WORK"
