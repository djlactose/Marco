# Marco — Agentless Network Inventory

[![Buy Me a Coffee](https://img.shields.io/badge/☕_Buy_me_a_coffee-djlactose-yellow)](https://buymeacoffee.com/djlactose)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Marco is a standalone Windows desktop tool for **agentless** discovery and deep hardware/software inventory of
Windows machines on networks you are **authorized to administer**. It scans IP ranges, classifies live devices
(Windows PC/server, printer, network gear, Unix/Linux), and for reachable Windows hosts pulls a detailed
inventory over authenticated WMI/CIM (DCOM) and the remote registry, using credentials you supply. Results show
in a sortable, filterable grid and export to CSV and JSON.

> **Authorization.** This is an administrative tool. Use it only on networks and machines you are authorized to
> manage. It uses only standard, authenticated Windows management interfaces — it never exploits vulnerabilities,
> never guesses or sprays credentials, and only ever uses credentials you explicitly provide.

---

## What it collects

- **Discovery:** liveness (ICMP + TCP fallback), reverse DNS / NBNS naming, ARP MAC + OUI vendor, device type.
- **Inventory (Windows hosts, over WMI/DCOM):** system (make/model/serial/chassis/BIOS/motherboard), OS
  (edition/version/build/arch/install date/uptime), current-or-last logged-on user, CPU(s), memory (total +
  per-slot), storage (physical disks with SSD/HDD/NVMe type, bus, health, SMART, temperature and wear where the
  system exposes them; logical volumes), network adapters (IP/MAC/speed/DHCP/DNS/gateway), and
  **installed software** from the registry uninstall keys (64-bit, 32-bit, per-user), deduplicated to match
  Programs and Features. Software reads use the SMB/Remote-Registry path when available and fall back to
  StdRegProv-over-WMI when it isn't (e.g. over a VPN, or when the Remote Registry service is off).
  - **Updates & servicing:** installed hotfixes, feature release (22H2/24H2…) and full build incl. UBR,
    pending-reboot flags (servicing stack, Windows Update, file renames, computer rename, domain join), WSUS /
    Automatic Updates policy.
  - **Security posture:** antivirus / antispyware / firewall products (Security Center) and Defender status
    (real-time protection, signature age, last scan, tamper protection), firewall profiles, BitLocker per volume,
    TPM version, Secure Boot and UEFI/BIOS, UAC, Remote Desktop + NLA, SMB1 / signing / encryption,
    virtualization-based security (Credential Guard, HVCI), and whether LAPS manages the machine (never the
    password). Everything is read-only, and a value that could not be determined stays blank rather than "off".
  - **Users:** local accounts, members of the local Administrators group, user profiles on disk with last use,
    every interactive / RDP session (not just the console user), per-account last logon.
  - **Services & startup:** Windows services (state, start mode, run-as account, path) with an "automatic but
    stopped" count, startup items, and — off by default — non-Microsoft scheduled tasks.
  - **Peripherals:** monitors with EDID make/model/**serial**/size, GPUs (VRAM, driver), printers (with the TCP/IP
    port address), attached USB devices, battery health (full-charge vs design capacity, cycles), ACPI thermal
    zone, and — off by default — the USB storage devices ever connected.
- **Inventory (Linux/Unix hosts, over SSH):** OS (distro/version/kernel/arch), hostname, CPU, memory, storage
  (lsblk + df), network adapters, current/last login, and installed packages (dpkg / rpm / apk). Password auth;
  the runner routes by device type automatically (Windows→WMI, Linux→SSH).

Each collector can be switched on or off in the left panel ("Inventory collectors"); the choice persists and is
recorded in the run log. Heavier or audit-oriented collectors (scheduled tasks, USB history) default off. Sources
that only exist on some SKUs — Security Center on client Windows, BitLocker on Pro/Enterprise, the Storage
Management classes on Windows 8 / Server 2012 and later — are reported as "not available" notes rather than
failures.

## Beyond a single scan

Marco keeps and reasons about scans, not just runs them:

- **Scan history & compare** — every completed run is auto-saved (gzipped) to the `scans\` folder and listed in
  the left panel. **Compare…** diffs the current grid against an earlier run by serial/MAC (so DHCP churn reads as
  an address change, not a new machine), flagging software added/removed, security posture regressions, new local
  admins, and hardware swaps.
- **Compliance & fleet health** — ~22 opinionated rules over the collected posture (BitLocker, SMB1, firewall,
  RDP+NLA, Secure Boot, TPM 2.0, LAPS, patch age, OS end-of-support…) give a per-host score and a fleet rollup.
  Null inputs read *unknown*, never *fail*. Extend or retune with your own JSON packs in `Marco.Data\compliance\`.
- **Lifecycle / EOL** — a bundled table flags operating systems past or nearing end of support, plus approximate
  hardware age from the BIOS date (refresh with `build\refresh-eol.ps1`).
- **Known-device baseline** — bless a scan as the set of known devices; later scans flag anything new (a rogue
  Raspberry Pi jumps out), with a *NEW?* state for Wi-Fi MAC randomization that inventory resolves.
- **Prerequisite doctor** — when inventory fails, a per-host "why" with a copy-paste fix, and a fleet rollup
  grouping hosts by cause (firewall, token filtering, Remote Registry, SSH…). Emits text only, never runs it.
- **Client profiles** — bundle an engagement's targets, scoped credentials, and report branding; sharable as a
  `.marcoclient.json` that never carries credentials.
- **Per-host actions & Wake-on-LAN** — right-click for RDP/SSH/web-admin/C$/ping (only where the evidence fits),
  and wake asleep hosts by MAC before a scan.
- **Branded assessment report** — one-click client-ready HTML (executive summary, compliance donut, prioritized
  findings, asset appendix); print to PDF.
- **Headless CLI** — `Marco.exe scan …` for Task Scheduler (see *Scheduled / headless scans* below).

The itemization tool (cross-machine query), SQLite export, and AD browse remain planned for later phases.

---

## Portability — one file you can send

Marco publishes as a **single, self-contained `Marco.exe`** (~138 MB) with the .NET runtime embedded. No .NET
install is required on your machine, and **nothing is installed on the targets** (it is agentless). It runs
unelevated (`asInvoker`) — the admin rights it needs live on the *remote* targets, not locally.

Data (settings, logs, the run log, credential profiles, saved scans) is written **portable-first**: to a
`Marco.Data\` folder beside the exe when that location is writable (USB stick, unzipped folder), otherwise to
`%LOCALAPPDATA%\Marco\`. The current location is shown in the app header.

> **Credential profiles do not travel.** Credential profiles are saved automatically (`credentials.dat`),
> encrypted with Windows DPAPI scoped to *your* account. A profile saved on one machine/account will **not**
> decrypt on another — that is the intended security property, not a bug. Re-enter credentials on the new machine.

Scan options (targets, discovery toggles, concurrency, the beta-updates choice) persist in `settings.json` in the
same data folder. Concurrency is capped automatically at what the machine can sustain — derived from its logical
processors and the ephemeral TCP port range, since each in-flight host opens up to 11 TCP probes at once. The cap is
shown beside the box, and an out-of-range value (typed, or loaded from a `settings.json` saved on a bigger machine)
snaps to it.

---

## Build & run

Requires the **.NET 8 SDK**.

```powershell
# Restore, build, test
dotnet test Marco.sln

# Run from source
dotnet run --project Marco.App

# Publish the single portable exe  ->  artifacts\publish\Marco.exe (+ .sha256)
pwsh build\publish.ps1

# Sign it for distribution (see "EDR / code signing" below)
pwsh build\sign.ps1 -CertThumbprint <your-cert-thumbprint>
```

`build\publish.ps1 -NoExtract` produces the multi-file folder variant (exe + WPF native DLLs beside it, no
runtime extraction) instead of the single self-extracting exe.

---

## Using it

1. **Targets** — enter CIDR (`10.0.0.0/24`), ranges (`10.0.0.1-50`), single IPs, or hostnames (one per line, or
   comma/space-separated on a line), or load a host file. Several networks can go in one scan. Expansions over
   65,536 addresses ask for confirmation.
2. **Discovery** — click **Start**. The grid fills as hosts respond, in numeric address order (the **Address**
   column sorts numerically, so 10.0.0.2 comes before 10.0.0.10), with one collapsible section per IP block you
   entered (each CIDR/range; single IPs and hostnames share an "Individual hosts" section) — untick **Group by IP
   block** for a flat list. **Pause** parks new hosts (hosts already mid-probe finish — the status shows how many
   are still "in flight"); **Cancel** stops within about a second and the status reads "Cancelling…" until the last
   in-flight probes have drained. Both also work during inventory.
3. **Credentials** — add one or more credential sets (left panel). They are tried in order per host; the first
   that authenticates is remembered for that host. With none configured, Marco uses your current session token.
4. **Inventory** — select a host and **Inventory selected**, or **Inventory alive** for all live Windows hosts.
   The detail pane shows the full inventory; per-collector status is listed at the bottom.
5. **Export** — **Export CSV** (a `machines.csv` plus keyed `software.csv` / `disks.csv` / `adapters.csv`) or
   **Export JSON** (the full nested structure). Exports respect the current filter and include scan metadata
   (including the app version that produced them). **Open scan…** loads a previously exported JSON scan back
   into the grid.
6. **Multiple windows** — open Marco again to scan another network side by side; each window's title shows its
   targets. All windows share the same data folder: `settings.json` is whatever the last-closed window saved,
   credential edits made in one window are picked up by the others before they save, and run-log lines carry the
   writing process's `pid`.

---

## Scheduled / headless scans

The same executable runs a scan from the command line — no window — when the first argument is `scan`:

```
Marco.exe scan --targets <file|token[,token...]> [--out <path.json>]
          [--csv <dir>] [--collectors Name,Name] [--concurrency N]
          [--no-inventory] [--credential-label <label>] [--client <name>]
          [--quiet] [--log <path>]
```

At least one of `--out` / `--csv` is required. `--targets` accepts a host-file path or inline tokens; `--client`
loads a saved client profile's targets and credential scope. The run is also copied into the scan history, so an
interactive Marco sees it. Exit codes: `0` ok, `1` usage error, `2` scan failed, `3` output write failed,
**`4` credentials could not be decrypted**.

**Credentials come from the saved store** (DPAPI, bound to the Windows user who saved them). Marco never accepts a
plaintext password on the command line. This has one hard consequence for Task Scheduler: the task must run **as
that same user, with a stored password** — "Run whether user is logged on or not" *with* the password saved, not
an S4U logon (which cannot unlock the DPAPI key). Exit code `4` means exactly this mismatch.

Create a nightly task (run once, from an elevated prompt, adjusting paths and the account):

```
schtasks /Create /TN "Marco nightly" /SC DAILY /ST 02:00 /RU DOMAIN\svc-marco /RP * ^
  /TR "\"C:\Tools\Marco.exe\" scan --targets \"C:\Tools\targets.txt\" --out \"C:\Tools\scans\nightly.json\" --quiet"
```

The CLI path never touches the auto-update pipeline, so a scheduled scan can never swap the exe under a running
interactive instance.

---

## Target prerequisites

For authenticated inventory, each **target** needs:

| Requirement | Why | Port/Service |
|---|---|---|
| DCOM / WMI reachable | remote WMI queries | TCP **135** + dynamic RPC |
| File & Printer Sharing (SMB) | remote registry session (`IPC$`) | TCP **445** |
| Remote Registry service | installed-software enumeration | service (trigger-started; may be disabled in hardened builds) |
| Windows Firewall rules | allow "Windows Management Instrumentation (WMI)" and "Remote Event Log Management" | — |

## Credentials & the two classic gotchas

- **Cross-domain / workgroup — `runas /netonly`.** To supply different network credentials than your logon
  without joining the domain, launch Marco with:
  ```
  runas /netonly /user:TARGETDOMAIN\admin "C:\path\to\Marco.exe"
  ```
  Then choose *current session token* (no credential set), or add explicit sets in the UI.

- **Local admin denied over the network — `LocalAccountTokenFilterPolicy`.** Using a **local** administrator
  account (not a domain account), WMI/registry access can be denied even with correct credentials, because UAC
  remote token filtering strips the admin token over the network. Marco detects this case and tells you exactly
  this. Fix on the **target**:
  ```
  reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System `
    /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f
  ```
  (Or use a domain account, which is not subject to this filtering.)

---

## Releases, versioning & auto-update

Releases are built by GitHub Actions from two branches:

- **`develop` → beta pre-releases.** Every push publishes a pre-release tagged `vX.Y.Z-beta.N` (N = the CI run
  number). Only the newest five betas are kept.
- **`main` → stable releases.** A push publishes a release tagged with the `<Version>` from
  `Directory.Build.props` — the single source of truth for Marco's version. The workflow refuses to re-release
  an existing tag, so bump `<Version>` on develop right after each stable release.

Each release carries `Marco.exe`, its `Marco.exe.sha256`, and a **build provenance attestation** — anyone can
verify an exe traces to this repository's CI with `gh attestation verify Marco.exe --repo djlactose/Marco`.

**Auto-update.** On launch (and every 12 hours) Marco checks GitHub Releases. When a newer version exists it
**asks** — a dialog names the version and offers to download and install it now (if a scan is running, the
question waits until it finishes). *Yes* downloads the exe with progress in the header, verifies its SHA-256
against the published checksum, swaps it in and restarts Marco. *No* leaves a header link ("Update vX available
— download and install") for later and asks again at the next launch; a background check never asks twice for the
same version in one session, while **Check for updates now** always does. Nothing is downloaded until you say
yes; a download that completed but wasn't applied (Marco was closed first) is applied automatically at the next
launch. A failed update rolls back automatically (crash-loop sentinel + the kept `.old` exe), and every step is
recorded in the run log. The **Include beta (pre-release) updates** checkbox (left panel, "About & updates")
selects the channel; by default a beta build follows betas and a stable build follows stable. Set the environment
variable **`MARCO_NO_UPDATE=1`** to disable the updater entirely (e.g. via GPO/SCCM for managed fleets).

With several Marco windows open, the steps that touch the exe (apply at startup, rollback, "restart to apply")
are serialised across windows through a short-lived gate, and only one window downloads a given release (the
others report "another Marco window is downloading this update" and pick up the staged file on their next
check). After one window applies an update, a window still running the previous version keeps running from
`Marco.exe.old` until it is closed; its "restart to apply" link then says the update was applied elsewhere.

---

## EDR / code signing

An unsigned executable that prompts for admin credentials and sweeps a subnet is, behaviorally, indistinguishable
from malware — EDR products will quarantine it, and single-file .NET self-extraction is itself a common heuristic
trigger. **Sign the release** (`build\sign.ps1`, timestamped) with a certificate from a reputable CA. Each release
ships a `Marco.exe.sha256` for verification. If your org's tooling still flags the signed binary, add an EDR
exclusion for it by path/hash.

CI builds are currently **unsigned**: the release workflows contain a dormant signing step that activates when
the `CODESIGN_PFX_B64` / `CODESIGN_PFX_PASSWORD` repository secrets are added (base64-encoded PFX + password).
Once the project has published releases, [SignPath Foundation](https://signpath.org) offers free Authenticode
signing for open-source projects and is the intended path to signed CI builds. Until then, the SHA-256 checksums
and provenance attestations are the verification story.

---

## Security posture

- Authenticated access only. No exploitation, no credential guessing/spraying, no brute force.
- No agents, no persistence, no data exfiltration — inventory data is read and displayed locally.
- Credentials live in memory as `SecureString`, are persisted only under DPAPI (your account) if you opt in, and
  are **never** written to disk in plaintext, to logs, or to exports.
- Outbound (internet) calls: on launch and every 12 hours Marco checks
  `api.github.com/repos/djlactose/Marco` for a newer release over HTTPS and, when one exists, downloads it from
  GitHub and verifies it against the published SHA-256 before it is ever run. **No scan or inventory data is
  sent anywhere** — the request carries only a `Marco/<version>` user agent. Failures are silent (logged to the
  run log) so blocked networks lose nothing. Set `MARCO_NO_UPDATE=1` to disable all outbound calls.
- A local **run log** (`Marco.Data\logs\runlog.jsonl`) records targets, timestamps, operator, and success/failure
  counts for access attribution — never credentials.

---

## Project layout

```
Marco.Core/         domain model, ScanController, InventoryRunner, auto-update pipeline, all interfaces (BCL only)
Marco.Discovery/    liveness, DNS/NBNS naming, ARP, OUI table, device classifier
Marco.Inventory/    IWmiSession (System.Management), collectors, remote registry
Marco.Credentials/  credential sets, DPAPI persistence, per-host mapping
Marco.Export/       CSV / JSON writers + reopenable scan document
Marco.App/          WPF UI (MVVM)
Marco.Tests/        xUnit
build/              publish.ps1, sign.ps1, refresh-oui.ps1
.github/            CI: PR tests, beta releases (develop), stable releases (main), dependabot
```
