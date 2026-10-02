> **History.** This is the original refactor proposal from 2026-10-01, kept for its reasoning.
> Some of it was built (portable Linux release, admin plane, the decision log), some was
> superseded (see docs/ARCHITECTURE.md, "Lineage"). For how the system works today, read
> docs/ARCHITECTURE.md.

# DAoC Server: server-first architecture and refactor plan

Status: **proposal, awaiting owner sign-off.** Nothing has been moved yet.
Source of truth for facts below: the `OfflineDAoC-main` tree (release 0.33/0.33b) as audited on
2026-10-01, plus a shallow clone of upstream `OpenDAoC/OpenDAoC-Core` at `f0aaa1e` (2026-09-26).

Owner decisions this plan is built on:

| Decision | Answer |
|---|---|
| Who connects | Several humans from several machines, plus bots. LAN or port-forwarded internet. |
| World data | One-time import from the 0.33 release package; client files discarded. |
| Saved progress | Fresh start. No migration of played databases. |
| Bot iteration | C# plugin boundary, hot-reload where it is safe. |
| Runtime | Native .NET on Linux inside a distrobox. **No Docker/Podman. No Wine for the server.** |
| Admin UI | CLI first (permanent fallback), then a terminal UI over the same API. No web UI. |
| Launcher | Deleted from the new project. Legacy releases keep it. |
| Client | Out of scope. Installed separately; the server only needs to speak its protocol. |
| Where | New tree under `project/`; git host to be decided later. |
| Bot bodies vs bot AI | Bot *bodies* (entity, inventory, equipment, persistence, combat hooks) may stay woven into the core. Only the *AI* (decisions, tactics, goals, roles, tuning) must be a separately updatable module. |
| Upstream OpenDAoC | Static snapshot. An upstream pull is optional, manual and occasional, never part of a normal build. |
| Managed config | The admin GUI/CLI edits settings through the server, which applies them live and writes small server-owned config files (e.g. `config/admins.json`). |
| Init system | None. The server is a plain foreground process; works under sysvinit, OpenRC, runit, systemd or a terminal. |
| Network | Home network first; remote access assumed to work via router port forwarding. No VPN work. |

---

## 1. Diagnosis

### 1.1 One sentence

The server is a heavily modified OpenDAoC fork whose bot AI is woven through the core in both
directions, administered by a Windows launcher that edits the live database behind the server's
back, and shipped as one bundle whose world data only exists inside the client package.

### 1.2 What is actually in the tree

| Area | Where | Size / shape | Classification |
|---|---|---|---|
| Game server core | `source/server/GameServer` (minus `bots/`), `CoreBase`, `CoreDatabase`, `CoreServer` | ~2,300 C# files. Versus current upstream HEAD: **722 files / ~50k lines differ** (whitespace-insensitive; upper bound, includes upstream drift since the fork) | **True server core**, but forked hard |
| Bot AI | `GameServer/bots/` (226 files, ~49k lines), of which `bots/autonomous/` ~30k | `GameBot : GameNPC, IGamePlayer`; `BotBrain : ABrain` (4.7k lines); `GameBot.cs` 4.6k lines; ~100 `Autonomous*` policy/controller classes | **Bot/gameplay logic**, compiled into `GameServer.dll` |
| Reverse coupling | 244 core files outside `bots/` reference bot types | `GameNPC`, `GameLiving`, `GamePlayer`, `SpellHandler`, `Group`, property calculators, loot, siege, named mobs, `GameLoop`, `GameServer` | The main obstacle to "bots as a layer" |
| Pathing | `source/server/Pathing/Detour` (C++, has `CMakeLists.txt`), navmeshes only in the release package | `LibraryImport("lib/Detour")` | **True server core** (native dependency) |
| Tests | `source/server/Tests` | ~2,226 tests at 0.33; 59 bot/autonomous-related test files | Keep |
| Launcher | `source/tools/OfflineDaoc.Launcher` (`MainForm.cs` 3,125 lines, WinForms, `net10.0-windows`) | Starts server and client, edits DB directly | **Launcher/UI** acting as a **second admin plane** |
| Setup / progress import | `OfflineDaoc.Setup`, `OfflineDaoc.ProgressImport`, `Get-OfflineDAoC.ps1`, `*.cmd` | Windows | **Packaging/installer** |
| Release pipeline | `build_release_033.py`, `assemble_*`, `seal_*`, `smoke_*` | Python, snapshots a dev install, wipes progress, bundles .NET + client | **Packaging** |
| Client tooling | `tools/pet-art`, `tools/asset-tool`, `build-client-raid.py`, `OfflineDaoc.Mpk`, `inspect_*`, `patch_observer_flight.py`, helmet fix | Client art, MPK, game.dll patches | **Client-specific**, out of scope |
| Upstream container bits | `source/server/Dockerfile`, `DOLLinux.sln`, `entrypoint.sh` | Builds against upstream MySQL DB | Evidence that Linux builds are possible; not used as-is (no Docker) |

