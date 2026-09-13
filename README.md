# ePACS Offline Installer — `l3_installer`

> The installer for an **offline PACS node**: a signed, self-contained medium that installs the
> whole L2-R2 stack (28 services, MySQL, cache) onto a Debian machine with no network, no DBA
> and no second chance; keeps it running; backs it up; upgrades it from the next medium; and
> moves the society's data to and from the state instance only as signed, counted **packs**.
>
> Written for three readers. **DevOps** builds the medium (§4) and the site packs (§5).
> **The operator** installs and runs the node (§6, §7). **QA** proves it (§9). Everything in
> this file is checked against the code at the time of writing; where a number is quoted it was
> measured, and where something is not built it says so.

> ### Status — read this first (2026-09-13)
>
> | | |
> |---|---|
> | **Runs end to end** | verify → precheck → topology → platform → install → database → health, checkpointed at every phase, resumable after a power cut. **Dry run is the default**; nothing changes without `--apply` |
> | **Built and unit-tested** | media build + verify with a secret gate · 28-service topology generated from the estate · sibling-URL rewrite into each service's own file · Linux ACL / nftables / service accounts · health aggregator (healthy / listening / failed) · AES-256-GCM backups wrapped to the node and to the state · restore · repair · upgrade on a power-cut-safe junction flip · signed hash-chained packs out and in · `.epcfg` signature verification · control payloads re-hashed at use |
> | **Tests** | **445** in `Installer.UnitTests` (432 pass, 13 skip only where a dependency is absent — a live MySQL, a Linux host; on the Linux CI leg a skip is a failure) + 15 harness contract tests. The evidence pack is rendered from every CI run: [`docs/testing/installer-evidence.md`](docs/testing/installer-evidence.md) |
> | **Not yet proven** | the Debian end-to-end ([`tests/integration/debian-e2e.sh`](tests/integration/debian-e2e.sh), nightly + on demand in [`e2e-debian.yml`](.github/workflows/e2e-debian.yml)) is scripted with 22 counted gates and **has not yet run** |
> | **Not built** | Windows ACL/firewall/account engines (exit **4** by name) · the migration step of an upgrade (17.5) · the state-side pack ingest · `/health/live` inside the 28 services (`MapErpHealth()`, owed by the estate) |
> | **Target** | .NET 10 (SDK `10.0.302`, pinned in `global.json` to match the L2-R2 workspace). **Debian 12 + systemd** is the node OS (ADR-0009/0010); Windows builds and is close to portable but refuses by name where an engine is missing |
> | **Truth for status** | [`.kiro/specs/epacs-offline-installer/tasks.md`](.kiro/specs/epacs-offline-installer/tasks.md) — the per-item audit. CI fails if it claims evidence that does not exist (`build/check-tasks-evidence.py`) |
> | **Architecture record** | the L2-R2 workspace: `docs/offline-installer-assessment-and-plan.md` §12 (the review), `docs/product/offline-pacs-node-operating-model.md` (what NABARD is asked to accept), `README.md` §5h |
>
> **This README is hand-written.** The workspace's `build/generate-module-readmes.py` excludes
> this repo (`NOT_MODULES`): its generated prose about state branches and `r2-dev-stable` is not
> true of a separate product on `master`/`baseline/**`.
>
> `harness/` is a **deliberate stand-in payload**, not the product (§11). `Pacs.Fas.Api` is a
> simulation of `l3_FAS` and must never be installed on a node that runs the real thing.

---

## Contents

