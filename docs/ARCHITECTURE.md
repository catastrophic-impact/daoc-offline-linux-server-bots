# Architecture

How the system works today. For the reasoning that led here, see
[history/2026-10-01-original-proposal.md](history/2026-10-01-original-proposal.md).

## 1. What this is

A Dark Age of Camelot server for Linux, built on an OpenDAoC fork. It hosts players from any
machine together with two kinds of bots, and ships as **one portable folder**: no .NET install,
no Wine, no Docker. The game client is not part of the project; players install it separately.

- **World:** Classic + Shrouded Isles (no Trials of Atlantis), the world of Offline DAoC 0.33.
- **Classes:** the classic Classic + SI classes. The custom Sluaghbinder class exists in the code
  but is switched off (`classes / enable_sluaghbinder = False`), and it needs a patched client.
- **Bots:** autonomous gamebots that live in the world, and companion bots that players summon.
  See [BOTS-DEV.md](BOTS-DEV.md).

## 2. Lineage

| Stage | What it was |
|---|---|
| **OpenDAoC** | Upstream emulator (ECS rewrite of DOLSharp). The exact fork point is unknown (no git history came with 0.33). |
| **Offline DAoC 0.33** (`source/` in the parent folder) | Windows single-player bundle with a WinForms launcher. It added the bots, the Realm Exchange, the Classic/SI world conversion and many gameplay fixes, changing about 720 upstream files. |
| **`project/`** (`daoc-server-src`) | Linux conversion: portable release, admin socket with TUI/CLI, launcher removed, Linux fixes. It still imported the world from the 0.33 release. **Frozen reference: do not edit.** |
| **`rewrite/`** (deleted) | An attempt to rebuild on unmodified upstream with bots as real `GamePlayer`s. It worked technically, but reaching parity meant rebuilding everything, so it was abandoned. Its world builder and navmesh packaging live on here. |
| **This repo** (`next/` in the workspace) | `project/` plus: the world built from pinned upstream data (no 0.33 download), navmesh release packaging, fast bot logins, a dev loop. **This is where work happens.** |

Upgrading OpenDAoC is possible but means merging into a heavily modified fork. It's a deliberate
project, not routine. See [UPSTREAM.md](../UPSTREAM.md).

## 3. The pieces

```
 build time (build box)                                 run time (portable folder)
 ──────────────────────                                 ──────────────────────────
 src/*  ──dotnet publish──►  bin/ (self-contained)      daoc-server.sh ─► bin/CoreServer ─┐
 Pathing/Detour ──cmake──►  bin/lib/Detour.so                                            │
 OpenDAoC-Database (pinned)                             data/opendaoc.sqlite3.db ◄────────┤ world + saves
   + world/patches ──WorldBuilder──► defaults/world.sqlite ─(first start)─► data/        │
 navmeshes ──package-navmeshes──► daoc-navmeshes-*.tar.xz ─navmesh-install─► navmesh/ ◄──┤ pathing
                                                         config/ (serverconfig.xml, admins.json)
                                                         run/admin.sock ◄── daoc-admin (TUI / CLI)
```

| Project (`src/`) | Role |
|---|---|
| `CoreServer` | Host program (`CoreServer`): starts the game server, reads console commands, and handles SIGINT/SIGTERM/`exit` with a clean save. Linux-fixed in `project/`: no busy loop without stdin, and it exits on a failed start. |
| `GameServer` | The game: OpenDAoC fork plus 0.33's changes. Contains `bots/` (all bot code), `admin/` (admin service) and `scripts/` (commands, NPCs, quests). |
| `CoreBase`, `CoreDatabase` | Shared libraries and the database layer (SQLite in practice; MySQL also supported). |
| `Admin.Protocol` | Admin request/response types, command grammar and text output, shared by the server and the tools. |
| `DaocAdmin` | `daoc-admin`: terminal GUI (Terminal.Gui 1.19, pinned) without arguments, CLI with arguments. |
| `WorldBuilder` | Builds the world database from the upstream dump plus `world/patches`. |
| `Pathing/Detour` | Native navmesh library (C++, CMake), loaded as `bin/lib/Detour.so`. |
| `Tests` | NUnit, about 2,300 tests (about 60 bot-specific files). |

## 4. Runtime

- **Start modes** (`deploy/daoc-server.sh`):
  - `start` runs the server in the background (its own session) and opens the admin TUI in the
    same terminal.
  - `start --console` runs it in the foreground; this is also used automatically without a
    terminal.
  - `stop` and `status` do what they say.
