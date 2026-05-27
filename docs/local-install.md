# Local ACE install playbook

A reproducible, ordered procedure for getting an upstream
[ACEmulator/ACE](https://github.com/ACEmulator/ACE) server running on a
Windows 11 developer box, so a contributor can investigate the codebase
against a live server.

This doc is procedural. The design rationale for what we eventually want
to change in ACE lives in [`ace-fork-plan.md`](ace-fork-plan.md). The
open questions this local install lets you investigate live in
[`research/ace-investigation.md`](research/ace-investigation.md).

## Contents

- [Scope](#scope)
- [Known-good versions](#known-good-versions)
- [Prerequisites](#prerequisites)
- [Step 1: Install MariaDB](#step-1-install-mariadb)
- [Step 2: Clone and build ACE](#step-2-clone-and-build-ace)
- [Step 3: Create databases and import schemas](#step-3-create-databases-and-import-schemas)
- [Step 4: Import the world data](#step-4-import-the-world-data)
- [Step 5: Place the DAT files](#step-5-place-the-dat-files)
- [Step 6: Configure `Config.js`](#step-6-configure-configjs)
- [Step 7: First interactive run](#step-7-first-interactive-run)
- [Step 8: Connect with an AC client](#step-8-connect-with-an-ac-client)
- [Optional appendix: Run as a Windows service via NSSM](#optional-appendix-run-as-a-windows-service-via-nssm)
- [Troubleshooting](#troubleshooting)
- [See also](#see-also)

## Scope

This playbook covers a single-developer install on Windows 11 for the
purpose of reading and modifying ACE source against a running server.

Out of scope: Linux and macOS hosts, production deployment, multi-tenant
hosting, public-internet exposure, hardening for shared environments,
and anything Asheron's Call client-side beyond launching it locally.

## Known-good versions

These are the exact versions verified in the most recent install pass.
Treat them as "verified with", not "requires forever" — upstream ACE may
move faster than this doc.

| Component | Version |
|---|---|
| Windows | 11 |
| .NET SDK | 10.0.300 |
| MariaDB | 12.2 |
| ACE source | `master` at commit `9bc20cbd` |
| ACE-World-Database release | latest at time of install |
| Disk required (server + DBs + DATs) | ~5 GB |

## Prerequisites

You need local administrator rights on the box. The install touches the
Windows service control manager, opens listening UDP ports, and writes
to `C:\ACE\`.

Install these once:

- **.NET 10 SDK**. The exact runtime target ACE uses (`net10.0`) is
  pinned in `Source/ACE.Server/ACE.Server.csproj`. Install via
  `winget install --id Microsoft.DotNet.SDK.10`. Verify with
  `dotnet --info` — the SDK list must include 10.0.x.
- **An Asheron's Call client install** you already have the right to
  use. The server needs the four DAT files that ship with the client;
  see [Step 5](#step-5-place-the-dat-files). This repository will not
  link to, mirror, or otherwise help locate client assets. Use a copy
  you legally own.

## Step 1: Install MariaDB

ACE expects MySQL or MariaDB. The verified install uses MariaDB 12.2.

```powershell
winget install --id MariaDB.Server --silent
```

The installer registers a Windows service `MariaDB` that auto-starts on
boot. Confirm with:

```powershell
Get-Service MariaDB
sc qc MariaDB
```

### Database user: choose your tradeoff

Pick **one** of these two paths. The rest of this doc assumes you made a
choice and updated [Config.js](#step-6-configure-configjs) accordingly.

**Recommended: dedicated `ace` user with a password**

Create a user that has privileges only on the three ACE databases:

```sql
CREATE USER 'ace'@'localhost' IDENTIFIED BY 'CHOOSE_A_PASSWORD';
GRANT ALL PRIVILEGES ON ace_auth.*  TO 'ace'@'localhost';
GRANT ALL PRIVILEGES ON ace_shard.* TO 'ace'@'localhost';
GRANT ALL PRIVILEGES ON ace_world.* TO 'ace'@'localhost';
FLUSH PRIVILEGES;
```

Run the schema imports in later steps as this user.

**Shortcut: passwordless `root@localhost` (disposable dev boxes only)**

The MariaDB installer's default for `root@localhost` is empty-password
local-socket auth. ACE's stock `Config.js.example` is wired for exactly
this. It is the fastest path to a running server.

It is **not** a safe default outside a disposable single-user
developer box. Do not use this on any host that:

- Other accounts can log into,
- Will ever be reachable from outside `localhost`,
- Will outlive this experiment.

If you take the shortcut, write down "this box has passwordless root
MariaDB" somewhere obvious so you remember to fix it before re-using
the machine.

## Step 2: Clone and build ACE

Clone upstream ACE wherever you keep source. The verified path is
`C:\Users\darin\repos\ACE`.

```powershell
cd C:\Users\darin\repos
git clone https://github.com/ACEmulator/ACE.git
cd ACE
```

Build the server in Release configuration:

```powershell
dotnet build -c Release Source/ACE.Server/ACE.Server.csproj
```

The output exe lands at
`Source/ACE.Server/bin/x64/Release/net10.0/ACE.Server.exe`. The build
is slow the first time (NuGet restore) and fast on subsequent passes.

## Step 3: Create databases and import schemas

ACE uses three databases:

- `ace_auth` — accounts and access levels
- `ace_shard` — per-character live state (the "shard" is the running
  world's state)
- `ace_world` — static world content (creatures, items, spells,
  landblocks, encounters)

### Destructive warning

The commands below **drop and recreate** the three databases if they
already exist. They assume those databases are disposable local ACE
installs. Do not run them against a database server that holds anything
you care about.

Create the databases:

```sql
DROP DATABASE IF EXISTS ace_auth;
DROP DATABASE IF EXISTS ace_shard;
DROP DATABASE IF EXISTS ace_world;
CREATE DATABASE ace_auth  CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE ace_shard CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE ace_world CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
```

Import the schemas (these files live in the ACE clone). PowerShell's
`<` is reserved, so pipe via `Get-Content`:

```powershell
$base = "C:\Users\darin\repos\ACE\Source\ACE.Database\DatabaseSetupScripts\Base"
Get-Content "$base\AuthenticationBase.sql" | mysql -u <user> -p ace_auth
Get-Content "$base\ShardBase.sql"          | mysql -u <user> -p ace_shard
Get-Content "$base\WorldBase.sql"          | mysql -u <user> -p ace_world
```

Replace `<user>` with `ace` (recommended path) or `root` (shortcut).
Drop `-p` if you went with the shortcut.

After this step, `ace_auth` has accounts/accesslevel tables, `ace_shard`
has the per-character schema, and `ace_world` has empty content tables
ready for the next step.

## Step 4: Import the world data

ACE ships an empty `ace_world`. Content is published as periodic dumps
in the [`ACEmulator/ACE-World-Database`](https://github.com/ACEmulator/ACE-World-Database)
repo's Releases page. Download the latest release asset (a single large
`.sql` archive, typically ~150 MB extracted) and import it:

```powershell
Get-Content path\to\extracted\world.sql | mysql -u <user> -p ace_world
```

This takes several minutes. When it finishes, spot-check:

```sql
USE ace_world;
SELECT COUNT(*) AS landblock_instance_rows FROM landblock_instance;
SELECT COUNT(*) AS spell_rows FROM spell;
```

The verified install saw `landblock_instance` ~363k rows and `spell`
~6k rows. Empty or near-empty counts mean the import didn't apply.

## Step 5: Place the DAT files

ACE reads four binary files that ship with the original Asheron's Call
client:

- `client_portal.dat`
- `client_cell_1.dat`
- `client_highres.dat`
- `client_local_English.dat`

### Source and redistribution

Copy these files from your own AC client install (commonly under
`C:\Turbine\Asheron's Call\`). They are copyrighted client assets.

This repository will not:

- Commit DAT files.
- Host DAT files.
- Link to DAT downloads or mirrors.
- Help locate copies of the DAT files.

If you do not have a client install you are entitled to use, stop. The
rest of this playbook does not apply to you.

### Where to put them

Copy the four DATs to `C:\ACE\Dats\`. That path matches the default
`DatFilesDirectory` in `Config.js.example`, so the rest of the install
needs zero changes if you put them there.

```powershell
New-Item -ItemType Directory -Path C:\ACE\Dats -Force
# Then copy the four .dat files into C:\ACE\Dats\.
```

Verify:

```powershell
Get-ChildItem C:\ACE\Dats\*.dat | Select-Object Name, Length
```

You should see all four files. Approximate sizes match the client
install (`highres` is the largest; `cell_1` second; `portal` and
`local_English` smaller).

## Step 6: Configure `Config.js`

ACE reads `Config.js` from the directory the exe runs in:
`Source/ACE.Server/bin/x64/Release/net10.0/Config.js`. The build does
not create one; you must copy or create it.

```powershell
$bin = "C:\Users\darin\repos\ACE\Source\ACE.Server\bin\x64\Release\net10.0"
Copy-Item "$bin\Config.js.example" "$bin\Config.js"
```

Edit `Config.js` to match your setup:

- **If you used the recommended dedicated-user path**: set
  `MySql.Authentication.Username`, `MySql.Shard.Username`, and
  `MySql.World.Username` to `ace`, and set the matching `Password`
  fields to the password you chose in [Step 1](#step-1-install-mariadb).
- **If you used the passwordless-root shortcut**: the example file
  already has `root` + empty password on `127.0.0.1:3306`. No change
  needed.
- **`DatFilesDirectory`**: leave at `c:\\ACE\\Dats\\` if you put the
  DATs there per [Step 5](#step-5-place-the-dat-files). Otherwise set
  it to wherever the DATs actually are. Double backslashes are required
  because the file is parsed as JavaScript-ish JSON.
- **`AllowAutoAccountCreation`**: leaving this `true` is convenient for
  a dev box because the first user to connect creates their account on
  the fly. See the warning in [Step 8](#step-8-connect-with-an-ac-client).

Other defaults (`WorldName`, port 9000, `AutoUpdateWorldDatabase`,
`AutoApplyDatabaseUpdates`) are fine for a local install.

## Step 7: First interactive run

Run the server in a foreground console once before automating anything.
This is where boot failures are easiest to read.

```powershell
cd C:\Users\darin\repos\ACE\Source\ACE.Server\bin\x64\Release\net10.0
.\ACE.Server.exe
```

The `Logs\ACE_Log.txt` file relative to the exe is the primary record
of what happens. On a clean boot you will see, in order:

1. `Starting ACEmulator...`
2. `Initializing ConfigManager` / `Initializing ModManager`
3. `Pruned N invalid friends found on friend lists` (fast, within a
   second or two)
4. `Automatic Server version check started...` → `No Update Required!`
5. `Automatic World Database Update started...` → version line + `No
   Update Required!`
6. `Automatic Database Patching started...` → `complete` (a couple of
   seconds)
7. Four lines starting `Successfully opened c:\ACE\Dats\...` (the four
   DATs)
8. Three lines: `Successfully connected to ace_auth/ace_world/ace_shard
   database on 127.0.0.1:3306`
9. `Binding ConnectionListener to 0.0.0.0:9000` and `0.0.0.0:9001`
10. `World started and is currently Closed and will open automatically
    when server startup is complete`

If any of those steps prints an exception or refuses to advance, see
[Troubleshooting](#troubleshooting).

Stop the server cleanly from the interactive console by typing
`server-stop` (or close the window). For automated runs, see the
[NSSM appendix](#optional-appendix-run-as-a-windows-service-via-nssm).

## Step 8: Connect with an AC client

You need the original `acclient.exe`. ACE's wiki documents the
exact command-line arguments used to point the client at a local
server. Typical invocation:

```
acclient.exe -h 127.0.0.1 -p 9000 -a testaccount:testpassword
```

(Run from the client's install directory. The `-a` argument is
`account:password`.)

### First-user admin warning

With `AllowAutoAccountCreation=true`, the **first** account to connect
to a fresh `ace_auth` is auto-promoted to Admin access level. This is
fine on a closed dev box.

It is **not** fine if the server is reachable from the network. The
default `Config.js.example` binds to `0.0.0.0:9000`, which on a box
without a host firewall rule means anyone routable to your machine can
race to claim the admin account. Either:

- Keep the box behind a firewall that blocks UDP 9000/9001 from
  outside, or
- Set `AllowAutoAccountCreation=false` after creating your admin
  account and add later accounts manually via the admin console.

## Optional appendix: Run as a Windows service via NSSM

This is convenience only. Nothing in M0 investigation requires the
server to be a service — `ACE.Server.exe` in a console window works
just as well. Skip this section unless you specifically want the
server to auto-start on boot.

### Install NSSM

```powershell
winget install --id NSSM.NSSM --silent
```

### Create the service

`C:\ACE\Logs` must exist before NSSM points its stdout/stderr at it:

```powershell
New-Item -ItemType Directory -Path C:\ACE\Logs -Force | Out-Null
```

```powershell
$exe = "C:\Users\darin\repos\ACE\Source\ACE.Server\bin\x64\Release\net10.0\ACE.Server.exe"
$dir = Split-Path $exe
nssm install ACEServer $exe
nssm set ACEServer AppDirectory $dir
nssm set ACEServer Description "ACEmulator Server"
nssm set ACEServer DisplayName "ACEmulator Server"
nssm set ACEServer Start SERVICE_DELAYED_AUTO_START
nssm set ACEServer DependOnService MariaDB
nssm set ACEServer AppExit Default Restart
nssm set ACEServer AppThrottle 30000
nssm set ACEServer AppRestartDelay 5000
nssm set ACEServer AppStdout "C:\ACE\Logs\nssm-stdout.log"
nssm set ACEServer AppStderr "C:\ACE\Logs\nssm-stderr.log"
nssm set ACEServer AppRotateFiles 1
nssm set ACEServer AppRotateOnline 0
nssm set ACEServer AppRotateBytes 10485760
nssm set ACEServer AppStopMethodConsole 30000
nssm set ACEServer AppStopMethodWindow 5000
nssm set ACEServer AppStopMethodThreads 5000
nssm start ACEServer
```

### Known limitations of this setup

These are real and worth understanding before relying on auto-start.

- **No graceful shutdown.** Windows services have no console for
  Ctrl+C and `ACE.Server.exe` has no window for `WM_CLOSE`. NSSM falls
  through its stop methods quickly and effectively force-kills the
  process within a second of `Stop-Service`. ACE auto-persists shard
  state every ~31 minutes (`ShardPlayerBiotaCacheTime` in `Config.js`),
  so the loss window is bounded, but any in-flight player action at
  shutdown is lost. For dev that's acceptable; for anything else, build
  an out-of-band shutdown trigger that drives ACE's `server-stop`
  console command before stopping the service.

- **LocalSystem is over-privileged.** NSSM defaults the service to
  `LocalSystem`. That is the verified setup, not a recommendation.
  Migrating to a dedicated low-privilege account or a virtual service
  account (`NT SERVICE\ACEServer`) requires giving that account read
  access to the ACE source bin directory, full access to the DAT
  directory, and full access to `C:\ACE\Logs\`. Out of scope for this
  doc.

### Operator-local helper script (not in this repo)

A small `.bat` next to your service install with `start` / `stop` /
`restart` / `status` / `tail` shortcuts saves a lot of typing. Per the
repo policy ([CONTRIBUTING.md](../CONTRIBUTING.md)) we do not commit
such scripts here; keep yours alongside the install.

A minimal example, for inspiration only:

```bat
@echo off
if "%1"=="status" sc query ACEServer & exit /b
if "%1"=="start"  sc start  ACEServer & exit /b
if "%1"=="stop"   sc stop   ACEServer & exit /b
if "%1"=="tail"   powershell -NoProfile -Command "Get-Content C:\ACE\Logs\ACE_Log.txt -Wait -Tail 50" & exit /b
echo Usage: %~n0 {status^|start^|stop^|tail}
```

## Troubleshooting

These are issues observed during verified install passes. If you hit
something not on this list, the first place to look is
`Logs\ACE_Log.txt` next to the exe.

- **`No connection could be made` on database connect.** MariaDB
  service isn't running, or `Config.js` points at the wrong host/port.
  Confirm with `Get-Service MariaDB` and `mysql -u <user> -p -h
  127.0.0.1`.

- **`Access denied for user`.** Wrong username or password in
  `Config.js`. If you took the shortcut path and changed `root`'s
  password later, the example config won't work — update `Config.js`
  to match. If you took the dedicated-user path, the `GRANT` statements
  must name all three databases.

- **`Could not open dat file ...`.** Either the DATs are missing or
  `DatFilesDirectory` doesn't match where they are. Verify both:
  `Get-ChildItem C:\ACE\Dats\*.dat` and
  `Select-String -Path Config.js -Pattern DatFilesDirectory`.
  Remember the double backslashes.

- **`Address already in use` on `0.0.0.0:9000` or `0.0.0.0:9001`.**
  Another ACE process is already running (common after a crash where
  the service restarted), or another program holds the port. Find it
  with `Get-NetUDPEndpoint -LocalPort 9000` and stop the holder.

- **Service starts but no `Binding ConnectionListener` line appears.**
  Server is still booting (the world database update can take 30+
  seconds on a fresh import) or it's stuck. Tail the log; if there's
  no new line for a minute, capture the last 100 lines and investigate.

- **World database update appears to hang on every start.** Disk I/O
  contention with another process, or a very slow disk. Acceptable on
  HDD; on SSD it should be seconds. Not a server bug.

- **NSSM log file grows unbounded.** `AppRotateOnline` is on by
  default in some NSSM versions; the playbook sets it to 0 explicitly
  so rotation only happens at service restart. ACE's own `ACE_Log.txt`
  is rotated by log4net independently and isn't affected.

- **Service stops in under a second.** Expected — see the "no
  graceful shutdown" note in the NSSM appendix. This is a limitation of
  the dev-only service wrapping, not a sign the server crashed.

## See also

- [`ace-fork-plan.md`](ace-fork-plan.md) — what we eventually change in
  ACE, and why
- [`research/ace-investigation.md`](research/ace-investigation.md) —
  the five open questions a local ACE install lets you answer
- [`architecture.md`](architecture.md) — the design this playbook
  exists to enable contributor investigation of
- [`../README.md`](../README.md) — project overview
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — repo conventions
- [ACEmulator/ACE](https://github.com/ACEmulator/ACE) — upstream
- [ACEmulator/ACE-World-Database](https://github.com/ACEmulator/ACE-World-Database)
  — world data releases
