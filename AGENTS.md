# DAoC server: start here (AI assistants and developers)

This file is loaded by Claude Code as `CLAUDE.md`. Read it fully before changing anything, then the
doc for your task:

| Task | Read |
|---|---|
| Anything about bots | [docs/BOTS-DEV.md](docs/BOTS-DEV.md): kinds, lifecycle, code map, dev loop, plan |
| How the system fits together | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Building and testing | [docs/BUILD.md](docs/BUILD.md) |
| Running, admin, upgrades, troubleshooting | [docs/RUN.md](docs/RUN.md) |
| Releases, navmesh set, world data, DB pin | [docs/RELEASING.md](docs/RELEASING.md) |
| Upstream pins | [UPSTREAM.md](UPSTREAM.md) |
| Why things are the way they are | [docs/history/](docs/history/) |

## What this is

A Linux-first **Dark Age of Camelot server** (OpenDAoC fork, .NET 10, SQLite) with
**autonomous gamebots** and **companion bots**. It's released as one portable folder with a
built-in Classic + Shrouded Isles world; navmeshes ship as a separate archive. The game client is
not part of the project. The owner's goal now is to **develop the bots**: smarter, natural
behaviour, dungeons, battlegrounds, keeps and frontier, and later chat.

## Which folder is which (on the owner's Steam Deck)

| Folder | Status |
|---|---|
| `~/Dev/vscode/OfflineDAoC-main/next/` | **This repo (a real folder). All work happens here.** |
| `~/Dev/vscode/OfflineDAoC-main/project/` | Link to `~/boxes/daoc-server-box-home/daoc-server-src`: the previous Linux build. **Frozen reference: never modify.** Read it for data or logic if needed. |
| `~/boxes/daoc-server-box-home/` | The build box's home: `.dotnet/` (SDK) and test server folders only, no source. |
| `~/boxes/daoc-server-box-home/next-test/daoc-server/` | Dev/test server folder: unpacked release, navmeshes installed, 9 test bots. `tools/dev-deploy.sh` targets it (inside the box: `~/next-test/daoc-server`). |
| `~/boxes/daoc-server-box-home/daoc-server/` | An older test server folder from `project/`; its `navmesh/` is a source copy of the navmesh set. |

The box sees `/home/deck` directly, so build the repo in place: inside the box, `cd /home/deck/Dev/vscode/OfflineDAoC-main/next`.
Inside the box, `~` is the box home, not `/home/deck`.
| `~/Dev/vscode/OfflineDAoC-main/source/` | The original Offline DAoC 0.33 Windows source (read-only reference, legacy) |
| `~/boxes/daoc-offlineserver-box/OfflineDAoC/playable-v0.33/` | The owner's 0.33 install (Wine client + pristine 0.33 world DB under `editions/`). Read only. |

`rewrite/` was an abandoned experiment and is deleted; see
`docs/history/2026-10-01-rewrite-playerbot-findings.md`.

## Environment and how to run things

- **Host:** SteamOS (read-only root). **Build box:** distrobox `daoc-server-box` (Ubuntu 22.04),
  home `~/boxes/daoc-server-box-home`, .NET SDK at `~/.dotnet/dotnet` inside it.
- **From an editor sandbox (VS Code flatpak)**, host commands need `flatpak-spawn --host ...`, and
  box commands
  `flatpak-spawn --host distrobox enter daoc-server-box -- bash -lc '...'`. The sandbox `/tmp` is
  private; put shared files under the box home.
- **Common commands** (inside the box, from the repo root):
  - Build and test: `cd src && ~/.dotnet/dotnet build DaocServer.sln -c Release && ~/.dotnet/dotnet test Tests/Tests.csproj -c Release --no-build`
  - Full release: `tools/build-release.sh`. It takes about 2 minutes and creates `dist/`.
  - Quick bot iteration: `tools/dev-deploy.sh ~/next-test/daoc-server` (about 15 s).
  - Run the server: `~/next-test/daoc-server/daoc-server.sh start` (or `start --console`).
  - Admin: `~/next-test/daoc-server/daoc-admin bots list` (and so on).
- **Ports:** game TCP 10300, UDP 10400. Only one server at a time. Check with
  `ss -ltnp | grep 10300` (on the host) before starting.

## Gotchas

- **CRLF:** many source files use CRLF line endings. Preserve them when editing (check with
  `file`). Don't rewrite whole files with a different ending.
- **The build needs `src/CoreServer/config/serverconfig.xml`** to exist (it's gitignored). Copy it
  from `serverconfig.example.xml`; the scripts do this.
- **Server root is `bin/`:** `bin/config`, `bin/navmesh` and `bin/logs` are symlinks to the
  folder's own directories.
- **Bots are NPCs pretending to be players** (`GameBot : GameNPC, IGamePlayer`). Core files
  special-case them, so bot changes can touch `GameNPC`, `SpellHandler` and other core files.
- **Navmeshes are in the source** as `world/navmesh/*.part-*` (four ~90 MB parts, under GitHub's
  100 MB limit). `build-release.sh` joins, verifies and installs them into `dist/daoc-server`.
  Rebuilding replaces `dist/`; play on a copy elsewhere.
- **`world/patches/200-*.sql` are generated.** Never hand-edit them; add `300-*` files instead
  (docs/RELEASING.md).
- **Don't trust "it compiles":** check bot behaviour on the dev server (TUI Bots tab, `/tele`,
  `logs/server.log`).
- **`/travel` doesn't work on Linux** (it drives the Windows client's keyboard). `&tc` is
  registered twice. Both are known issues (docs/ARCHITECTURE.md §8).

## Rules

- **Protect player data.** Never commit a played database, logs, backups, credentials,
  `serverconfig.xml` or `admins.json`. `.gitignore` is a safety net only.
- **Don't touch `project/`** (`daoc-server-src`). It's the frozen reference.
- **Keep the owner's gameplay rules:**
  - real loot, inventories, coins and equipment upgrades;
  - the Realm Exchange;
  - stablemaster routes;
  - selling only at natural task breaks;
  - gamebots start without armor and earn it;
  - Darkness Falls is open to solo bots.
- **Tell bot types apart:** real players, companion bots and autonomous gamebots are different.
- **No unbounded loops in bot code.** Every loop, retry and search has an explicit cap.
- **No client assumptions in `src/`:** no `game.dll`, client paths or input injection (the
  `/travel` legacy is a known exception to remove).
- **Don't start a server, or deploy over a running one, without the owner's OK.** The owner often
  has a server running on 10300.
- **Be honest in reports:** automated tests and in-game checks are separate claims. Don't
  describe Darkness Falls raid AI, Legion or the hardest level 70+ content as implemented.
- **Upstream is a static snapshot:** port fixes by hand and cite the upstream commit.
- **Communication:** the owner prefers short, direct answers and is careful with token cost. Do
  the work, report briefly, and don't start large explorations without saying why.