- **Stopping always saves:** the TUI's Stop button, `daoc-admin server stop`, typing `exit`,
  Ctrl+C and SIGTERM all go through the same path.
- **Admin plane:**
  - `GameServer/admin/AdminSocketHost` listens on `run/admin.sock` (mode 0600, no network port).
  - `AdminService` implements every operation: status, accounts and roles, bots, population,
    stop.
  - The server console accepts the same commands.
  - `config/admins.json` is server-owned (GM/admin names), applied at startup and on account
    creation.
- **Ports:** TCP 10300 (game) and UDP 10400. `RegionIP` in `config/serverconfig.xml` is the
  address clients are told to use.
- **Paths:** the server's root directory is `bin/`. `bin/config`, `bin/navmesh` and `bin/logs` are
  symlinks to the folder's own directories, and the database path is relative (`../data/...`), so
  the folder can be moved.

## 5. Data

| Data | Where | Notes |
|---|---|---|
| World and all saves | `data/opendaoc.sqlite3.db` | Created on first start from `defaults/world.sqlite`. Back it up before upgrades. |
| Server settings | `ServerProperty` table | In game: `/serverproperty`. Bot settings are in category `autonomous_population`. |
| Config | `config/serverconfig.xml`, `logconfig.xml`, `invalidnames.txt`, `admins.json` | Created from `defaults/`; never overwritten by upgrades. |
| Bot tuning | `bin/bot-goals.json` (optional; defaults built in) | Goal mix per level band; read at startup. |
| Navmeshes | `navmesh/zone*.nav` (99 files, about 2 GB) | Set `classic-si-2`; manifest `world/navmesh-manifest.sha256`. |

**World pipeline** (`tools/build-release.sh`):
1. `fetch-upstream-db.sh` downloads OpenDAoC-Database at the pinned commit and verifies a content
   hash.
2. `WorldBuilder` creates every table from the server's own DataObjects and loads the dump's
   rows.
3. `world/patches/*.sql` run in name order:
   - `200-*` is the generated 0.33 delta, which reproduces 0.33's pristine world exactly;
   - `300-*` are hand-written changes (for example, faster bot logins).

## 6. Bots (summary)

The full guide is [BOTS-DEV.md](BOTS-DEV.md).
- **Body:** `GameBot`, a `GameNPC` that implements `IGamePlayer`. It's an NPC made to behave like a
  player; that's why bot changes often touch core code.
- **Brain:** `BotBrain` (think loop, combat) plus about 100 policy classes under `bots/` and
  `bots/autonomous/`.
- **Autonomous bots:**
  - The roster lives in `offline_world_bots`.
  - `AutonomousPopulationController` logs them in at `bot_logins_per_second`.
  - `AutonomousWorldBotController` and the goal policies decide what they do.
  - Their state is persisted per bot.
- **Companions:** temporary bots created by `/spawn` that join the player's group.

## 7. Decisions that stand

- Linux-first portable folder; distrobox only for building (and on hosts that lack libicu).
- No Docker or Podman. No init-system integration; the server is a plain foreground process.
- CLI first, TUI on top, no web UI. The admin socket is local only; remote admin goes over ssh.
- Home network first; internet play is via router port forwarding (TCP 10300, UDP 10400).
- Classic + SI only. The world is built from upstream data, with no 0.33 download.
- Navmeshes ship in releases (default) and can be generated from a client (optional).
- Bot AI is to become a separate, reloadable module, extracted step by step from the working code
  ([BOTS-DEV.md](BOTS-DEV.md), last section).

## 8. Known issues

- **`/travel`** drives the local Windows game client's keyboard, so it does nothing on Linux or
  for remote players.
- **`&tc`** is registered twice: Realm Exchange teleport and the transfer-corpse script. Startup
  logs a duplicate-key error.
- **Upstream data gaps:** missing NPC templates and spells are logged at startup, inherited from
  0.33 and upstream data.
- **Detour** ignores `fread` results, so a truncated navmesh file isn't detected.
- **libicu is required:** the server hard-codes the `en-US` culture
  (`CoreDatabase/ObjectDatabase.cs`).
- **The navmesh generator** (`tools/navmesh`) is Windows-only, and the Wine automation for
  `navmesh-install.sh --build-from-client` doesn't exist yet.