| | |
|---|---|
| [1. The model in one paragraph](#1-the-model-in-one-paragraph) | what an offline node is, and the four rulings |
| [2. Repository map](#2-repository-map) | where each thing lives |
| [3. Build the code](#3-build-the-code) | prerequisites, build, test — any machine |
| [4. DevOps: build a release medium, in order](#4-devops-build-a-release-medium-in-order) | topology → secrets → stage → build → verify |
| [5. DevOps: the two site packs](#5-devops-the-two-site-packs) | `.epcfg` (identity) and `.epdata` (the society) |
| [6. Operator: install a node](#6-operator-install-a-node) | prerequisites, dry run, apply, what to check |
| [7. Operator: day two](#7-operator-day-two) | backup, restore, repair, upgrade, uninstall, packs, support bundle |
| [8. What the installer refuses](#8-what-the-installer-refuses) | the refusal table, with exit codes |
| [9. QA: prove it, in order](#9-qa-prove-it-in-order) | unit/contract, the evidence pack, the Debian e2e, the manual matrix |
| [10. Configuration reference](#10-configuration-reference) | every option the CLI and agents read |
| [11. The harness](#11-the-harness) | the stand-in payload |
| [12. CI](#12-ci) | the three workflows and what each proves |
| [13. Troubleshooting](#13-troubleshooting) | symptom → cause → fix |
| [14. Documents and ADRs](#14-documents-and-adrs) | the record |
| [15. Owed](#15-owed) | what is designed and not built |

---

## 1. The model in one paragraph

An offline PACS node is a **signed, self-contained replica-of-record for one society**. The
state instance stays the system of record for everything above the society. The node runs the
same 28 services and the same baseline schema as every state (`db/stable_baseline_ddl.sql`,
1,256 tables); it is born from a **carve-out** of that society's rows out of the state database;
and data crosses the boundary only as **packs** — hash-chained, CMS-signed, counted, applied
exactly once by sequence. Nothing streams. Approvals that belong above the PACS are **deferred,
never bypassed**: they wait in a pack for the state, and the SLA clock starts when the pack is
received there.

The four rulings, each an ADR: packs not a stream ([ADR-0011](docs/adr/ADR-0011-sync-is-a-pack-not-a-stream.md));
approvals deferred ([ADR-0012](docs/adr/ADR-0012-approvals-above-the-pacs-are-deferred.md));
the migration runner applies the estate's generated migration ([ADR-0013](docs/adr/ADR-0013-migration-runner-applies-the-estates-migration.md));
Debian + systemd, framework-dependent payloads on a bundled runtime ([ADR-0009](docs/adr/ADR-0009-framework-dependent-payload.md), [ADR-0010](docs/adr/ADR-0010-debian-systemd-linux-target.md)).

```
 state instance (system of record)                         offline PACS node (Debian 12, no DBA)
 ┌───────────────────────────────────────────┐             ┌──────────────────────────────────────────┐
 │ L2-R2 (28 svc) + state DB (every PACS)    │   medium    │ /opt/epacs/current -> releases/<ver>      │
 │ stage-l2r2-medium.py -> epacs-media build ───────────►  │ /data/epacs/{mysql,config,packs,backups} │
 │ l2r2 db carve-out --pacs X -> X.epdata    ───────────►  │ epacs-install: verify -> precheck ->      │
 │ site.epcfg (+ .sig)                       ───────────►  │   platform -> install -> db -> health     │
 │                                           │             │   counted at every gate                   │
 │ pack ingest (OWED)             ◄───────────────────────  │ ledger pack: rows since the watermark,    │
 │ policy pack (masters, decisions) ─────────────────────► │   chained, signed; policy pack applied    │
 └───────────────────────────────────────────┘  USB/link   └──────────────────────────────────────────┘
```

---

## 2. Repository map

| Path | What it is |
|---|---|
| `src/Installer.CLI` | **The entry point** (`Installer.CLI`, published self-contained as the one bootstrap exception). Parses the command line, builds the composition root, runs the pipeline, maps outcomes to exit codes |
| `src/Installer.Core` | Composition root (`AddInstaller()`), the **pipeline** (verify → precheck → topology → platform → install → database → config → register → health → checkpoint), the state machine with fsync'd checkpoints, `VerifiedMedia` (control files re-hashed at use), the `.epcfg` loader with detached-signature verification, `Packs/` (exporter, applier, MySQL access), schema fingerprinting |
| `src/Installer.Actions` | Prechecks, payload extraction, `PayloadConfigRewriter` (sibling URLs and the DB password into each service's own `appsettings.json`), `SystemdUnitWriter`, `Platform/Linux/` (ACL, nftables, service accounts), `Database/` (MySQL bootstrap: initialise, accounts, baseline, site data, census), `Health/HealthAggregator`, upgrade/repair/uninstall engines |
| `src/Installer.MediaBuilder` | **`epacs-media`**: `build` (measure, hash, sign, **refuse secrets**) and `verify` |
| `src/ManifestVerifier` | Manifest CMS signature + SHA-256 payload verification |
| `src/BackupRestore` | Backup (dump, encrypt per file, wrap the key to the node and to the state, MAC the manifest, verify by reading back) and restore (verify, decrypt, safety backup, count) |
| `src/SupportBundle` | Diagnostics collector with redaction, encrypted to the state's public key |
| `src/Installer.Agent` | Always-on: health polling, disk, config drift (baseline persisted), certificate expiry, clock drift, backup scheduling; answers `/health/live` |
| `src/Sync.Agent` | `PackSyncWorker`: ledger-pack export on a schedule, policy-pack apply; answers `/health/live`. The Kafka/NLDR stream worker is in `Frozen/` and unregistered — `Packs:Mode=stream` exits 4 |
| `src/SharedKernel` | Options, contracts, `Crypto/BackupCrypto`, `Packs/` (manifest, envelope, ledger, classification), `Security/` (CMS signer, secret store with a random master key), `Hosting/` (health endpoint, run-log file provider) |
| `topology/` | **Generated by the workspace** (`build/generate-topology.py`): `service-map.l2r2.{linux,windows}.yaml`, `appsettings.Applications.json`, `sibling-urls.json`. Hand-owned: `infrastructure.{linux,windows}.yaml`, `media-spec.l2r2.yaml` |
| `packaging/config-templates/` | The templates the installer renders per service; `appsettings.Site.template.json` carries the node's overlay (including `CBSIntegration:Mode=Queued`) |
| `packaging/error-catalog/` | The typed error catalogue (`ERP-INST-…`) |
| `tests/Installer.UnitTests` | 445 tests — unit, contract (generated artefacts against the estate's own files) and `[SkippableFact]` live tests |
| `tests/integration/` | `debian-e2e.sh` and its fixtures (harness service map, a small baseline, config templates, media spec) |
| `build/` | `installer-test-evidence.py` (TRX → evidence markdown), `check-tasks-evidence.py` (tasks.md cannot claim what CI did not see) |
| `samples/` | Example manifest, service map, `.epcfg`, media spec — **examples, not inputs**; the real ones are in `topology/` |
| `harness/` | The stand-in payload and the frozen NLDR protocol rig (§11) |
| `docs/adr/` | ADR-0001 … ADR-0013 (§14) |
| `docs/testing/installer-evidence.md` | The evidence pack from the last run |
| `.kiro/specs/epacs-offline-installer/tasks.md` | The per-item status audit — the only place to trust for "is X built" |

---

## 3. Build the code

Any machine; no database needed for the default test run.

**Prerequisites**

| | |
|---|---|
| .NET SDK | exactly the version in `global.json` (`10.0.302`). The analyser set is tied to it; a different SDK turns into build errors, not warnings |
| MySQL client + `mysqldump` | only for the live tests (`EPACS_TEST_MYSQL_BIN`); `apt-get install mysql-client` / `brew install mysql-client` |
| Docker | only for the Debian end-to-end (§9.3) and the harness |
| `openssl`, `nft` | only on a node; `nft -c -f` is used by the e2e to parse the generated ruleset |

```bash
dotnet --version                     # must print 10.0.302
dotnet restore ePACS.Installer.sln
dotnet build   ePACS.Installer.sln -c Release --no-restore     # 0 warnings: the analysers are errors
dotnet test    ePACS.Installer.sln -c Release --no-build \
    --logger "trx;LogFileName=installer.trx" --results-directory artifacts/test-results
```

Expected on a machine without MySQL: `Passed! - Failed: 0, Passed: 432, Skipped: 13, Total: 445`.
The 13 are named in the evidence pack with the reason each skipped. With a MySQL reachable:

```bash
export EPACS_TEST_MYSQL='Server=127.0.0.1;Port=3306;Database=epacs_test;Uid=root;Pwd=…'
export EPACS_TEST_MYSQL_BIN=/usr/bin        # a directory holding mysql AND mysqldump
dotnet test ePACS.Installer.sln -c Release --no-build
```

→ the 10 schema-fingerprint tests and the pack round trip run for real (on Linux, 0 skips; on
macOS the 2 `/etc/os-release` tests still skip). The database named must be one you can create
and drop tables in; the tests create `pk_live_*` tables and drop them.

Render the evidence pack from any TRX:

```bash
python3 build/installer-test-evidence.py artifacts/test-results/installer.trx > docs/testing/installer-evidence.md
```

Then the harness, if you need it: `cd harness && dotnet build ePACS.SyncHarness.sln && dotnet test tests/Harness.ContractTests` (15 tests).

---

## 4. DevOps: build a release medium, in order

A medium is built **from the L2-R2 workspace**, never from this repository alone: the service
list, the ports, the schema, the table classification and the redaction rules all come from
there, and this repository carries no copy of any of them. Every step is dry-run by default and
prints exactly what it would do; `--apply` performs it.

### 4.0 What you need before you start

| | Where it comes from | Why |
|---|---|---|
| The workspace checkout, every module on `r2-dev-stable` | `git clone` of `l2r2-platform-build` with its 36 sibling repos; `ops/l2r2 doctor` green | the 28 services are published from those checkouts |
| The **ASP.NET Core 10 runtime**, unpacked (`dotnet/`) | `dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir <dir>` | the medium bundles it; every service is framework-dependent on it (ADR-0009) |
| **MySQL 8.4 LTS** generic Linux tarball, unpacked | `mysql-8.4.x-linux-glibc2.28-x86_64.tar.xz` from dev.mysql.com | not in any repo; ~500 MB |
| **Garnet** (cache), unpacked | Microsoft Garnet release for linux-x64 | `Components:Cache:Enabled` defaults **on** — it holds the idempotency keys |
| The **release signing certificate** (`release.pfx`) and its password in `EPACS_PFX_PASSWORD` | the signing ceremony (G3) — who holds it is a NABARD/state decision, recorded as open | an unsigned medium exits 2 and is **not releasable**; the node pins the thumbprint |
| The **state recovery public key** (PEM) | the state's key custodian | every backup's key is wrapped to it, so the state can open a node's backup if the node is lost |
| A version number (`3.4.0`) and the schema version (from `db/schema_version_registry`) | the release plan | written into the manifest; the upgrade window is checked against it |

Kafka + JRE are needed only if `Components:Eventing:Enabled=true`; nothing in the pack path
uses Kafka and it defaults **off** (ADR-0003, ADR-0011).

### 4.1 The topology is current

```bash
# in l2r2-platform-build
python3 build/generate-topology.py --check      # exit 1 if l3_installer/topology/* is stale
python3 build/generate-topology.py --apply      # regenerate; commit the result in l3_installer
```

The generator reads what the compose generator reads — each module's own `appsettings.json`
for the port it binds, the project on disk, `ops/ansible/group_vars/all.yml` for the start
order. **A service that pins no `Urls` is not discovered and does not reach the medium** — this
is how `l3_lob` (2026-09-07) and `l3_SHG` (2026-09-13) were found missing. `ops/l2r2 ci guards`
runs this check; CI fails on a stale topology.

### 4.2 No secret can reach the medium

```bash
python3 build/config-hygiene.py scan --branch r2-dev-stable       # exit 1 names every file and key
```

The base `appsettings.json` files on `r2-dev-stable` still carry TD-123 credentials (Keycloak,
Gmail, Aadhaar, WebLand). The staging script redacts them; the media builder's **SecretGate**
then refuses any payload where one survives (exit 3, naming file and key, value masked). Its
rules are byte-equal to `config-hygiene.py`'s, asserted by a test, so the two cannot drift.
Redaction is not rotation — rotation is the owner's (TD-123).

### 4.3 Stage and build

```bash
# in l2r2-platform-build
python3 build/stage-l2r2-medium.py --version 3.4.0 --out artifacts/l2r2-stage \
    --runtime /path/to/dotnet --mysql /path/to/mysql-8.4 --garnet /path/to/garnet \
    --pfx /secure/release.pfx                        # dry run: prints the plan, writes nothing
python3 build/stage-l2r2-medium.py … --apply         # does it
```

In order, it: publishes the 28 services framework-dependent linux-x64 into `<out>/services/<repo>/`;
**redacts** the published `appsettings*.json` (`config-hygiene.py redact-dir`); publishes
`Sync.Agent` and `Installer.Agent`; assembles the control payloads — `<out>/config`
(`service-map.yaml` = the Linux map, `sibling-urls.json`, `appsettings.Applications.json`,
`pacs-table-classification.json`) and `<out>/db/stable_baseline_ddl.sql`; copies the runtime and
infrastructure you pointed it at; renders `topology/media-spec.l2r2.yaml`; and runs:

```bash
epacs-media build --spec <rendered spec> --out artifacts/medium/l2r2-3.4.0 --groups core,cache --pfx release.pfx
```

`epacs-media` exit codes: **0** built and signed · **1** build error · **2** built **unsigned**
(development only; a node refuses it) · **3** refused — a payload carries a secret · **99**
unexpected. A development medium: `--unsigned` instead of `--pfx`. A deliberate allow for a
key the gate flags wrongly: `--allow <key>` (recorded in the build output; use sparingly).

### 4.4 Verify what you built, with the installer's own verifier

```bash
epacs-media verify --media artifacts/medium/l2r2-3.4.0 --thumbprint <release cert SHA-1>
```

Verdict first: a manifest that does not verify cannot vouch for any hash it declares, however
well those hashes match. `ok` per payload and `OK n payload(s)` is the only pass.

### 4.5 Publish the installer itself

The CLI is the one **self-contained** publish (it must run before the bundled runtime exists):

```bash
dotnet publish src/Installer.CLI -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/cli
```

Put `artifacts/cli/Installer.CLI` beside the medium. Its own `appsettings.json` defaults are
Windows paths; on a Debian node set the Linux ones in the environment (§6.2) or ship an
`appsettings.Production.json` beside the binary with `Installer:DataRoot=/data/epacs`,
`Installer:BinaryRoot=/opt/epacs`, `Installer:ExpectedSigningThumbprint=<release cert>`.

### 4.6 What goes on the stick

```
/media/
  release-manifest.yaml, release-manifest.yaml.sig     ← signed by release.pfx
  payloads/…                                           ← config, config-templates, db, dotnet, mysql, garnet, services, sync, agent
  Installer.CLI (+ appsettings.Production.json)        ← self-contained
  site.epcfg, site.epcfg.sig                           ← §5.1, one per node
  <pacs>.epdata/                                       ← §5.2, one per node
```

---

## 5. DevOps: the two site packs

A medium is the same for every node in a state. What makes a node *this* society is two packs,
both signed, both verified on the node before anything is touched.

### 5.1 `site.epcfg` — the node's identity

JSON, schema version 1. The fields the pipeline reads:

```json
{
  "signature": "",
  "schema_version": 1,
  "pacs_id": "GJ-0001",          ← must match the .epdata; written into every pack the node emits
  "state_code": "GJ",
  "district_code": "…",
  "data_root": "/data/epacs",
  "binary_root": "/opt/epacs",
  "backup_targets": ["/data/epacs/backups", "/media/backup-usb"],
  "backup_schedule": { "daily": "02:00", "weekly_day": "Sunday", "weekly_time": "03:00" },
  "log_retention_days": { "application": 30, "audit": 90, "mysql": 7 },
  "monitoring": { "health_poll_interval_seconds": 60, "disk_check_interval_seconds": 900, "drift_check_interval_seconds": 3600 }
}
```

`samples/site-config-pack.epcfg` is the full shape. It is **data the pipeline receives, not a
configuration source**: it can never redefine an installer path or a threshold (those are §10).

Sign it with a detached CMS signature beside it (the node verifies `site.epcfg.sig` against the
pinned thumbprint; 7.9):

```bash
openssl cms -sign -binary -in site.epcfg -signer state-release.pem -inkey state-release.key \
    -outform DER -out site.epcfg.sig
```

Unsigned `.epcfg` is exit 64 unless `--allow-unsigned-config` — development only, logged at
Warning. The installed copy is kept at `<DataRoot>/config/site.epcfg` (+ `.sig`) and is what
repair, upgrade and the agents read afterwards.

### 5.2 `<pacs>.epdata` — the society

Cut on the state instance from the state database, **counted first**, signed:

```bash
# in l2r2-platform-build, with MYSQL_HOST/MYSQL_USER/MYSQL_PWD/MYSQL_DATABASE in the environment
ops/l2r2 db carve-out --state GJ --pacs GJ-0001 --out GJ-0001.epdata \
    --signer state-release.pem --key state-release.key            # dry run: counts, per table
ops/l2r2 db carve-out … --apply
```

What it does: reads `db/pacs-table-classification.json` (1,256 tables: **839** cut by the
society column, **397** masters that travel whole, **20** excluded by rule — a guard fails the
build if a baseline table is unclassified); `SELECT COUNT(*)` per society table; per-table
`mysqldump --no-create-info --replace --where=<society>`; writes `manifest.json` with the counts
and `SchemaFingerprintKind: baseline-file-sha256` (the SHA-256 of `db/stable_baseline_ddl.sql`,
so a pack cut against one baseline cannot be loaded onto another); signs the manifest with
`openssl cms`. The password reaches `mysqldump` through `MYSQL_PWD`, never argv.

On the node, `--site-data=<dir>` loads it **after** the baseline: signature → PacsId equals the
`.epcfg` → baseline hash equals the installed schema's → load → count every table against the
manifest. Any mismatch is exit 2 with the table named. Without `--site-data` the node has a
schema and no society, and the log says so.

**Known gap, stated:** some of the 397 "master" tables are society data reached through another
table (no society column of their own). Each needs a join-path OVERRIDE in the classification
before a ledger pack from that node is complete. The review is open (`review_needed` in the
classification file).

---

## 6. Operator: install a node

### 6.1 The machine

| | Required | Checked by |
|---|---|---|
| OS | **Debian 12** (or Ubuntu / a Debian derivative, `ID_LIKE=debian`) with **systemd** running | `OsVersionCheck` — anything else is a **block naming ADR-0010** |
| Privilege | root | `AdminRightsCheck` — an unprivileged installer cannot create units or own directories |
| RAM | ≥ 8 GB (16 recommended) | `RamCheck`, block / warning |
| Disk | ≥ 100 GB free on the data volume, ≥ 10 GB on the system volume | `DiskSpaceCheck` (`ERP-INST-PRE-0004`) |
| Ports | 3306, 6379, 9092, 443 free, plus every service port in the map | `PortAvailabilityCheck` names the holder |
| Filesystem | case-sensitive (ext4/xfs), because MySQL is initialised with `lower_case_table_names=0` and the value is fixed forever | `MySqlBootstrapper` refuses before initialising |
| Packages | `nftables`, `libaio1`, `libnuma1`, `libicu72` (the e2e installs exactly these) | the units fail to start otherwise |
| Not present | any MySQL data directory at `<DataRoot>/mysql/data` | re-initialising would destroy a society's books — refused |

### 6.2 Point the installer at Linux paths

Configuration precedence, lowest first: `appsettings.json`, `appsettings.Production.json`, then
environment variables prefixed `EPACS_` (`__` for `:`). Either ship a Production file (§4.5) or:

```bash
export EPACS_Installer__DataRoot=/data/epacs
export EPACS_Installer__BinaryRoot=/opt/epacs
export EPACS_Installer__ManifestPath=/media/release-manifest.yaml
export EPACS_Installer__ExpectedSigningThumbprint=<release cert SHA-1>
```

### 6.3 Dry run, then apply

```bash
# what would happen — changes nothing; this is the default
/media/Installer.CLI --mode=install --config=/media/site.epcfg --media=/media --site-data=/media/GJ-0001.epdata

# do it
/media/Installer.CLI --mode=install --config=/media/site.epcfg --media=/media --site-data=/media/GJ-0001.epdata --apply

# unattended rollout (no console; the run log is the record)
/media/Installer.CLI --quiet --mode=install --config=/media/site.epcfg --media=/media --site-data=/media/GJ-0001.epdata --apply
```

Windows-style `/flag:value` works everywhere `--flag=value` does. `--help` prints the full list.

What the pipeline does with `--apply`, in order, each a checkpoint: verify the manifest
signature and every payload hash → prechecks (a single block stops it, nothing touched) → load
the service map **from the verified `config` payload** (never a loose file) → platform: service
accounts (`useradd --system --shell /usr/sbin/nologin`), directory ownership and modes, the
nftables table `inet epacs` written to `/etc/nftables.d/epacs.nft` and loaded → extract the
release into `/opt/epacs/releases/<version>` → initialise MySQL, create accounts, impose the
baseline (**counted before and after**), load the site data (**counted per table**) → render
each service's configuration and rewrite its sibling URLs to `127.0.0.1:<port>` and the DB
password (mode 0640) → write and enable `epacs-<name>.service` units in start order → flip
`/opt/epacs/current` to the release → start → health window (`HealthWindowSeconds`, 120 s) →
verdict.

### 6.4 Exit codes — an interface the rollout tooling branches on

| | |
|---|---|
| **0** | Success (or a dry run) |
| **1** | Precheck failure — a prerequisite was not met; **nothing was changed** |
| **2** | Operation failure — the log names the phase; the run is checkpointed |
| **3** | Health check failure after install — services registered, one or more not healthy, **named** |
| **4** | **Mode or engine not implemented in this build — nothing was done.** Do not treat the node as upgraded, restored or backed up |
| **5** | Refused — another installer holds the lock (PID named), or a required input is missing |
| **64** | Usage error, at the prompt, before anything is stopped |
| **99** | Unexpected error |
| **130** | Cancelled (Ctrl+C); checkpointed and resumable — run the same command again |

### 6.5 What to check afterwards — count, do not trust rc=0

```bash
systemctl list-units --type=service --state=active 'epacs-*' --no-legend | wc -l     # = entries in the map less components that are off (32 with eventing off)
readlink /opt/epacs/current                                                          # releases/<version>
nft list table inet epacs                                                            # the firewall table exists
getent passwd l2r2 epacs-db                                                          # the accounts exist, nologin
stat -c %a /opt/epacs/releases/<version>/services/l3_FAS/appsettings.json            # 640
MYSQL_PWD=… /opt/epacs/current/mysql/bin/mysql --defaults-file=/data/epacs/mysql/my.cnf -uroot -N -B \
  -e "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='epacs' AND TABLE_TYPE='BASE TABLE'"   # 1256
curl -s http://127.0.0.1:5090/health/live                                            # the installer agent (Services:Agent:HealthPort)
curl -s http://127.0.0.1:5080/health/live                                            # the sync agent (Services:Sync:HealthPort)
ls /data/epacs/config/                                                               # site.epcfg, site.epcfg.sig, pacs-table-classification.json
ls /data/epacs/logs/installer/                                                       # the run log (12.4)
```

The installer's own census is the gate; these are the operator's cross-checks. A service that
is *listening* but not answering its health path is reported as **Listening**, not Healthy, and
the run exits 3 naming it.

### 6.6 Where things live on the node

```
/opt/epacs/
  current -> releases/3.4.0/                 ← the junction; flipped atomically on upgrade, flipped back on failure
  releases/3.4.0/{dotnet,mysql,garnet,services/<repo>,sync,agent}
/data/epacs/
  mysql/{data,logs,my.cnf}                   ← the society's books; never removed by an uninstall
  config/{site.epcfg,site.epcfg.sig,pacs-table-classification.json,…}
  installer/{state.json,baseline.sha256}     ← checkpoints; the hash of the baseline the node was born with
  keys/master.key                            ← the secret store's key, 0600, random per node
  packs/{outbound,inbound}                   ← §7.4
  sync/pack-ledger.json                      ← the hash chain and watermark
  backups/                                   ← §7.1
  logs/{installer,agent,sync,mysql}
/etc/systemd/system/epacs-*.service
/etc/nftables.d/epacs.nft
```

---

## 7. Operator: day two

Every mode is dry-run without `--apply`. Every mode refuses before it changes anything if a
precondition fails (§8).

### 7.1 Backup

```bash
Installer.CLI --mode=backup --apply
```

Dumps the database and the attachments; encrypts each file with AES-256-GCM (chunked, a fresh
nonce per chunk); wraps the file key to the **node** (`backup.kek` in the secret store) and to
the **state** (`Backup:Encryption:RecoveryPublicKeyPath`); writes `backup-manifest.json` with
per-file plaintext and ciphertext hashes and an HMAC (`backup-manifest.json.mac`); then
**verifies by reading back** — unwraps, decrypts, checks the dump is complete. A backup that
cannot be read is not a backup, and the mode exits 2. The package goes to every
`Backup:Targets` entry; `WarnOnSameVolume` says so when one of them is the data disk.

### 7.2 Restore

```bash
Installer.CLI --mode=restore --backup=/data/epacs/backups/bk-2026-09-13T02-00 --apply
```

Never guesses which backup (`--backup` is required, 64 otherwise). Verifies the manifest MAC and
every hash; takes a **safety backup** of the current data first and names it; decrypts into a
staging directory; loads; **counts** — a restore that leaves the database empty is exit 2 with
the safety backup named (`mysql` exits 0 on empty input). Attachments are restored after the
database.

### 7.3 Repair, upgrade, uninstall

```bash
Installer.CLI --mode=repair  --media=/media                     # what is wrong
Installer.CLI --mode=repair  --media=/media --apply             # re-lay what the release owns; data untouched
Installer.CLI --mode=repair  --media=/media --regenerate-config --apply   # DISCARDS hand edits to service config
Installer.CLI --mode=upgrade --media=/media-3.5.0 --apply       # backs up first; flips the junction; flips back on failure
Installer.CLI --mode=uninstall --apply                          # stops, deregisters, removes the firewall table and binaries; KEEPS the data
```

Repair re-lays binaries, units, permissions (18.4) and configuration from the installed
`site.epcfg`; it refuses a medium of a different version (that is an upgrade, and doing it as
repair would skip the backup) and refuses to regenerate config with no `.epcfg` known. Upgrade
refuses a version outside the manifest's declared window, an older version, and a pre-upgrade
backup that does not verify; **the migration step is not built** (17.5) — an upgrade that
changes the schema is refused until it is. Uninstall never deletes business data without
`--purge-data --override-token=<jwt> --confirm="PURGE <pacs_id>"`, and in this build every
purge token is refused by `DenyAllOverrideTokenValidator`: the governance gate is unimplemented,
and a permissive stub would expose an irreversible operation before its gate was written.

### 7.4 Packs — how data moves

`Sync.Agent` (`epacs-ePACSSync`) exports a **ledger pack** on `Packs:ExportIntervalMinutes`
(default daily) into `<DataRoot>/packs/outbound/<pacs>-L-<seq>/`: the society's rows changed
since the watermark (GTID), one `data/<table>.sql` per table as `REPLACE INTO`, a manifest with
per-table counts, the previous pack's manifest hash (**the chain**), the schema fingerprint, and
a CMS signature by the site certificate (`Packs:SigningPfxPath`, password in the env named by
`Packs:SigningPfxPasswordEnv`). Optionally encrypted to the state (`Packs:RecipientPublicKeyPath`).
Carry the directory to the state on the stick, or let a link upload it.

**Policy packs** arrive in `<DataRoot>/packs/inbound/<pacs>-P-<seq>/` and are applied every
`Packs:ApplyIntervalMinutes` (15): signature by the pinned state signer
(`Packs:InboundSignerThumbprint`, `RequireSignedInbound=true`), PacsId, chain continuity (a gap
— seq N+2 without N+1 — is refused), schema fingerprint, then load and **count**. A pack applied
once is never applied again (the ledger records it). A rejected pack is moved aside and named in
the log; the sweep continues with the next.

**What the state does with a ledger pack is owed** (`l2r2 pack ingest`, exactly-once by
`(pacs_id, pack_seq)`): designed in ADR-0011, not yet built. A pack is re-exportable from its
watermark, so a lost stick loses nothing and (encrypted) leaks nothing.

### 7.5 Support bundle and the agents

The support bundle collects logs, configuration (redacted: passwords, connection strings,
Aadhaar, phone numbers), the drift report and the health verdicts into one package encrypted
to the state's public key — a plaintext bundle on a USB stick was the alternative. The
Installer Agent keeps polling health, disk, drift (its baseline survives a restart), certificate
expiry and clock drift, and schedules backups; both agents answer `GET /health/live` on their
map ports so the service map can probe them like any other unit.

---

## 8. What the installer refuses

Refusing is most of what an installer should be good at. Every row is a test in
`RefusalMatrixTests` or the engine's own suite, with a **nothing-changed oracle** (directory hash
before == after) where the row says "before anything is touched".

| Situation | Exit | Reason |
|---|---|---|
| Another installer holds the lock | 5, names the holder's PID | Two processes registering services is unrecoverable |
| Manifest fails signature, or any payload fails its hash | 2, before anything is touched | The only tamper-evidence in force |
| A control file (service map, templates, baseline) altered on the medium | 2, before anything is touched | Control payloads are re-hashed at the moment of use (G35); a loose file beside the manifest is never read |
| Any blocking precheck | 1, before anything is touched | A half-installed node is worse than none |
| Malformed service map | 2, before anything is touched | Loaded ahead of the first mutation on purpose |
| Mode or engine with no implementation | 4 | Never 0. Today: Windows ACL/firewall/accounts, `Packs:Mode=stream`, the migration step |
| Data volume cannot host `lower_case_table_names=0` | 1, before anything | Fixed at initialisation; a node built wrongly has to be flattened |
| MySQL data directory already populated | 1, before anything | Re-initialising would destroy a society's books |
| Baseline applied but the table count did not move | 2 | rc=0 is not evidence |
| Site data: unsigned, wrong PacsId, wrong baseline hash, a count short | 2, table named | The node would be born as the wrong society, or an incomplete one |
| Install with no `.epcfg` | 5 | The node would install under a defaulted identity |
| Unsigned `.epcfg` | 64 unless `--allow-unsigned-config` | It decides identity, data root and backup targets |
| Repair against a medium of a different version | 2, before anything | That is an upgrade; repair would skip the backup and the migrations |
| Repair that must regenerate config with no `.epcfg` | 2 | Broken configuration left in place and reported as success is worse |
| Upgrade whose pre-upgrade backup does not verify | 2, before staging | A backup that cannot be read is not a way back |
| Upgrade to an older version, or outside the declared window | 2, before anything | No migration backwards; an untested path |
| Restore with no `--backup` | 64 at the prompt | The installer will not guess which backup overwrites the data |
| Restore whose manifest MAC or a file hash fails | 2, before the safety backup | Tampered or truncated |
| Restore that leaves the database empty | 2, safety backup named | The count is the evidence |
| Inbound pack: unsigned, impostor signer, gap in the chain, tampered, replayed, schema mismatch | rejected and named; the sweep continues | Exactly-once by sequence; the chain is the integrity |
| Purge without token + typed confirmation | 64 at the prompt | Caught before anything is stopped |
| Purge with any token | refused by `DenyAllOverrideTokenValidator` | The governance gate is unimplemented |
| Payload carrying a credential (at build time) | `epacs-media` 3 | A medium must not ship TD-123 secrets |
| Anything, without `--apply` | dry run, 0 | Dry run is the default |

---

## 9. QA: prove it, in order

The principle carried from the estate: **a test asserts on effective behaviour and counts, never
on file text or rc=0.** Three layers — unit (pure logic), contract (generated artefacts against
the estate's own files), integration (a real MySQL, a real Debian). What is automated is listed
here with its expected result; what is not is the manual matrix in §9.4.

### 9.1 Unit and contract — 445 tests, by area

Run as in §3. Then read [`docs/testing/installer-evidence.md`](docs/testing/installer-evidence.md)
(or render it from your own TRX). What each area proves:

| QA area | Test classes | What is asserted |
|---|---|---|
| Media integrity | `HashVerifierTests`, `ManifestParserTests`, `MediaBuilderTests`, `VerifiedMediaTests` | every payload hashed; a flipped byte anywhere is refused; a control file altered after the manifest is refused at use; verdict before detail |
| Secrets, audit chain, support bundle | `SecretGateTests`, `TamperEvidenceTests` | the gate's rules equal `config-hygiene.py`'s; a real ERPClient `appsettings.json` from `r2-dev-stable` is refused; secrets never sit in the store in clear and the master key is random, on disk, root-only; generated passwords are long, random and never repeat; the audit chain detects tampering at entry N and a deleted entry at the gap; the bundle redacts what it must and is encrypted to the state when a key is configured |
| Topology | `TopologyContractTests`, `ServiceMapLoaderTests` | 28 applications + 5 infrastructure entries; ports unique; start orders follow the tiers; the Windows map has the same set |
| Configuration | `ConfigGeneratorTests`, `PayloadConfigRewriterTests`, `SiteConfigLoaderTests` | sibling URLs → `127.0.0.1`; external hosts untouched; the real FAS/ERPClient/Loans files re-parse; `.epcfg` signature verified, wrong signer refused |
| Prechecks | `PrecheckTests` | each check with a passing and a failing case driven by the real machine; a non-Debian host blocks naming ADR-0010 |
| Platform | `LinuxPlatformEnginesTests`, `SystemdUnitWriterTests` | the exact commands, the nftables ruleset, the units — dry run issues nothing, apply issues exactly N |
| Database | `DatabaseBootstrapTests`, `SchemaDriftTests`, `SchemaFingerprintLiveTests` (live) | initialise/accounts/baseline counted; census; an unreadable secret store throws rather than reads as empty; fingerprint drift detected end to end on a real MySQL |
| Pipeline | `InstallerPipelineTests`, `InstallerStateMachineTests`, `InstallerLockTests`, `CurrentLinkTests`, `CompositionRootTests`, `CliOptionsTests`, `PrecheckRunnerTests` | every transition; checkpoint persisted and resumed at each phase; the lock names its holder; the junction flip and its rollback; an unrecognised flag is an error, never dropped |
| Health | `HealthAggregatorTests` | healthy / listening / failed; one failing service → exit 3 naming it; "started without error" masks nothing |
| Backup / restore | `BackupCryptoTests`, `BackupRestoreEngineTests` | round trip; wrong key refused, not garbage; nonces never reused; tampered byte refused; wrap/unwrap to node and state; empty restore refused; safety backup named |
| Packs | `PackEnvelopeTests`, `PackEnginesTests`, `PackLiveTests` (live) | chain continuity; every refusal by name with a real CMS signer; export scope (the other society's rows never travel); replay/gap/tamper/impostor sweep; the round trip on a real MySQL, counted |
| Day two | `UpgradePathTests`, `RepairEngineTests`, `UninstallAndMonitorTests` | refusals by version and window; repair re-lays only what the release owns; purge refused with and without a token; the data survives every refusal; the drift baseline survives a restart |
| Refusals | `RefusalMatrixTests` | one test per row of §8 that says "before anything is touched", with the nothing-changed oracle |

**Pass criterion:** 0 failed; every skip named in the evidence pack with a reason that is true
of the machine it ran on. On the Linux CI leg, where MySQL and the client are provided, **a skip
is a failure**.

### 9.2 The evidence pack

Every CI run uploads `test-results-<os>` containing `installer.trx` and the rendered
`installer-evidence.md` — the refusal matrix, per-engine counts, the skipped list with reasons,
and every test by name. `build/check-tasks-evidence.py` then fails the build if `tasks.md`
claims a test that the TRX does not contain. QA's handover starts from that file, not from a
claim.

### 9.3 The Debian end-to-end — 22 counted gates

[`tests/integration/debian-e2e.sh`](tests/integration/debian-e2e.sh) installs the harness
payload (stand-ins for the 28 services) with a real MySQL 8.4 on a real Debian 12 with systemd,
and prints a **COUNT at every gate**, stopping at the first that is wrong:

| Step | Gates (expected) |
|---|---|
| 1 Publish | 2 harness services published |
| 2 Runtime | bundled `dotnet` present; `mysqld` present |
| 3 Medium | `epacs-media build --unsigned` exits **2**; `verify` exits 0 |
| 4 Install | dry run 0; `--apply` 0; **active `epacs-*` units == entries in the map**; `current` → `3.3.0`; schema table count printed; `nft list table inet epacs` exists; `getent passwd l2r2`; service `appsettings.json` mode **640**; `site.epcfg` kept |
| 5 Backup / tamper / restore | backup 0; `backup-manifest.json.mac` present; **0** plaintext `.sql` in the package; restore 0 after a table is tampered |
| 6 Repair | binary deleted → repair 0 → binary re-laid |
| 7 Uninstall | 0; **0** `epacs-*` units remain; `/data/epacs/mysql/data` kept |

Run it:

```bash
# on a Debian 12 VM as root (≈600 MB of downloads the first time: MySQL and the runtime)
sudo tests/integration/debian-e2e.sh
EPACS_E2E_KEEP=1 sudo tests/integration/debian-e2e.sh      # keep the node for inspection

# or exactly as CI does, in a privileged systemd container
docker run -d --privileged --name e2e --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw -v "$PWD:/src" jrei/systemd-debian:12
docker exec e2e bash -c "apt-get update -qq && apt-get install -y -qq curl xz-utils ca-certificates libaio1 libnuma1 nftables libicu72"
docker exec e2e bash -c "curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet"
docker exec e2e bash -c "cd /src && tests/integration/debian-e2e.sh" | tee e2e.log
```

Or dispatch `e2e-debian` from the Actions tab; it uploads `e2e.log`, the installer's run logs
and the units' journal as the `e2e-debian` artefact. **Status: scripted, scheduled nightly,
not yet run.** The same script pointed at `build/stage-l2r2-medium.py`'s output runs the real
product; what it does not prove is stated in its header (the 28 real services, the site data
pack, a power cut).

### 9.4 The manual matrix — what QA runs beyond the automation

Each row: do it, record the exit code, and **prove nothing changed** where the row says so
(`find /opt/epacs /data/epacs -type f -exec sha256sum {} + | sort > before.txt` … `diff before.txt after.txt`).

| # | Scenario | Expect | Nothing changed? |
|---|---|---|---|
| M1 | Install with a medium whose manifest `.sig` was replaced | 2, "signature" in the log | yes |
| M2 | Install after editing one byte of `payloads/config/service-map.yaml` on the stick | 2, names `config` | yes |
| M3 | Install on a machine with `/data/epacs/mysql/data` already populated | 1, names the directory | yes |
| M4 | Install on an ext4 volume (pass) and on a case-insensitive mount (block) | 0 / 1 naming `lower_case_table_names` | — |
| M5 | Install with `site.epcfg` unsigned, without the allow flag | 64 | yes |
| M6 | Install with an `.epdata` whose `pacs_id` differs from the `.epcfg` | 2, both ids named | yes (schema imposed, no rows) |
| M7 | Install with an `.epdata` cut against a different baseline | 2, "baseline" named | as M6 |
| M8 | Edit one count in `.epdata/manifest.json` upward | 2, the table and both counts named | as M6 |
| M9 | `kill -9` the installer during extraction, during the DB phase, and during the junction flip; re-run the same command | resumes at the recorded phase; exactly one `current`; no unit registered twice; counts unchanged | — |
| M10 | Pull the power during backup; re-run | the partial package is not listed as a backup; a new one verifies | — |
| M11 | Restore from a package with one file truncated | 2, the file named, safety backup named | yes |
| M12 | Repair with a medium of version +1 | 2, "upgrade" in the message | yes |
| M13 | Upgrade to version −1; upgrade outside the window; upgrade with a pre-upgrade backup that fails verify | 2 each, before staging | yes |
| M14 | Uninstall; then `ls /data/epacs/mysql/data` | 0; the data is there | — |
| M15 | `--mode=uninstall --purge-data --apply` without a token; with any token | 64; refused by the validator | yes |
| M16 | Stop `epacs-l3_FAS`; wait one poll | agent reports **Failed** naming it; `restart` recovery fires per the map | — |
| M17 | Hand-edit a service's `appsettings.json`; wait one drift interval; restart the agent; check again | drift reported both times (the baseline survived the restart) | — |
| M18 | Post transactions; wait for the export; carry the outbound pack to a bench with the state signer; verify chain and counts; delete it from the node; re-export | second pack has seq +1 and `prev_hash` = first's manifest hash; the re-export from the watermark is identical | — |
| M19 | Place a policy pack signed by the wrong certificate; a pack with seq N+2 before N+1; a pack with one byte altered; the same pack twice | each rejected and named; applied once | — |
| M20 | On the node, `curl` each service port; `curl` each agent's `/health/live` | every port answers; agents 200 | — |
| M21 | Set `Components:Eventing:Enabled=true` in the installer's config and install | Kafka + JRE registered and started; medium larger by ~290 MB | — |
| M22 | Install on Windows | 4 naming the missing platform engine; nothing registered | yes |

### 9.5 What the estate owes QA before "done"

The first green `e2e-debian` run; the same script against a medium staged from the 28 real
services with a real `.epdata` (needs a state database — the compose `localdb` seeded from a
state capture is the bench); the power-cut matrix as a chaos suite (`harness/tests/Harness.ChaosTests`
is an empty project today); the 30-day long-offline soak (`Harness.LongOfflineTests`, empty).
These are listed in `tasks.md` W9 and are not claimed here.

---

## 10. Configuration reference

Read by `Installer.CLI` and both agents from `appsettings.json` → `appsettings.Production.json`
→ environment `EPACS_<Section>__<Key>`. `${DataRoot}` / `${BinaryRoot}` expand inside values.
Site-specific values (identity, backup targets, schedules) come from the `.epcfg` and cannot be
set here.

| Section | Key | Default | Meaning |
|---|---|---|---|
| `Installer` | `DataRoot`, `BinaryRoot` | `D:\ePACSData`, `C:\Program Files\ePACS` | **Set the Linux paths** (§6.2) |
| | `ManifestPath` | `release-manifest.yaml` | resolved against `--media` |
| | `ExpectedSigningThumbprint` | null | **set it**; null accepts any chain-trusted signer |
| | `HealthWindowSeconds` | 120 | how long a service has to become healthy after start |
| | `ServiceMapPath` | `config/service-map.yaml` | inside the verified `config` payload |
| `Precheck` | `MinRamGb` / `RecommendedRamGb` | 8 / 16 | block / warning |
| | `MinDataDiskFreeGb` / `MinSystemDiskFreeGb` | 100 / 10 | block |
| | `RequiredPorts` | 3306, 6379, 9092, 443 | plus every port in the map |
| | `MinLinuxKernelMajor` | 5 | |
| `Components` | `Cache:Enabled` / `Cache:Provider` | true / `redis` | the cache holds the idempotency keys; off = not registered (never registered-and-stopped) |
| | `Eventing:Enabled` | false | Kafka + JRE, ~290 MB; nothing in the pack path needs it |
| `Services` | `MySql:Port`, `MySql:DatabaseName`, `Sync:HealthPort` (5080), `Agent:HealthPort` (5090), `Applications:*` | from the generated topology | do not hand-edit `Applications` |
| `Backup` | `Targets[]` | `${DataRoot}/backups` | every entry receives the package |
| | `Schedule:{Daily,WeeklyDay,WeeklyTime}` | 02:00 / Sunday / 03:00 | the agent runs it |
| | `Encryption:RecoveryPublicKeyPath` | null | **set it**: the state can open the backup if the node is lost |
| | `Encryption:RecoveryPrivateKeyPath` | null | the state's side only, for `restore` from a wrapped key |
| | `LargeDbThresholdGb`, `TargetFreeSpaceMultiplier`, `WarnOnSameVolume` | 5 / 1.5 / true | |
| `Packs` | `Mode` | `packs` | `stream` exits 4 (ADR-0011) |
| | `Root`, `LedgerPath`, `ClassificationPath` | `${DataRoot}/packs`, `${DataRoot}/sync/pack-ledger.json`, `${DataRoot}/config/pacs-table-classification.json` | |
| | `SigningPfxPath`, `SigningPfxPasswordEnv` | null, `EPACS_SITE_PFX_PASSWORD` | the site certificate that signs outbound packs |
| | `RecipientPublicKeyPath` | null | encrypt outbound packs to the state |
| | `InboundSignerThumbprint`, `RequireSignedInbound` | null, true | the state signer the node pins |
| | `ExportIntervalMinutes`, `ApplyIntervalMinutes` | 1440, 15 | |
| `Monitoring` | poll / disk / drift / cert / clock intervals | 60 s / 900 s / 3600 s / 6 h / 30 min | overridable from the `.epcfg` |

Secrets the installer generates (MySQL root and application passwords, `backup.kek`) live in
the secret store under `<DataRoot>/keys/master.key` (0600, random per node). They are never on
a command line (`MYSQL_PWD` / stdin) and never in a generated file except the application
connection string, mode 0640, owned by the service account — the estate's ErpConfig adoption is
partial (FAS 24 and Loans 47 raw `ConfigurationBuilder` sites read the file, not the
environment), which is why.

---

## 11. The harness

`harness/ePACS.SyncHarness.sln` — a stand-in application payload (`Pacs.Fas.Api`, a 435-line
simulation of FAS with an outbox; `Pacs.Loans.Api`, `Pacs.SyncWorker`) and the **frozen**
NLDR/Kafka protocol rig (`Nldr.Api`, `Nldr.SyncWorker`). It exists so the chassis can be
exercised without the 28 real services, and the Debian e2e installs it for that reason. It is
not the product; ADR-0011 froze the stream it models.

```bash
cd harness
docker compose -f docker/docker-compose.minimal.yml up -d     # Kafka + MySQL ×2 + Redis ×2
dotnet run --project src/Pacs.Fas.Api                        # :5101
dotnet test tests/Harness.ContractTests                       # 15 tests
```

`docs/test-harness/TESTERS-README.md` is its tester guide; `Harness.IntegrationTests`,
`Harness.ChaosTests` and `Harness.LongOfflineTests` are scaffolding with no facts yet.

---

## 12. CI

| Workflow | Trigger | What it proves |
|---|---|---|
| [`ci.yml`](.github/workflows/ci.yml) | push / PR on `master`, `baseline/**` | Linux **and** Windows: restore, build (0 warnings), 445 tests with a MySQL 8.4 (`lower_case_table_names=0`) and the client provided on Linux so **no test that could run is skipped**; renders the evidence pack; `check-tasks-evidence.py`; harness contract tests; win-x64 publish of the CLI and the harness; assembles and verifies an **unsigned** smoke medium (exit 2 expected, so a release pipeline cannot mistake it for a release) |
| [`e2e-debian.yml`](.github/workflows/e2e-debian.yml) | nightly 02:00 IST, `workflow_dispatch` | §9.3, in a privileged `systemd-debian:12` container; uploads `e2e.log`, installer logs, the journal |
| [`build.yml`](.github/workflows/build.yml) | push | packages the shared libraries to GitHub Packages — **not** a gate, by design |

Before you push, run what CI runs: §3, then `python3 build/check-tasks-evidence.py`.

---

## 13. Troubleshooting

| Symptom | Cause | Do |
|---|---|---|
| Build errors that mention analysers (`CA…`) on a fresh clone | wrong SDK | `dotnet --version` must be `10.0.302`; install it, do not edit `global.json` |
| `epacs-media build` exits 3 | a payload carries a credential | the message names file and key; fix it in the module on `r2-dev-stable` (`config-hygiene.py`), never with `--allow` for a real secret |
| `epacs-media build` exits 2 | built unsigned | expected for `--unsigned`; for a release, pass `--pfx` and set `EPACS_PFX_PASSWORD` |
| Install exits 1 "Windows 10 1809 required" on Debian | you are on a build before 2026-09-13 | the OS check compared the kernel patch level with a Windows build; fixed and pinned by test |
| Install exits 1 naming `lower_case_table_names` | case-insensitive data volume | use ext4/xfs; the setting cannot be changed after initialisation |
| Install exits 2 naming `config` / `config-templates` / `db` | a control payload does not match the manifest | rebuild the medium; a loose file beside the manifest is never read (G35) |
| Install exits 5 "another installer" | a previous run holds the lock, or crashed mid-phase | check the PID named; if it is gone, re-run — the state machine resumes |
| Install exits 3 naming a service as **Listening** | the port answers but the health path does not | the service has no `/health/live` yet (`MapErpHealth()` owed); the tcp floor is what the map can prove today |
| Every inter-service call takes 75 s and fails | sibling URLs still point at a dev host | the rewrite did not run (repair with `--regenerate-config`), or the URL is in a section the rewriter does not own — see `sibling-urls.json` |
| Restore exits 2 "empty" with a safety backup named | the dump was a placeholder or truncated | restore the safety backup; investigate the original package's manifest |
| A pack is rejected "gap" | a previous pack never arrived | apply them in sequence; the ledger names the next expected `seq` |
| `Packs:Mode=stream` exits 4 | the NLDR stream is frozen | ADR-0011; use packs |
| Windows: exit 4 naming `LinuxAclEngine` etc. | platform engines are Linux-only | by design until the Windows engines exist |
| `generate-topology.py --check` fails in CI | a module changed its port/project, or a new one appeared | `--apply` in the workspace and commit `topology/` here |

---

## 14. Documents and ADRs

| | |
|---|---|
| [`tasks.md`](.kiro/specs/epacs-offline-installer/tasks.md) | per-item status audit — **the truth for "is it built"** |
| [`docs/testing/installer-evidence.md`](docs/testing/installer-evidence.md) | the evidence pack from the last run |
| [`AGENTS.md`](AGENTS.md) | full project context for assistants |
| [`docs/engineering-guide.md`](docs/engineering-guide.md) | engineering conventions |
| [`docs/test-harness/`](docs/test-harness/) | the harness design and its tester guide |
| L2-R2 workspace `docs/offline-installer-assessment-and-plan.md` | the review of record (§12: 2026-09-13) |
| L2-R2 workspace `docs/product/offline-pacs-node-operating-model.md` | the NABARD-facing operating model |
| L2-R2 workspace `docs/adr/ADR-0007-offline-pacs-node.md` | the estate-side mirror of the four rulings |

ADRs: [0001](docs/adr/ADR-0001-wix-v4-burn-bootstrapper.md) WiX (proposed, unbuilt; CMS over the manifest is the signature in force) ·
[0002](docs/adr/ADR-0002-garnet-over-redis.md) Garnet ·
[0003](docs/adr/ADR-0003-kafka-kraft-single-node.md) Kafka single node (off by default) ·
[0004](docs/adr/ADR-0004-kestrel-self-hosted.md) Kestrel ·
[0005](docs/adr/ADR-0005-dbup-schema-migrations.md) DbUp — **superseded by 0013** ·
[0006](docs/adr/ADR-0006-sync-abstraction-layer.md) sync abstraction — superseded by 0011 for the offline node ·
[0007](docs/adr/ADR-0007-harness-native-deployment.md) / [0008](docs/adr/ADR-0008-harness-deployment-profiles.md) harness ·
[0009](docs/adr/ADR-0009-framework-dependent-payload.md) framework-dependent payload ·
[0010](docs/adr/ADR-0010-debian-systemd-linux-target.md) Debian + systemd ·
[0011](docs/adr/ADR-0011-sync-is-a-pack-not-a-stream.md) packs, not a stream ·
[0012](docs/adr/ADR-0012-approvals-above-the-pacs-are-deferred.md) approvals deferred ·
[0013](docs/adr/ADR-0013-migration-runner-applies-the-estates-migration.md) the migration runner.

---

## 15. Owed

Recorded in `tasks.md` and in the workspace's §12.7; none of it is claimed above.

- The **first run** of the Debian end-to-end, then the same against the real 28-service medium with a real `.epdata`.
- State-side pack **ingest** (`l2r2 pack ingest`) and the push from `ln_cbspushqueue`.
- The remaining ADR-0012 cuts in the estate: `Approvals:AadhaarOtp:Required`, *pending-external* on T12/OTB/DMS, `DeferredJournalSink` for SMS/e-mail.
- Join-path OVERRIDEs for the 397 tables without a society column.
- `MapErpHealth()` in `utils-traceability` and one line in each of 28 services.
- 17.5 the migration step of an upgrade (ADR-0013 unblocked it).
- Windows ACL/firewall/account engines; the signing ceremony (G3); site hardware and administration (G0); delta upgrade (G24); the offline NuGet mirror for building on a disconnected host (G14).
- TD-123 rotation — the owner's; the secret gate makes shipping the unrotated values impossible, it does not rotate them.

---

Proprietary — Intellect Design Arena Ltd.
