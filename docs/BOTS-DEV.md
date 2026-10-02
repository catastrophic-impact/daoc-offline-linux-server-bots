# Working on the bots

Everything needed to change how bots behave: what kinds exist, how they live, where the code is,
how to iterate quickly, and where the design is going. All bot code is in
`src/GameServer/bots/` (C#).

## 1. Three kinds of characters (never mix them up)

| Kind | What it is | Created by | Lives |
|---|---|---|---|
| **Real players** | Humans on a game client | Account login | As long as they play |
| **Companion bots** | Temporary helpers in a player's group | `/spawn <class>` (or `/raid`) by a player | Until the owner disbands them or logs out. Disposable, not saved as characters |
| **Autonomous bots** (gamebots) | Persistent characters that live in the world on their own: level, hunt, travel, group, RvR, trade | Admin: TUI **Bots** tab or `daoc-admin bots create` | Saved in the roster; logged in whenever the population is on |

Many policies serve both bot kinds, but the rules differ (companions follow the owner;
autonomous bots pursue goals). When you change shared code, check both.

## 2. How a bot works

**Body:** `GameBot.cs`, a `GameNPC` that implements `IGamePlayer`. It has a class, race,
specializations (`specs/`), real equipment and inventory (`BotEquipment`, `BotInventory`) and
attribute progression. Because it's an NPC acting as a player, the core treats it as a player in
many places. That's why some bot behaviour lives in core files such as `GameNPC`, `SpellHandler`
and the property calculators.

**Brain:** `BotBrain.cs` (plus the `BotBrain.*.cs` partials). The think loop runs every
`ThinkInterval`, which is shorter in combat and adapted by `AutonomousFidelityPolicy`. It picks
targets, spells, styles and movement, delegating to the policy classes.

**Autonomous bot lifecycle:**
1. **Created:** `admin/BotRosterAdmin.Create` writes a row in **`offline_world_bots`** (identity
   from `AutonomousBotIdentityGenerator`). Level-50 bots also get gear from
   **`offline_level50_loadouts`**.
2. **Logged in:** `AutonomousPopulationController` polls once per second. With the population on,
   it loads up to `bot_logins_per_second` roster bots, builds a `GameBot` from the record and its
   saved inventory (owner ID `offlinebot:<BotId>`), and places it at its saved position, or at a
   validated starting location if it's new.
3. **Lives:** `AutonomousWorldBotController` (split into `*.Expedition`, `*.Siege`, `*.Rally` and
   more) plus the goal and decision policies pick objectives: hunt a camp, travel by stable
   horse, run a dungeon, join a raid, defend a keep. `AutonomousStuckWatchdog` recovers stuck
   bots.
4. **Persisted:** `AutonomousBotStatusPersistence` saves position, progress and state, batched.
   Inventory saves when it changes.
5. **Logged out:** when the population goes off, `max_active_bots` drops, or the server stops.
   The character stays.
6. **Deleted:** `bots delete` marks the record retired and queues an **`offline_bot_commands`**
   `Delete`. The game loop removes the bot from the world, then deletes its data.

**Companion lifecycle:** `/spawn [class]` (`commands/playercommands/TemporaryGroupCommands.cs`)
creates a `GameBot` with `IsTemporaryGroupHelper` and an `Owner`, levelled to match the owner, and
adds it to the owner's group. Engagement modes (`/aggressive`, `/defensive`), `/pull`, `/grind`
and `/raid` drive it. It's removed when disbanded or when the owner quits.

## 3. In-game commands (bot-related)

| Command | Who | What |
|---|---|---|
| `/spawn [class]` | Player | Companion class menu, or create a companion of that class |
| `/classes` | Player | Your realm's Classic + SI classes and their party roles |
| `/aggressive`, `/defensive` | Player | Companions assist you (default), or wait for enemies to approach |
| `/pull` | Player | Party bots and their pets engage your target |
| `/grind`, `/grind stop` | Player | Stationary automatic pulls with your companions |
| `/raid 40`, `/raid 80` | Player | A level-50 companion raid (total includes you) |
| `/tele <bot name>`, `/tele mob <monster>` | Player | Teleport to an autonomous bot or a monster camp (useful for watching bots) |
| `/mobs [level] [page]` | Player | Accessible monsters at a level |
| `/stables [classic\|si] [page]` | Player | Your realm's stable routes |
| `/disband` | Player | Upstream group command; removes companions |

The full list (about 290 commands) is in `ALL SERVER COMMANDS.txt` in the original 0.33 bundle,
and `/cmdhelp` shows it in game.

## 4. Admin and tuning (no rebuild needed)

| Knob | How | Effect |
|---|---|---|
| Create, list or delete autonomous bots | TUI **Bots** tab; `daoc-admin bots create --realm all --count 10 [--level 50] [--class Druid]`, `bots list`, `bots delete <name>` | Roster management |
| Population on/off, max in world | TUI **Population** tab; `daoc-admin population on\|off\|max N` | Logs bots in or out (characters are kept) |
| `bot_logins_per_second` | Server property (default 8) | Login speed |
| `startup_ramp_minutes` | Server property (default 0 = none) | Optional staggered startup |
| `ai_budget_ms` | Server property (default 4.0) | Time budget for bot decisions per game-loop slice; extra work is deferred |
| Goal mix per level band | `bin/bot-goals.json` (optional) | Percent split between **SoloPve / GroupPve / RvR** per band. Defaults: levels 1–19 = 60/40/0 (no RvR allowed), 20–49 = 40/40/20, 50 = 20/40/40. Read at startup and validated: a broken file stops startup rather than being ignored. |

Server properties can be changed in game with `/serverproperty` (GM), or in the `ServerProperty`
table while the server is stopped. Most are read at startup or on the bot controller's periodic
refresh.

## 5. The development loop

Do this inside the build box (`distrobox enter daoc-server-box`), where the .NET SDK is.

1. **Once, make a dev server folder:**
   ```bash
   cd /path/to/repo && tools/build-release.sh          # on this Deck: /home/deck/Dev/vscode/OfflineDAoC-main/next
   mkdir -p ~/next-test && tar -xzf dist/daoc-server-*-linux-x64.tar.gz -C ~/next-test
   cp dist/daoc-navmeshes-classic-si-1.tar.xz* ~/next-test/ && ~/next-test/daoc-server/navmesh-install.sh
   ```
   On this Steam Deck that folder already exists: `~/boxes/daoc-server-box-home/next-test/daoc-server`.
2. **Edit** code under `src/GameServer/bots/`.
3. **Deploy:** `tools/dev-deploy.sh ~/next-test/daoc-server`. It rebuilds the server DLLs (about
   15 s), copies them into `bin/`, and restarts the server if it was running. The world, bots and
   accounts are untouched.
4. **Watch:**
   - the TUI **Bots** tab (activity per bot);
   - `/tele <bot>` in game;
   - `logs/server.log` (bot lines carry the bot's name);
   - `logs/console.out`.
5. **Test:** `cd src && dotnet test Tests/Tests.csproj -c Release`. Add a `UT_*.cs` test next to
   the existing bot tests for every behaviour you change.
6. **Report honestly:** say "tests pass" and "I watched it in game" separately; they are
   different claims.

Only `GameServer.dll` and friends are swapped. A change to `CoreServer`, `DaocAdmin` or packages
needs a full `tools/build-release.sh`.

## 6. Where things live (`src/GameServer/bots/`)

| What you want to change | Where |
|---|---|
| Bot body: stats, gear, inventory, death and release | `GameBot.cs`, `BotEquipment.cs`, `BotInventory.cs`, `BotAttributeProgression.cs`, `BotLifetimeBuild.cs`, `specs/`, `BotReleaseBindPoints.cs`, `BotRestRecovery.cs` |
| Think loop and combat flow | `BotBrain.cs`, `BotBrain.BardPveCrowdControl.cs`, `BotBrain.PvpCrowdControl.cs` |
| Class combat AI: spells, styles, songs, ranged | `BotCasterPriority.cs`, `BotMeleeStylePolicy.cs`, `BotSongTwistPolicy.cs`, `MinstrelBotCombatPolicy.cs`, `SavageBotCombatPolicy.cs`, `BardBotCrowdControlPolicy.cs`, `BotPvpCrowdControl.cs`, `BotRangedCombat.cs`, `BotSpellPower.cs`, `BotPoisonSupply.cs`, `autonomous/Animist*`, `autonomous/BotAnimistPolicy.cs` |
| Pets and charm | `autonomous/AutonomousPetSupport.cs`, `autonomous/*CharmPolicy.cs`, `autonomous/CharmCreatureRoles.cs`, `BotGroupPetBuffTargets.cs` |
| Healing, buffing, group roles | `BotGroupSupport.cs`, `BotBuffReservations.cs`, `BotPartyRoles.cs`, `BotMaintenancePulsePolicy.cs` |
| Companions | `Companion*.cs`, `TemporaryCompanion*.cs`, `PlayerLedPullCoordinator.cs`, `TemporaryGroupStableTravel.cs`; commands in `commands/playercommands/` (`TemporaryGroupCommands`, `CompanionEngagementCommands`, `PlayerGrindCommand`, `CompanionRaidCommand`) |
| Goals: what to do next | `autonomous/AutonomousBotDecisionEngine.cs`, `AutonomousBotGoalPolicy.cs`, `AutonomousObjectiveAssignments.cs`, `BotGoalSettings.cs` |
| Hunting: camps and targets | `autonomous/AutonomousCampPlanningPipeline.cs`, `AutonomousCampLevelPolicy.cs`, `AutonomousPveTargetPolicy.cs`, `AutonomousAuditedCampPolicy.cs`, `AutonomousCapnBryGoalCatalog.cs` + `data/capnbry_classic_si_goals.json`, `AutonomousClassic165RestoredSpawnCatalog.cs` |
| Travel and navigation | `autonomous/AutonomousWorldBotController*.cs`, `AutonomousStableRoutePlanner.cs`, `AutonomousZone*.cs`, `AutonomousGroundTravelMotion.cs`, `AutonomousThreatAwarePathing.cs`, `AutonomousCorridor*.cs`, `AutonomousRouteRecoveryPolicy.cs`, `AutonomousStuckWatchdog.cs` |
| Dungeons | `autonomous/AutonomousDungeon*.cs`, `AutonomousDarknessFalls*.cs`, `data/dungeon_navigation_points.json`, `data/darkness_falls_*.json` |
| Groups and raids | `autonomous/AutonomousBotGroupCoordinator*.cs`, `AutonomousRealmRaid.cs`, `autonomous/RealmRaid*.cs`, `AutonomousGroup*.cs`, `BotGroupInvite.cs` |
| RvR: frontier, keeps, siege, events | `autonomous/AutonomousRvr*.cs`, `AutonomousSiege*.cs`, `AutonomousFrontier*.cs`, `autonomous/RealmEvent*.cs`, `autonomous/Rvr*.cs`, `BotSiegeRuntime.cs`, `BotRvrAmbush.cs` |
| Chat | `autonomous/AutonomousBotChat*.cs`, `AutonomousChatIntentModel.cs`, `AutonomousChatKnowledge.cs`, `autonomous/RealmEventBanter.cs` |
| Economy: Realm Exchange, selling | `autonomous/AutonomousBotEconomy.cs`, `AutonomousAuctionValuation.cs`, `AutonomousSupplyMerchantSpace.cs` |
| Population and logins | `autonomous/AutonomousPopulationController.cs`, `AutonomousPopulationProperties.cs`, `AutonomousPopulationRamp.cs`, `AutonomousRealmLoginBalancer.cs` |
| Admin operations | `GameServer/admin/BotRosterAdmin.cs` |

## 7. Conventions

- **No unbounded loops** in bot code. Every loop, retry and search has an explicit cap. A stuck
  bot must never stall the game loop.
- **Stay within the AI budget.** Heavy planning goes through the existing budget and fidelity
  policies (`AutonomousAiBudget`, `AutonomousFidelityPolicy`), not into the think loop.
- **Keep the owner's gameplay rules:**
  - real loot, inventories, coins and equipment upgrades;
  - the Realm Exchange;
  - stablemaster routes;
  - selling only at natural task breaks;
  - gamebots start without armor and earn it;
  - Darkness Falls is open to solo bots.
- **Don't describe** Darkness Falls raid AI, or the hardest level 70+ content, as implemented.
- **Line endings:** many source files use CRLF. Edit them without converting the whole file
  (check with `file <path>`).

## 8. Where the design is going: a separate, reloadable AI

Goal: change bot behaviour without touching or restarting the core, then grow it toward
battleground roaming, keep siege and defense, frontier, dungeons, natural spacing instead of
stacking on the player, and a chat interpreter (WoW playerbots style). Done incrementally, with
behaviour unchanged at each step:

1. **Separate project:** move the decision policies (class combat, target and camp choice, goal
   choice) into `src/BotAI/`. They're mostly self-contained `*Policy` classes already. The server
   references the project; no behaviour change.
2. **Narrow interface:** policies read the world through a small "what this bot sees" view and
   return decisions, instead of reaching into `GameBot`/`GameNPC`. One policy family at a time,
   each covered by its tests.
3. **Reload:** load `BotAI.dll` into a reloadable (collectible) context, so `daoc-admin bots reload`
   swaps behaviour on a live server. Bot state stays in the server, so bots keep their goals.
4. **Grow:** a trigger → action → strategy layer (react to adds and spawns, flee, assist),
   formation spacing for followers, battleground and keep strategies, and a chat module that turns
   whispers and says into intents, plus ambient talk.

An earlier experiment (bots as real `GamePlayer`s on unmodified upstream, in the deleted
`rewrite/`) proved that design works. Its findings, and the original plan, are in
`docs/history/`.