### 1.3 The four couplings that matter

**A. The launcher is a second admin plane, not a front-end.** Verified in `MainForm.cs`:

- GM: `UPDATE Account SET PrivLevel=@privilege WHERE Name=@account`, where the account is
  whatever `account.txt` holds (`PortableCredentials.cs` creates `offline` + random password).
  It refuses to run while the server is up, because the server caches the account in memory
  and would overwrite the change. It writes `offline_local_options.MakeMeGM`, which **the server
  never reads**. If the account doesn't exist, it throws and shows a message box.
- Bots: inserts `offline_world_bots` rows *and* raw `Inventory` rows for starting gear; deletes
  bots by running a cascade of `DELETE` across inventory, auction escrow, ledger, listings.
- Population and settings: writes `offline_population_settings` and `ServerProperty` directly.
- Runtime: writes `offline_runtime_status` heartbeat; detects the server by "is port 10300
  listening" and by process name `CoreServer.exe`.
- Schema: creates `offline_*` tables itself (`CREATE TABLE IF NOT EXISTS ...`), so the schema is
  partly owned by a UI program.
- The one good pattern: a DB-backed command queue, `offline_bot_commands`, that the server polls
  (`AutonomousPopulationController`). That is the seed of a real admin API.

Consequence: the launcher can't just be deleted. Every launcher action has to become a
server-side operation first.

**B. The server assumes one human on loopback.** Verified:

- `LocalPlayerTravelInput.cs`: `/travel` holds the forward key by **injecting Win32 keystrokes
  into the foreground `game.dll` window**, reading `%APPDATA%\...\Atlas\user.dat` for the key
  binding. Only on Windows, only on loopback. It fails closed, so Linux and remote players
  silently get a degraded `/travel`.
- `PlayerMovementComponent` and `ClientService` exempt loopback clients from soft link-death and
  activity timeouts. With several humans, the host gets different rules from everyone else.
- GM bootstrap exists only via the launcher: in-game `/plvl` and `/account` require Admin, so
  a fresh server has no way to make its first admin except editing the database by hand.
- **Suspected, needs the Phase 3 audit:** companion balance (`TemporaryCompanionBalance`),
  `PlayerLedPullCoordinator`, population ramp tuned for one player, realm event notices, and
  `ClientSessionDiagnostics` heuristics. Names suggest "the player"; code must be read before
  claiming anything.

**C. Bots are not a layer, they are a weave.** `GameBot` is a `GameNPC` that implements
`IGamePlayer`; core code then special-cases it in 244 files (`is GameBot` checks, calls into
static `Autonomous*` classes, `GameServer.cs` initializes `AutonomousBotGoalPolicy` and saves
`AutonomousBotRegistry`, `GameLoop` stops camp planning). Bot state is held in many
**static** registries and caches (`AutonomousBotRegistry`, `PendingSpawns`, corridor caches,
quarantines). Statics are the specific thing that will fight hot-reload.

**D. World data is trapped in the client bundle.** The customized SQLite world (camps, exchange,
`offline_*` tables, Sluaghbinder rows) and the navmeshes are not in source control. They exist
only inside `OfflineDAoC-v0.33b.zip`, which is mostly client. The release script then *removes*
progress from a developer's played DB to produce a clean world. There is no schema-migration
story: the DB is the artifact.

### 1.4 What is in better shape than it looks

- Server, database and core projects target `net10.0`, not `net10.0-windows`. Only the launcher,
  setup, progress import and two diagnostic tools are Windows-only.
- Windows performance counters are already behind `OperatingSystem.IsWindows()` guards.
- `System.Data.SQLite.Core` ships Linux native binaries; Detour has a CMake build.
- The edition switch (`ENABLE_SLUAGHBINDER`) is already a database server property, not a
  build flag. The server side of "0.33 vs 0.33b" is a setting.
- Many bot decisions already live in named policy classes (`*Policy`, `*Planner`,
  `*Catalog`), which are natural extraction units. `bot-goals.json` is already external tuning.
- Accounts and privilege are already database-backed (`Account.PrivLevel`, OpenDAoC's
  `ePrivLevel` Player=1 / GM=2 / Admin=3). The authority exists; it just has no proper door.

---

## 2. Target architecture

### 2.1 Components

