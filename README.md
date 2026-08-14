# Marco — Agentless Network Inventory

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
- **Inventory (authenticated Windows hosts):** system (make/model/serial/chassis/BIOS/motherboard), OS
  (edition/version/build/arch/install date/uptime), CPU(s), memory (total + per-slot), storage (physical disks +
  logical volumes), network adapters (IP/MAC/speed/DHCP/DNS/gateway), and **installed software** from the registry
  uninstall keys (64-bit, 32-bit, and per-user), deduplicated to match Programs and Features.

Additional collectors (services, antivirus, printers, USB, hotfixes, drive/CPU temperatures), the itemization
tool, HTML/SQLite export, AD browse, and saved-scan history are planned for later phases.

---

## Portability — one file you can send

Marco publishes as a **single, self-contained `Marco.exe`** (~138 MB) with the .NET runtime embedded. No .NET
install is required on your machine, and **nothing is installed on the targets** (it is agentless). It runs
unelevated (`asInvoker`) — the admin rights it needs live on the *remote* targets, not locally.

Data (settings, logs, the run log, credential profiles, saved scans) is written **portable-first**: to a
`Marco.Data\` folder beside the exe when that location is writable (USB stick, unzipped folder), otherwise to
`%LOCALAPPDATA%\Marco\`. The current location is shown in the app header.

> **Credential profiles do not travel.** If you choose to save credential profiles, they are encrypted with
> Windows DPAPI scoped to *your* account. A profile saved on one machine/account will **not** decrypt on another —
> that is the intended security property, not a bug. Re-enter credentials on the new machine.

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

1. **Targets** — enter CIDR (`10.0.0.0/24`), ranges (`10.0.0.1-50`), single IPs, or hostnames (one per line), or
   load a host file. Expansions over 65,536 addresses ask for confirmation.
2. **Discovery** — click **Start**. The grid fills as hosts respond; **Pause**/**Cancel** any time.
3. **Credentials** — add one or more credential sets (left panel). They are tried in order per host; the first
   that authenticates is remembered for that host. With none configured, Marco uses your current session token.
4. **Inventory** — select a host and **Inventory selected**, or **Inventory alive** for all live Windows hosts.
   The detail pane shows the full inventory; per-collector status is listed at the bottom.
5. **Export** — **Export CSV** (a `machines.csv` plus keyed `software.csv` / `disks.csv` / `adapters.csv`) or
   **Export JSON** (the full nested structure). Exports respect the current filter and include scan metadata.

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

## EDR / code signing

An unsigned executable that prompts for admin credentials and sweeps a subnet is, behaviorally, indistinguishable
from malware — EDR products will quarantine it, and single-file .NET self-extraction is itself a common heuristic
trigger. **Sign the release** (`build\sign.ps1`, timestamped) with a certificate from a reputable CA. Each release
ships a `Marco.exe.sha256` for verification. If your org's tooling still flags the signed binary, add an EDR
exclusion for it by path/hash.

---

## Security posture

- Authenticated access only. No exploitation, no credential guessing/spraying, no brute force.
- No agents, no persistence, no data exfiltration — inventory data is read and displayed locally.
- Credentials live in memory as `SecureString`, are persisted only under DPAPI (your account) if you opt in, and
  are **never** written to disk in plaintext, to logs, or to exports.
- The only outbound (internet) call in the product is an optional, clearly-labelled public-IP lookup — **off by
  default** and not built in this phase.
- A local **run log** (`Marco.Data\logs\runlog.jsonl`) records targets, timestamps, operator, and success/failure
  counts for access attribution — never credentials.

---

## Project layout

```
Marco.Core/         domain model, ScanController, InventoryRunner, all interfaces (BCL only)
Marco.Discovery/    liveness, DNS/NBNS naming, ARP, OUI table, device classifier
Marco.Inventory/    IWmiSession (System.Management), collectors, remote registry
Marco.Credentials/  credential sets, DPAPI persistence, per-host mapping
Marco.Export/       CSV / JSON writers + reopenable scan document
Marco.App/          WPF UI (MVVM)
Marco.Tests/        xUnit (103 tests)
build/              publish.ps1, sign.ps1, refresh-oui.ps1
```
