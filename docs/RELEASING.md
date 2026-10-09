# Releasing and maintaining the data

## Publishing a release on GitHub

1. Build in the box: `tools/setup-build-box.sh --build` (or `tools/build-release.sh` inside it).
2. Tag first if you want a real version name: `git tag v0.34 && tools/build-release.sh`. The
   version comes from `git describe`.
3. Create a GitHub release and attach:
   - `dist/daoc-server-<version>-linux-x64.tar.gz` (server and world, ~85 MB);
   - `dist/daoc-navmeshes-classic-si-2.tar.xz` and its `.sha256` (~360 MB), for people who use the
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
source carries: each is under GitHub's 100 MB file limit, so plain git works without LFS. The
current set is the 103 meshes plus `seams.json` and `pockets.json` (bot zone-border and
walled-pocket data, read by the server) shipped with Offline DAoC 0.35 (see `UPSTREAM.md`).

**A new navmesh set** (for example, regenerated with `tools/navmesh`):
1. Bump the set name `classic-si-2` in `tools/package-navmeshes.sh` and
   `deploy/navmesh-install.sh`.
2. Regenerate the manifest:
   `(cd <folder> && sha256sum zone*.nav seams.json pockets.json | LC_ALL=C sort -k2) > world/navmesh-manifest.sha256`
   (keep a `#` comment header naming the set and its source)
3. Re-package and publish.
4. Retest the bots: their route data (`bots/autonomous/data/*.json`) was tuned on the current set.

## The world: upstream database pin and the Offline DAoC delta

- `tools/fetch-upstream-db.sh` pins OpenDAoC-Database (`DB_COMMIT`) and a content hash over its
  `.sql` files (`DB_CONTENT_SHA256`, independent of GitHub's archive compression).
- `world/patches/200-*.sql` are **generated**. Don't edit them by hand. They turn that upstream
  dump into the pristine world of the Offline DAoC release we track (0.35, plain edition).
- `world/patches/300-*.sql` are **hand-written** changes on top (for example
  `300-bot-login-speed.sql`). Add new world changes here, with a comment explaining why.

**Moving the upstream DB pin, or moving to a new Offline DAoC release** (rare and deliberate; for
a new release, skip step 1):
1. Set `DB_COMMIT` in `fetch-upstream-db.sh`, delete `.cache/`, move the old
   `world/upstream-db/*.tar.xz` away (otherwise it is used), run the script once (it downloads),
   and set `DB_CONTENT_SHA256` to the hash it prints in the mismatch message. Then bundle the new
   dump:
   `tar -C .cache/opendaoc-db-<commit> -cf - opendaoc-db-core | xz -9 > world/upstream-db/opendaoc-db-<commit>.tar.xz`
2. Build the plain upstream world (no patches):
   `dotnet src/WorldBuilder/bin/Release/net10.0/DaocWorldBuilder.dll --source <dump> --out /tmp/upstream.sqlite`
3. Regenerate the delta. You need the release's pristine plain-edition world DB:
   `editions/<version>-no-custom-class/runtime/data/opendaoc.sqlite3.db` inside the "b" release zip
   (for 0.35: SHA-256 `feb4a511…`, also listed in the plain release's `download-manifest.json`).
   Set `WORLD` at the top of the script to the release, then
   `python3 tools/world/derive-world-delta.py <pristine db> /tmp/upstream.sqlite world/patches`
   (it removes nothing itself: delete the old `200-*` files first). Check the script's `EXCLUDED`
   list against the release's `progress-policy.json` (in its `OfflineDaoc.ProgressImport` source).
4. Verify: build with only the `200-*` patches (`--patches <folder with copies of them>`), then
   run the same script against the result. It must print `0 tables differ`.
5. Update `UPSTREAM.md`.

## Upgrading OpenDAoC itself

This code base is a heavily modified fork (about 720 upstream files changed). Upgrading means
merging. It's a project of its own, not a routine step; see `UPSTREAM.md`. Port individual
upstream fixes by hand and cite the upstream commit.
