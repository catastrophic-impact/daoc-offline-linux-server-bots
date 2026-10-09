# Building

The build produces a portable server folder. It runs on x86-64 Linux with glibc 2.34+ and libicu,
with or without distrobox, and needs no .NET install. To run it, see [RUN.md](RUN.md); to
publish it, see [RELEASING.md](RELEASING.md).

```
dist/daoc-server/                              portable server folder, ready to start (world and navmeshes installed)
dist/daoc-server-<version>-linux-x64.tar.gz    the folder without navmeshes, as one archive (~85 MB)
dist/daoc-navmeshes-classic-si-2.tar.xz        the navmeshes (~360 MB), joined from world/navmesh/ parts
```

No client files are included.

## Option A: inside a distrobox (recommended; use it for releases)

On the host, from the repository root:

```bash
tools/setup-build-box.sh --build
```

This does three things, and is safe to re-run:
1. Creates the `daoc-server-box` distrobox (Ubuntu 22.04) with its own home
   (`~/boxes/daoc-server-box-home`; change with `BOX_NAME` and `BOX_HOME`).
2. Installs the .NET 10 SDK, cmake and g++ inside it.
3. Builds.

Without `--build`, it only prepares the box. Building on Ubuntu 22.04 keeps the native pathing
library compatible with glibc 2.34+ systems.

## Option B: on your own system

Requirements:
- the .NET 10 SDK (`dotnet` on PATH, `~/.dotnet/dotnet`, or `DOTNET=...`);
- `cmake`, `g++` and `curl`;
- internet access on the first build.

Then run `tools/build-release.sh`. The result needs your system's glibc version or newer.

## What `tools/build-release.sh` does

1. `dotnet publish` of `CoreServer` (the server) and `DaocAdmin` (the TUI/CLI), self-contained for
   linux-x64, into `dist/daoc-server/bin/`.
2. Builds `src/Pathing/Detour` with CMake (static libstdc++) into `bin/lib/Detour.so`.
3. **World:**
   - `tools/fetch-upstream-db.sh` unpacks the pinned OpenDAoC-Database bundled in
     `world/upstream-db/` (9.6 MB; it downloads only if the bundle is missing) into `.cache/` and
     verifies its content hash;
   - `src/WorldBuilder` creates the schema from the server's own DataObjects, loads the dump and
     applies `world/patches/*.sql`;
   - the result is `defaults/world.sqlite`.
4. Copies translations, default config, scripts (`daoc-server.sh`, `daoc-admin`,
   `navmesh-install.sh`, `release.conf`) and the README into the folder.
5. **Navmeshes:** joins `world/navmesh/*.part-*` into `dist/daoc-navmeshes-classic-si-2.tar.xz`,
   verifies it against the committed `.sha256`, and installs it into `dist/daoc-server/navmesh`.
6. Writes `VERSION` (`git describe`, or `dev` without git) and the tarball, which excludes
   `navmesh/`.

It takes about 2.5 minutes. For bot and server code changes, use the 15-second
`tools/dev-deploy.sh` instead (see [BOTS-DEV.md](BOTS-DEV.md)).

## Build and test only

```bash
cd src
cp -n CoreServer/config/serverconfig.example.xml CoreServer/config/serverconfig.xml  # local, gitignored
dotnet build DaocServer.sln -c Release
dotnet test Tests/Tests.csproj -c Release --no-build
```

Inside the box use `~/.dotnet/dotnet`. Build outputs go to `src/Release/` (server DLLs in
`src/Release/lib/`) and `src/build/`. 47 tests that need an installed world are skipped unless
`OFFLINE_DAOC_NAV_ROOT`, `OFFLINE_DAOC_DB_PATH` and `OFFLINE_DAOC_TEST_DETOUR` are set.

## Verified (2026-10-01, Steam Deck, Ubuntu 22.04 box)

These are automated and log checks. In game, the owner has used the predecessor build
(`project/`): login and `/spawn` worked.

| Check | Result |
|---|---|
| Build | 0 errors (~650 pre-existing warnings) |
| Tests | 2,754 passed, 0 failed (0.35 sync, 2026-10-09) |
| World build | ~95 s, 68 MB; identical to Offline DAoC 0.35's pristine world (0 differing tables) |
| Fresh release start | Ready in ~21 s; 99/99 navmeshes (measured at 0.33; not re-measured for 0.35's 103) |
| Bot logins | 9 new bots in the world in about 1 s; full AI active within 30 s; 0 errors |
| Stop | Saves and exits in ~2 s |
| Dev loop | `tools/dev-deploy.sh` ~16 s; the server boots afterwards with bots intact |