```
project/
  src/
    Server.Host/          console host (was CoreServer). Linux-first. No Windows service code.
    Server.Core/          OpenDAoC fork (was GameServer minus bot decision logic). Pinned.
    Server.Data/          CoreDatabase + schema migrations for every offline_* table.
    Server.Admin/         AdminService (in-process) + Unix-socket endpoint. Owns all privileged ops.
    Bots.Abstractions/    Small interfaces + plain data types shared by core and bot modules.
    Bots.Runtime/         In-server: bot lifecycle, scheduling, plugin loading, fault containment.
    Bots.Behavior/        THE PLUGIN: decision engine, goal/camp/RvR/class policies, chat, economy.
    Detour/               native pathing (CMake -> libDetour.so)
  tools/
    daoc-admin/           CLI client for the admin socket (also has an offline/DB mode).
    daoc-tui/             terminal UI client for the admin socket (Phase 6).
    world-import/         extracts world DB + navmeshes from a verified 0.33 package.
  config/                 serverconfig template, bot tuning JSON defaults
  deploy/                 distrobox install/start scripts (no init-system integration)
  tests/                  existing server tests + admin contract tests + bot plugin tests
  docs/
```

### 2.2 Dependency direction (arrows = "depends on")

```
  daoc-admin ─┐                                   (no project reference; talks over socket)
  daoc-tui  ──┴──► admin socket protocol (JSON lines)
                         │
                         ▼
  Server.Host ──► Server.Admin ──► Server.Core ──► Server.Data ──► SQLite
       │                              │   ▲
       │                              │   │ implements IBotWorld / IBotActions,
       │                              ▼   │ raises IBotHooks events
       └────────────────────────► Bots.Runtime ──► Bots.Abstractions ◄── Bots.Behavior (plugin)
                                                                         loaded at runtime,
                                                                         never referenced
```

Rules:

1. `Server.Core` must not reference `Bots.Behavior`. Today it effectively does (244 files).
2. `Bots.Behavior` may reference `Bots.Abstractions`. In the transitional phases it may also
   reference `Server.Core` types (leaky but pragmatic, see 4.4). It never writes to the
   database directly; persistence goes through the runtime.
3. Nothing outside `Server.Admin`/`Server.Data` writes `Account`, `ServerProperty` or
   `offline_*` tables while the server runs. The CLI's offline mode is the single exception, and
   it refuses to run while the server holds the database.
4. Nothing in `src/` knows about `game.dll`, `connect.exe`, client paths, or keystroke injection.

### 2.3 Responsibility split

| Responsibility | Owner |
|---|---|
| Network protocol, login, accounts, sessions | Server.Core |
| World state, regions, zones, NPCs, items, persistence | Server.Core + Server.Data |
| Tick scheduling (game loop, ECS services, brain think scheduling) | Server.Core / Bots.Runtime |
| Pathing / navmesh queries | Server.Core (Detour) |
| Combat resolution: attack, spells, styles, effects, property calcs | Server.Core |
| Bot bodies: `GameBot` entity, inventory, equipment, persistence records | Server.Core (it is world state) |
| Bot lifecycle: spawn, retire, delete, population target, budget | Bots.Runtime |
| Bot decisions: goals, camps, targets, tactics, spell/style choice, group roles, RvR plans, chat, economy choices | Bots.Behavior |
| Bot tuning numbers | JSON in `config/bots/`, hot-reloaded |
| Privilege, accounts, bans, settings, backups, shutdown | Server.Admin |
| Install, upgrade, world import | tools/ + deploy/ |
| Client install, client patches, art | **Separate project** |

---

## 3. GM/admin decoupling

### 3.1 Model

- **Truth:** `Account.PrivLevel` stays the single source of truth (1 Player, 2 GM, 3 Admin).
  No parallel role table. It is already queryable, and in-game commands already honour it.
- **Audit:** new table `admin_audit` (utc, actor, action, target, old, new) written by every
  privileged operation, whether it came from the CLI, the TUI or an in-game command.
- **Bootstrap and managed config:** `config/admins.json` lists admin and GM account names.
  At startup, and again on account creation, listed accounts are raised to that role. The file
  is **server-owned**: the CLI/TUI ask the server to add or remove an account, and the server
  updates the DB, applies it live and rewrites the file atomically (write temp, rename).
  Hand-edited files such as `serverconfig.xml` are never rewritten by tools, so comments
  survive. The same pattern applies to any other setting that belongs in a file. When the server
  is stopped, `daoc-admin --offline` edits the same file. This replaces the launcher checkbox.
- **Live correctness:** `AdminService.SetRole` updates the DB *and* any loaded in-memory
  `Account` for online clients, then tells the player. That removes the launcher's "stop the
  server first" requirement.

### 3.2 Transport: Unix domain socket, not TCP

- The server listens on `$DATA/run/admin.sock`, mode `0600`. Authorization is "you are the OS
  user that runs the server". No password, no port, nothing exposed to the network.
- Remote admin means `ssh host` then run `daoc-admin`. That's standard, secure and long-lived.
- Protocol: one JSON object per line, request `{"id":1,"op":"account.setRole","args":{...}}`,
  response `{"id":1,"ok":true,"result":{...}}` or `{"id":1,"ok":false,"error":{"code":"not_found","message":"..."}}`.
  Boring on purpose. Any language can speak it, and `socat` can test it.
