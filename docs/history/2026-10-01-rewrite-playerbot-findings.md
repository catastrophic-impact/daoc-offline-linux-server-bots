# Findings from the abandoned rewrite: bots as real players

> **History.** The rewrite (deleted 2026-10-01) rebuilt the server on **unmodified upstream
> OpenDAoC** (`f0aaa1e`, 2026-09-26) and made bots real `GamePlayer`s driven by a headless client,
> the WoW playerbots model. It was abandoned because reaching parity with this code base meant
> rebuilding everything, not because the approach failed. If bots are ever moved to player bodies
> (for example, to drop the NPC-as-player core hooks), start here.

## What worked (automated runs; never checked in game)

Bot characters were created and logged in through upstream's own code paths. They walked navmesh
paths at player speed, saved and restored their positions, and ran with 0 errors.

## How each piece was solved, without editing upstream

| A real client does | The bot did instead |
|---|---|
| Creates a character | Built a `DbCoreCharacter` like `CharacterCreateRequestHandler.CreateCharacter`: race base stats from `GlobalConstants.STARTING_STATS_DICT` plus exactly 30 points (cost 1/2/3 per point above +10/+15), model from `PlayerRace.GetModel(gender)`, validated with `CharacterCreateRequestHandler.IsCharacterValid`. Then it fired `DatabaseEvent.CharacterCreated` so upstream assigned the start location and starter gear. |
| Logs in | Synthesized packets fed to upstream's own handlers: `WorldInitRequestHandler` (char index = realm*10-10+slot), then, once `Player.ObjectState == Active`, `GameOpenRequestHandler` and `PlayerInitRequestHandler`. `PlayerInit` also re-runs on every region change. |
| Has a session ID | Drawn from `BaseServer._sessionIdAllocator` (private, via reflection) so it can't collide with a player's ID, and set on the client via `BaseClient.SessionId`'s private setter, not `GameClient.OnConnect`, which schedules a ClientService registration for the next tick. |
| Is findable by session ID (other clients send "create player <sessionId>") | `ClientService.Instance.OnClientConnect(client)` then, in the same step, `ServiceObjectStore.Remove<GameClient>(client)`. The bot is in the session map but never ticked: ClientService's tick reads the socket, and `PacketProcessor.SendPendingPackets` dereferences `Socket` and throws. **Use `<GameClient>` explicitly**: the store is keyed by type, and `<BotClient>` throws an NRE. |
| Receives packets | `Out` = a `DispatchProxy` implementing `IPacketLib`: everything is a no-op except `SendTCP` (LOS requests) and `SendMessage` (chat, the hook for a chat module). |
| Answers LOS checks | Upstream asks the client (`CheckLOSRequest`: header 3 bytes, then source and target object IDs, big-endian). Answer from the navmesh (`HasLineOfSight`) via `Player.LosCheckHandler.HandleLosResponse`, deferred to the next tick, never inside the call. |
| Sends position packets | `player.movementComponent.UpdatePosition(vector)`, `CurrentSpeed`, `Heading`, `OnHeadingUpdate()`. Also refresh `LastPositionUpdatePacketReceivedTime`, or upstream's soft link-death fires. |
| Gets autosaved | Unticked clients aren't autosaved, so the runtime saved bots itself (every 5 minutes and on logout). |
| Logs out | `SaveIntoDatabase()`, `Quit(true)`, `ClientState = Disconnected`, re-add to the store and `OnClientDisconnect`, dispose the session ID. Don't use `Disconnect()`: on a Playing client it starts link-death and leaves the character in the world. |

## Gotchas found

- **Upstream removes a timer whose callback throws** (`GameServiceUtils.HandleServiceException`).
  A bot runtime timer must catch everything, or all bots silently stop.
- **New characters become base classes** (Druid becomes Naturalist): plan the real class and
  train it at level 5.
- **Logins took 50–100 ms on the game loop** (synchronous account and character load); move them
  off the loop for large populations.
- **Upstream's newer door registration** warns with older navmeshes (about 80 "Failed to register
  door" lines); fresh meshes from current BuildNav should fix it.
- **Upstream's script mechanism works as a plugin loader:** with `EnableCompilation=false`, the
  `ScriptCompilationTarget` assembly gets its events, commands and tables registered, and
  `ScriptMgr.LoadAssembly` is public.
