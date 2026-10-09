# Releasing and maintaining the data

## Publishing a release on GitHub

1. Build in the box: `tools/setup-build-box.sh --build` (or `tools/build-release.sh` inside it).
2. Tag first if you want a real version name: `git tag v0.34 && tools/build-release.sh`. The
   version comes from `git describe`.
3. Create a GitHub release and attach:
   - `dist/daoc-server-<version>-linux-x64.tar.gz` (server and world, ~85 MB);
   - `dist/daoc-navmeshes-classic-si-2.tar.xz` and its `.sha256` (~340 MB), for people who use the
     release instead of building from source. Upload it once per navmesh set: attach it to every
     release, or keep a dedicated `navmeshes-classic-si-2` release and link to it.

   People building from source don't need this; the navmeshes are in the source
   (`world/navmesh/`).
4. Put the navmesh download URL in `deploy/release.conf`
   (`NAVMESH_URL=https://github.com/catastrophic-impact/daoc-offline-linux-server-bots/releases/download/<tag>/daoc-navmeshes-classic-si-2.tar.xz`),
   commit, and rebuild. `./navmesh-install.sh` with no arguments then downloads it automatically
   when the archive isn't next to the folder.

Never attach or commit a played database, logs, `serverconfig.xml` or `admins.json`.
`.gitignore` covers `dist/`, `.cache/`, `*.db`, `*.sqlite`, `*.nav` and logs.

## The navmesh archive

```bash
tools/package-navmeshes.sh <folder with zone*.nav>   # verifies against world/navmesh-manifest.sha256
```

It writes `dist/daoc-navmeshes-classic-si-2.tar.xz` and a `.sha256` (xz, about 4 minutes on a
Steam Deck), plus the same archive as ~90 MB parts in `world/navmesh/`. Those parts are what the
source carries: each is under GitHub's 100 MB file limit, so plain git works without LFS. The current set is the 99 meshes shipped with Offline DAoC 0.33. A source copy on
this machine is `~/boxes/daoc-server-box-home/daoc-server/navmesh`.

**A new navmesh set** (for example, regenerated with `tools/navmesh`):
1. Bump the set name `classic-si-2` in `tools/package-navmeshes.sh` and
   `deploy/navmesh-install.sh`.
2. Regenerate the manifest:
   `(cd <folder> && sha256sum zone*.nav | sort -k2) > world/navmesh-manifest.sha256`
3. Re-package and publish.
4. Retest the bots: their route data (`bots/autonomous/data/*.json`) was tuned on the current set.

## The world: upstream database pin and the 0.33 delta

- `tools/fetch-upstream-db.sh` pins OpenDAoC-Database (`DB_COMMIT`) and a content hash over its
  `.sql` files (`DB_CONTENT_SHA256`, independent of GitHub's archive compression).
- `world/patches/200-*.sql` are **generated**. Don't edit them by hand. They turn that upstream
  dump into Offline DAoC 0.33's pristine world.
- `world/patches/300-*.sql` are **hand-written** changes on top (for example
  `300-bot-login-speed.sql`). Add new world changes here, with a comment explaining why.

**Moving the upstream DB pin** (rare and deliberate):
1. Set `DB_COMMIT` in `fetch-upstream-db.sh`, delete `.cache/`, move the old
   `world/upstream-db/*.tar.xz` away (otherwise it is used), run the script once (it downloads),
   and set `DB_CONTENT_SHA256` to the hash it prints in the mismatch message. Then bundle the new
   dump:
   `tar -C .cache/opendaoc-db-<commit> -cf - opendaoc-db-core | xz -9 > world/upstream-db/opendaoc-db-<commit>.tar.xz`
2. Build the plain upstream world (no patches):
   `dotnet src/WorldBuilder/bin/Release/net10.0/DaocWorldBuilder.dll --source <dump> --out /tmp/upstream.sqlite`
3. Regenerate the delta. You need the pristine 0.33 world DB:
   `editions/0.33-no-custom-class/runtime/data/opendaoc.sqlite3.db` in the 0.33 release, SHA-256
   `1588118319fa3243…`. On this machine it's under
   `~/boxes/daoc-offlineserver-box/OfflineDAoC/playable-v0.33/`.
   `python3 tools/world/derive-033-delta.py <pristine 0.33 db> /tmp/upstream.sqlite world/patches`
   (it removes nothing itself: delete the old `200-*` files first).
4. Verify: build with patches, then run the same script against the result. It must print
   `0 tables differ`.
5. Update `UPSTREAM.md`.

## Upgrading OpenDAoC itself

This code base is a heavily modified fork (about 720 upstream files changed). Upgrading means
merging. It's a project of its own, not a routine step; see `UPSTREAM.md`. Port individual
upstream fixes by hand and cite the upstream commit.