- The existing `ApiHost` (`ATLAS_API` property, ASP.NET, binds Kestrel's default, exposes
  `GET /utils/shutdown/{password}`) stays **off** and is not extended. Don't build admin on it.

### 3.3 CLI surface (first cut)

```
daoc-admin account list [--role gm|admin]
daoc-admin account show <name>
daoc-admin account create <name> [--password-stdin]
daoc-admin account set-role <name> player|gm|admin
daoc-admin account set-password <name> --password-stdin
daoc-admin account ban|unban <name> [--reason ...]
daoc-admin server status | save | shutdown [--in 5m] | broadcast <msg>
daoc-admin settings get [key] | set <key> <value>        # ServerProperty, typed + validated
daoc-admin population status | set-target <n> | enable|disable
daoc-admin bots list [--realm ..] | show <id> | create ... | retire <id> | delete <id|--all>
daoc-admin bots reload | modules | breaker status|reset     # Phase 5
daoc-admin db backup <file>                                  # SQLite online backup API
daoc-admin world reset-keeps-relics                          # ported from launcher
daoc-admin --offline account set-role <name> admin          # DB direct; refuses if server holds DB lock
```

Error behaviour, the specific thing you asked for:

```
$ daoc-admin account set-role bob gm
error: account 'bob' not found.
  Accounts are created on first login when auto-creation is on, or with:
  daoc-admin account create bob
exit code 3
```

No exception reaches the user. Exit codes are stable: 0 ok, 2 usage, 3 not found, 4 refused,
5 server unreachable. The TUI shows the same message in a status line.

### 3.4 In-game commands

`/plvl`, `/account` and the GM commands stay. Internally they call `AdminService`, so audit and
live-update behaviour is identical regardless of entry point.

### 3.5 Launcher features: where each one goes

| Launcher feature | New home |
|---|---|
| Make me GM | `account set-role` + `config/admins.json` (TUI add/remove) |
| Start/stop server, detect running | `deploy/` start script, `server status/shutdown` |
| Start client with `account.txt` | **Removed.** Client project's concern |
| XP rates | `settings set` |
| Bot creation/deletion (incl. inventory insert, auction cascade) | `bots create/delete` → server-side `BotLifecycleService`, one transaction |
| Population target/enable | `population set-target/enable` |
| Bot goals editor | edit `config/bots/*.json` (hot reload) or `settings` |
| Exchange sales ledger, realm events, RvR dashboards | read-only `daoc-admin` queries, later TUI panels |
| Keep/relic reset | `world reset-keeps-relics` |
| Client session diagnostics | **Removed** (client-side) |
| Rolling server log view | log files + `tail -f`; TUI log pane later |

---

## 4. Bot decoupling strategy

### 4.1 What "fail safely" can and cannot mean in one .NET process

Be clear-eyed about this:

| Failure | In-process plugin | Out-of-process bot service |
|---|---|---|
| Exception in decision code | **Contained**: catch per bot per tick, circuit breaker | Contained |
| Slow decision (busy loop, huge search) | Detectable, not preemptible. Measure, then disable the module | Contained (timeout) |
| Infinite loop / StackOverflow / OOM | **Takes the server down** | Contained |
| Corrupting world state through a bad call | Possible if plugin touches core directly; mitigated by the action API | Prevented by API |

Out-of-process AI is the only true isolation, but every think tick (500–1,500 ms × N bots)
would need a world snapshot over IPC, and the existing 49k lines of logic read the live world
directly everywhere. That is a rewrite in disguise. **Recommendation: in-process plugin now,
with the boundary designed so an out-of-process host stays possible later.** If the plugin only
sees `IBotWorld` / `IBotActions`, moving it across a process boundary later is a mechanical
job.

Prevention comes first: plugin code follows **no-unbounded-loop rules**. Every `while`/retry
loop and every search (pathing fallback, target scan, camp planning) has an explicit iteration or
time cap. Recursion needs a depth limit. Code review and tests enforce this; the per-tick timer
only detects what slips through.

### 4.1a Body vs AI: what is actually extracted

Bot *bodies* stay in the core and may remain woven in: `GameBot`, inventory, equipment,
persistence, the combat and spell special-cases, and think scheduling. That is world state and
game rules, and moving it buys nothing. What is extracted is the *AI*: what a bot decides to do
and how it is tuned. So the coupling work in 4.4 targets only references from core into
**decision** code. Body-related references are left alone.

### 4.2 Three tiers of change, three speeds

| Change | Mechanism | Turnaround |
|---|---|---|
| Numbers, weights, thresholds, goal lists, camp tables | JSON in `config/bots/`, file watcher, validated on load; bad file = keep previous + log | Save the file |
| Decision logic (policies, class AI, goal engine) | Rebuild `Bots.Behavior` only, then `daoc-admin bots reload` (Phase 5) or restart (Phase 4) | Seconds to build. No core rebuild |
| New capabilities the AI needs from the world (new action, new query, new hook) | Change `Bots.Abstractions` + `Server.Core` | Full rebuild, by design. These should be rare |

### 4.3 Proposed interfaces (sketch, not final)

```csharp
// Bots.Abstractions: no reference to Server.Core in the end state.
public interface IBotBehaviorModule              // one per plugin assembly
{
    string Name { get; }
    Version Version { get; }
    void Initialize(IBotModuleContext context);  // tuning, logging, clock, RNG
    IBotDecider CreateDecider(BotDescriptor bot); // class/role-specific
    void Shutdown();                              // must release everything for unload
}

public interface IBotDecider
{
    // Called by Bots.Runtime on the bot's think tick. Must not block; must not hold world refs.
    void Think(IBotView self, IBotWorld world, IBotActions act, BotMemory memory);
}

public interface IBotWorld                        // read-only queries
{
    IReadOnlyList<EntityView> Nearby(float radius, EntityFilter filter);
    PathResult FindPath(Position from, Position to);    // Detour, owned by core
    CampInfo? Camp(int campId);
    RealmWarSnapshot RvR();
    // ...grow from what the extracted policies actually read
}

public interface IBotActions                      // intents; core validates and resolves
{
    void MoveTo(Position p, MoveMode mode);
    void Attack(EntityId target);
    void Cast(SpellId spell, EntityId? target);
    void UseStyle(StyleId style);
    void Follow(EntityId leader, float distance);
    void Say(ChatChannel channel, string text);
    void RequestSellAtNextTaskBreak();            // owner rule: sell only at natural breaks
}

// Core -> bots, replacing the 244 direct references.
public interface IBotHooks
{
    void OnDamaged(EntityId bot, DamageInfo info);
    void OnGroupChanged(EntityId bot, GroupView group);
    void OnDeath(EntityId bot, EntityId? killer);
    void OnSpellResolved(EntityId caster, SpellResult result);
    // ...one per category found in the coupling inventory
}

// Plain data, owned by the runtime, survives plugin reloads.
public sealed class BotMemory { public Dictionary<string, JsonNode> Slots { get; } = new(); }
```

`BotMemory` is the key to hot reload. Long-lived state such as current goal, camp choice and
route quarantine must live in host-owned plain data, not in plugin-defined objects or plugin
statics. Otherwise the old assembly can't unload, and a reload resets every bot's mind.

### 4.4 How to get there without breaking 49k lines

Strangler pattern, in this order:

1. **Coupling inventory (automated).** A Roslyn or grep script lists every reference from
   outside `bots/` into bot types, categorised as:
   (a) `is GameBot` type checks, which usually become `IGamePlayer` (already exists) or a
   capability flag;
   (b) calls into `Autonomous*` statics, which become `IBotHooks` events or `Bots.Runtime`
   services;
   (c) bot-specific rules inside combat/spell code, which stay in core as **game rules**.
   They're not AI, so they're legitimately server-side.
   Output: a checked-in CSV with a count that has to go to zero for (b).
2. **Split body from brain.** `GameBot` (entity, inventory, equipment, persistence) and the
   think scheduling in `BotBrain` stay in core. The *decision* parts of `BotBrain` and the
   `Autonomous*` policies move to `Bots.Behavior`.
3. **Move pure policies first.** The `*Policy`, `*Planner` and `*Catalog` classes that take
   inputs and return a decision are cheap to move and to unit-test, and they're the bulk of
   what you'll tune. Examples: `BotMeleeStylePolicy`, `BotSongTwistPolicy`, `BotCasterPriority`,
   `AutonomousCampLevelPolicy`, `AutonomousPveTargetPolicy`, the class combat policies.
4. **Then controllers.** `AutonomousWorldBotController.*`, group coordinator, RvR director:
   these hold the most static state and touch the world the most. Move them last, and only
   after the hooks exist.
5. **Allow a leaky phase.** For a while `Bots.Behavior` will still reference `Server.Core`
   and touch `GameBot` directly. That's acceptable: it still gives a separate build, separate
   tests and swap-at-startup. Tighten the boundary file by file; don't block on purity.

### 4.5 Hot reload mechanics (Phase 5)

- Load `Bots.Behavior.dll` into a **collectible `AssemblyLoadContext`**. `Server.Core` and
  `Bots.Abstractions` stay in the default context, so their types are shared.
- `bots reload`: build to a new versioned folder, quiesce (skip one think cycle), call
  `Shutdown()`, drop deciders, unload the context, load the new one, recreate deciders with
  existing `BotMemory`. Verify unload with a `WeakReference` and log if the old context is
  still alive (leak).
- Things that block unload: plugin statics holding core objects, event subscriptions on core
  objects, timers/threads started by the plugin, cached delegates. These are enforced by
  analyzer rules and a reload stress test, not by hope.
- If reload fails, the old module keeps running. If the new module throws on initialize,
  roll back.

### 4.6 Fault containment

- Every `Think` call is wrapped: an exception is logged with bot id, module version and
  decision trace, and that bot falls back to a built-in **safe decider** (stop, rest or return
  to bind) for a back-off period.
- Circuit breaker per module: more than N faults in M seconds across bots disables the module
  and puts all its bots on the safe decider. `daoc-admin bots breaker status|reset`.
- Per-tick time measurement per decider. Exceeding the budget (`AutonomousAiBudget` already
  exists) is logged and counted toward the breaker.
- Decision tracing: an optional ring buffer per bot of (inputs summary, chosen action, reason).
  `daoc-admin bots show <id> --trace` is the debugging tool you'll use most.

---

## 5. Linux-first deployment

### 5.1 Recommendation (implemented in Phase 1)

- **Portable folder.** The server is released as a self-contained folder: `dotnet publish -r
  linux-x64 --self-contained` plus `Detour.so` with libstdc++ linked statically. Requirements:
  x86-64, glibc 2.34+, libicu. No .NET install. Verified directly on SteamOS and inside an
  Ubuntu 22.04 distrobox.
- **Distrobox is the build environment** and an option for running on hosts that lack libicu or
  are read-only. Box `daoc-server-box`, home `~/boxes/daoc-server-box-home`.
- **No Wine anywhere on the server side.** Wine stays in the separate client project.

### 5.2 Layout of a server folder

```
daoc-server/
  daoc-server.sh  world-import.sh  README.md  VERSION
  bin/          replaced on upgrade (self-contained publish)
  defaults/     replaced on upgrade (templates for config/)
  config/       serverconfig.xml, logconfig.xml, invalidnames.txt   (kept across upgrades)
  data/         opendaoc.sqlite3.db                                   (kept)
  navmesh/      from world-import.sh                                  (kept)
  logs/  run/
```

All paths are relative to the folder, so it can be moved. An upgrade replaces `bin/`,
`defaults/` and the scripts, and runs schema migrations (Phase 2+).

### 5.3 Install and run flow

```
tar -xzf daoc-server-<version>-linux-x64.tar.gz && cd daoc-server
./world-import.sh --package <playable-v0.33> [--edition 0.33b]
./daoc-server.sh start | stop | status
```

No init-system integration is shipped. The server is a plain foreground process that saves and
exits cleanly on SIGTERM/SIGINT (verified), so any init system or a plain terminal can run it.

### 5.4 Network

- TCP 10300 (login/game), UDP 10400 (region/UDP). Set `IP`/`UdpIP` to `0.0.0.0`, and set
  `RegionIP` to the address clients should use. For LAN that's the host's LAN IP; for internet
  it's the public IP. `DetectRegionIP` and UPnP should default **off** for predictability.
  The packet code has hard-coded private-range checks (`10.`, `192.168.`) that need reviewing
  for mixed LAN and internet clients.
- Home network is the primary target. Remote players are assumed to reach the server through
  router port forwarding of the same ports; no VPN work is planned. `AutoAccountCreation`
  defaults **off**; accounts are created with `daoc-admin`.

---

## 6. Migration plan

Each phase ends in a working server. Automated checks and real in-game checks are reported
separately, per the existing project rules.

### Phase 0: Baseline the new tree (small)

- `git init project/`. Copy `source/server/{CoreBase,CoreDatabase,CoreServer,GameServer,Pathing,Tests,sharedModules}`
  and `source/development-tools` (navmesh builder). **Do not copy** the launcher, setup,
  progress import, release scripts, client tools, `older-versions`, `package-files`.
- Record the upstream base: `UPSTREAM.md` with the OpenDAoC commit this fork most closely
  matches (find it by bisecting upstream history against our unchanged files), and the policy
  in section 9.
- Build and test on Linux inside the distrobox. Record which tests fail on Linux and why.
  Path case-sensitivity and `\` separators are the likely culprits.
- **Exit:** `dotnet build` + `dotnet test` run on Linux; failures listed, not hidden.

### Phase 1: Server boots natively on Linux (medium)

- CMake-build `libDetour.so`. Make the import resolve on Linux: .NET maps `lib/Detour` to
  `lib/libDetour.so`. Verify.
- Write `world-import` (port the manifest/hash logic from `setup-daoc.sh`, plus the
  progress-wipe policy from `build_release_033.py`).
- Serverconfig template for SQLite, with `EnableCompilation` decided explicitly
  (`DOLScriptCompiler` has a Windows branch; verify on Linux or turn it off).
- Fix case-sensitivity issues found while loading data, scripts and navmeshes.
- **Exit:** the server starts headless on Linux, bots spawn and path, and a human logs in from a
  Wine client on **another machine**.

### Phase 2: Admin plane; launcher deleted (medium)

**Status (2026-10-01): first cut done.** Admin service and socket, `daoc-admin` TUI + CLI, and
console commands. Covered: bots list/create/delete, population on/off/max, accounts
list/create/set-role, and `config/admins.json`. Verified with automated tests, and with CLI/TUI
runs against a live server (not yet clicked through by the owner).

Deviations from the plan below:
- The service lives in `src/GameServer/admin/`, not a separate `Server.Admin` project, because it
  needs GameServer internals. The shared contract is `src/Admin.Protocol`.
- `offline_bot_commands` is kept as the internal delete queue.
- New server property `max_active_bots`.
- `population off` now logs bots out of the world; the launcher's off only stopped new logins.

Still to do from this phase: XP rates, exchange ledger, realm events, keep/relic reset, schema
migrations, `admin_audit`, and the `--offline` CLI mode.

- `Server.Data` migrations: every `offline_*` table gets a versioned migration owned by the
  server (move the `CREATE TABLE IF NOT EXISTS` from the launcher).
- `Server.Admin`: `AdminService` plus the socket. Port each launcher function as a server-side
  service, then add a CLI command for each. Move bot create/delete into a single-transaction
  `BotLifecycleService` (it replaces the launcher's raw inventory and auction SQL).
  `offline_bot_commands` can remain as an internal queue or be retired.
- `config/admins.json` (server-owned), `admin_audit`, live `SetRole`.
- **Exit:** every row of the table in 3.5 has a working replacement or an explicit "removed";
  "account not found" is tested; the launcher is not in the tree.

### Phase 3: Multi-human correctness (medium, uncertain)

- Remove `LocalPlayerTravelInput`. `/travel` uses server-side movement only (whatever the
  non-Windows fallback already does; verify in game).
- Loopback exemptions become a config flag (default off), so all clients follow the same rules.
- Audit the suspected single-player assumptions (1.3 B). Each finding is either fixed,
  configured, or documented as "per-player" behaviour.
- Account creation policy and rate limiting on login.
- **Exit:** two humans on two machines play together with bots for a session. Companion bots
  belong to the right player, and nobody's link-death or timeout behaviour differs.

### Phase 4: Bot boundary, restart-to-swap (large; most of the effort)

- Coupling inventory (4.4 step 1). Introduce `Bots.Abstractions`, `Bots.Runtime`, `IBotHooks`.
- Move policies, then controllers, into `Bots.Behavior`. Keep behaviour identical, verified by
  the existing bot tests and side-by-side in-game sessions.
- `Server.Core` no longer references `Bots.Behavior`. The server loads it by path at startup.
- JSON tuning hot-reload (cheap, do it early in this phase).
- **Exit:** changing a bot policy means rebuilding one project and restarting. Core is not
  rebuilt. Category (b) in the coupling CSV is zero.

### Phase 5: Hot reload + fault containment (medium)

- Collectible ALC, `bots reload`, `BotMemory`, safe decider, circuit breaker, decision trace.
- **Exit:** 100 reload cycles under load with no growth in loaded assemblies or memory;
  an injected exception disables one module, not the server.

### Phase 6: Terminal UI (small to medium)

- `daoc-tui` on `Terminal.Gui` (MIT, .NET, mature). Panels: status, accounts/roles,
  bots/population, settings, log tail, breaker status. Every panel is a view over CLI ops, with
  no logic of its own.

### Phase 7: Packaging (small)

- Release = `bin/` tarball + `world-import` + `deploy/` scripts + docs. Neither the client nor
  a world DB is redistributed; `world-import` pulls the world from the official release.
- Upgrade script: back up `data/`, replace `bin/`, run migrations.

Phases 2 and 4 can overlap once Phase 1 is done, because they touch different code. Phase 4 is
where the real investment is. Don't start it before Phase 0 gives you a Linux build/test loop,
or you'll be refactoring blind.

---

## 7. Risks and blockers

| Risk | Likelihood | Handling |
|---|---|---|
| Native libs (Detour, SQLite interop) don't load on Linux | Medium | Phase 1 spike first; CMake build is upstream's own path |
| Case-sensitive paths break data/script/navmesh loading | High | Expect it. Fix in code, not with symlinks. The install script's `ci_resolve` proves it exists |
| Navmeshes or world DB differ between 0.33 and 0.33b | Known | `world-import --edition`; reuse the hash-verified edition file list from the 0.33 manifest |
| Hot reload leaks (statics, events, timers) | High | Phase 4 moves state to `BotMemory` first; reload stress test is the exit gate; restart-to-swap remains the fallback |
| Bot behaviour silently changes during extraction | High | Move code verbatim first, refactor later; existing tests plus recorded in-game sessions per moved family |
| Single-player assumptions deeper than found | Medium | Phase 3 audit; two-human session is the exit gate |
| Game loop threading: plugin code called from parallel ECS services | Medium | Plugin contract states the threading rules; runtime calls deciders only from the brain tick |
| Exposed server gets abused | Medium if port-forwarded | Auto-create off by default; no TCP admin surface; owner accepts port-forward exposure |
| Upstream OpenDAoC fixes wanted later | Certain | Section 9 policy |
| Performance on modest hosts (Deck-class) with many bots | Medium | Existing `AutonomousAiBudget`/fidelity policy; measure in Phase 1 |
| World data redistribution | Unclear licensing | Never redistribute; import from the official package |
| Tests that assume Windows | Medium | Listed in Phase 0, fixed or quarantined with reason |

---

## 8. Keep / extract / remove

**Keep as-is (move verbatim):**
- the OpenDAoC fork core
- combat, spells, styles, properties
- packets and the client compatibility layer
- `Account.PrivLevel` and in-game GM commands
- `ENABLE_SLUAGHBINDER` (server-side rules only)
- navmesh builder
- the existing tests
- the owner's gameplay rules: real loot/coins/upgrades, Realm Exchange, stablemaster routes,
  selling at task breaks, bots start without armour, Darkness Falls open to solo bots

**Extract (behind boundaries):**
- bot decision logic into `Bots.Behavior`
- bot lifecycle into `Bots.Runtime`
- launcher DB operations into `Server.Admin` services
- `offline_*` schema into `Server.Data` migrations
- the progress-wipe policy into `world-import`
- bot tuning into `config/bots/`

**Remove from the new project:**
- WinForms launcher and its tests and layout checker
- `OfflineDaoc.Setup`, `OfflineDaoc.ProgressImport`
- `Get-OfflineDAoC.ps1`, all `.cmd` files
- `account.txt` / `PortableCredentials`, `offline_local_options.MakeMeGM`
- `LocalPlayerTravelInput`
- the Windows service installer actions in `CoreServer`
- client art and patch tooling (`pet-art`, `asset-tool`, MPK, `inspect_*`, observer patch,
  helmet fix, client raid builder)
- release seal/assemble scripts tied to the bundled client
- upstream `Dockerfile`/`entrypoint.sh`

**Keep elsewhere:** the old repo and its releases (0.3–0.33b) stay untouched as legacy.

---

## 9. Upstream OpenDAoC policy

**Static snapshot, optional manual pull.** Measured divergence is roughly 722 core files and
~50k changed lines against current upstream, before counting the 49k lines of bot code.
Continuous merging would cost more than the fixes it brings.

Modularity goal: the admin front-end (socket protocol) and the bot AI module (`Bots.Abstractions`)
must not depend on which server build is behind them. The server side of each is kept small and
identifiable: the admin endpoint module, plus a **bot host adapter** that implements
`Bots.Abstractions` on OpenDAoC internals. Rebuilding from a fresh upstream therefore means
re-applying the gameplay fixes and that adapter, and expecting some adapter repair per pull.
That is why a pull is a deliberate, occasional step, not part of every build.

Policy:

- Record the base commit in `UPSTREAM.md`.
- Watch upstream for security fixes and specific bug fixes you want; port them by hand as
  individual commits, citing the upstream commit.
- Re-evaluate only if a future goal (e.g. a newer client version) needs a large upstream
  feature. That would be its own project.

---

## 10. Recommendation: partial extraction, not rewrite

- **Cleaner rewrite: no.** The value of this project is the 49k lines of tuned bot behaviour
  and the 722 files of gameplay fixes. A rewrite throws that away and spends a year getting
  back to today. OpenDAoC's protocol and world model would still constrain you anyway.
- **Pure in-place refactor: no.** The old tree carries a Windows packaging pipeline, client
  tooling and a launcher woven into the data model; cleaning it in place means fighting that
  every day.
- **Partial extraction into a new tree: yes.** Copy the server verbatim into `project/`, drop
  everything that isn't server, then extract boundaries one at a time (admin plane, then bot
  layer) with the server working at the end of every phase.

The honest effort profile: Phases 0–2 are well-understood engineering, and they give you a
Linux server you can administer by command. Phase 4 is the long one, because it's the only
phase that changes how 49k lines are wired. The payoff is real but gradual. The first policies
become independently editable early; the big controllers come last.

---

## 11. Assumptions to verify

| Assumption | How to verify |
|---|---|
| Server runs on Linux .NET with no Windows-only code paths beyond the guarded ones found | Phase 0/1 build and boot |
| `System.Data.SQLite.Core` 1.0.119 native loads in the chosen distrobox image | Phase 1 boot |
| 0.33 and 0.33b share navmeshes; edition differences are DB + `game.dll` only | Compare package manifests / edition file list |
| Non-Windows `/travel` fallback is acceptable gameplay | In-game check, Phase 3 |
| Remote clients connect with only `RegionIP` set correctly | Two-machine test, Phase 1 |
| The upstream fork point is recoverable | Bisect upstream history in Phase 0 |
| DAoC login/auth path is not safe for hostile internet exposure | Read `LoginRequestHandler` + packet encryption before anyone port-forwards |
