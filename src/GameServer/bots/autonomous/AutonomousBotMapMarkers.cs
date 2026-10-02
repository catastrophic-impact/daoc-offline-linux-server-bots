using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Publishes persistent friendly autonomous bots through the client's scalable
/// map-marker channel. It never alters group membership and never reveals an
/// opposing-realm bot, including in the frontiers.
/// </summary>
public static class AutonomousBotMapMarkers
{
    private const uint BotMarkerNamespace = 0x80000000;
    private const int PollMilliseconds = 4000;
    private const int MinimumMovementBeforeUpdate = 192;
    private const int InteriorRefreshMilliseconds = 12000;
    private static readonly Logger Log = LoggerManager.Create(typeof(AutonomousBotMapMarkers));
    private static readonly ConcurrentDictionary<GamePlayer, Dictionary<uint, MarkerState>> VisibleByPlayer = new();
    private static Timer _timer;
    private static int _polling;

    private readonly record struct MarkerState(ushort Region, int X, int Y, int Z, long SentAtTick);

    public static ushort ClientMapRegion(ushort actualRegionId, ushort regionSkin) =>
        regionSkin != 0 ? regionSkin : actualRegionId;

    public readonly record struct ClientMapPosition(ushort MapId, int X, int Y, int Z);

    /// <summary>
    /// Minotaur-relic map packets always use the actor's actual region and
    /// region/global coordinates. They are not ordinary object-create packets:
    /// converting interiors to ZoneSkinID and zone-local X/Y displaces markers.
    /// Client 1.127 also needs tools/patch_bot_map_client.py: its stock zone
    /// lookup omits the map crop origin when checking cropped-map bounds.
    /// Preserve Z so the client's unchanged area selector handles each floor.
    /// </summary>
    public static ClientMapPosition ResolveClientMapPosition(
        bool interior,
        ushort actualRegionId,
        ushort regionSkin,
        ushort zoneSkinId,
        int zoneXOffset,
        int zoneYOffset,
        int x,
        int y,
        int z)
    {
        return new ClientMapPosition(actualRegionId, x, y, z);
    }

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = new Timer(Poll, null, PollMilliseconds, PollMilliseconds);
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = null;
        VisibleByPlayer.Clear();
    }

    internal static bool IsVisibleTo(GamePlayer player, GameBot bot)
    {
        if (player?.ObjectState != GameObject.eObjectState.Active ||
            bot?.ObjectState != GameObject.eObjectState.Active ||
            !bot.IsAutonomousWorldBot || bot.IsTemporaryGroupHelper ||
            player.Realm != bot.Realm)
        {
            return false;
        }

        // Capital and dungeon regions can expose different/null Zone objects
        // at room boundaries even though both actors are on the same interior
        // map. Outdoors retain the intentionally narrow same-zone visibility.
        if (IsInteriorMap(player))
            return player.CurrentRegionID == bot.CurrentRegionID;

        return player.CurrentZone != null && bot.CurrentZone != null &&
               player.CurrentZone.ID == bot.CurrentZone.ID;
    }

    private static bool IsInteriorMap(GameLiving living) =>
        living?.CurrentRegion?.IsCapitalCity == true ||
        living?.CurrentRegion?.IsDungeon == true ||
        living?.CurrentZone?.IsDungeon == true;

    public static bool ShouldRefreshStationaryInterior(bool interior, long elapsedMilliseconds) =>
        interior && elapsedMilliseconds >= InteriorRefreshMilliseconds;

    private static uint MarkerId(GameBot bot) =>
        BotMarkerNamespace | ((uint)bot.DatabaseID & 0x7FFFFFFF);

    private static void Poll(object state)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
            return;

        try
        {
            GamePlayer[] players = ClientService.Instance.GetPlayers()
                .Where(player => player?.ObjectState == GameObject.eObjectState.Active)
                .ToArray();
            GameBot[] bots = AutonomousBotRegistry.Snapshot();
            HashSet<GamePlayer> activePlayers = players.ToHashSet();

            foreach (GamePlayer stalePlayer in VisibleByPlayer.Keys.Where(player => !activePlayers.Contains(player)))
                VisibleByPlayer.TryRemove(stalePlayer, out _);

            foreach (GamePlayer player in players)
                UpdatePlayer(player, bots);
        }
        catch (Exception ex)
        {
            Log.Error("Unable to publish autonomous bot map markers.", ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static void UpdatePlayer(GamePlayer player, GameBot[] bots)
    {
        Dictionary<uint, MarkerState> previous = VisibleByPlayer.GetOrAdd(player, _ => new Dictionary<uint, MarkerState>());
        GameBot[] visibleBots = bots.Where(bot => IsVisibleTo(player, bot)).ToArray();
        HashSet<uint> visibleIds = visibleBots.Select(MarkerId).ToHashSet();
        Dictionary<uint, MarkerState> current = new(visibleBots.Length);
        long now = GameLoop.GameLoopTime;
        bool interior = IsInteriorMap(player);

        foreach (uint markerId in previous.Keys.Except(visibleIds).ToArray())
            player.Out.SendMinotaurRelicMapRemove(markerId);

        foreach (GameBot bot in visibleBots)
        {
            uint markerId = MarkerId(bot);
            Zone zone = bot.CurrentZone;
            ClientMapPosition mapPosition = ResolveClientMapPosition(
                interior,
                bot.CurrentRegionID,
                bot.CurrentRegion?.Skin ?? 0,
                zone?.ZoneSkinID ?? 0,
                zone?.XOffset ?? 0,
                zone?.YOffset ?? 0,
                bot.X,
                bot.Y,
                bot.Z);
            MarkerState marker = new(mapPosition.MapId, mapPosition.X, mapPosition.Y, mapPosition.Z, now);
            bool send = !previous.TryGetValue(markerId, out MarkerState old) ||
                        NeedsUpdate(old, marker) ||
                        ShouldRefreshStationaryInterior(interior, now - old.SentAtTick);
            if (send)
                player.Out.SendMinotaurRelicMapUpdate(markerId, marker.Region, marker.X, marker.Y, marker.Z);
            current[markerId] = send ? marker : old;
        }

        VisibleByPlayer[player] = current;
    }

    private static bool NeedsUpdate(MarkerState old, MarkerState current)
    {
        // A vertical floor transition can have identical X/Y. Send its new Z
        // immediately at the next poll instead of waiting for interior refresh.
        if (old.Region != current.Region || HasChangedFloorHeight(old.Z, current.Z))
            return true;

        long dx = old.X - (long)current.X;
        long dy = old.Y - (long)current.Y;
        long threshold = MinimumMovementBeforeUpdate;
        return dx * dx + dy * dy >= threshold * threshold;
    }

    public static bool HasChangedFloorHeight(int previousZ, int currentZ) => previousZ != currentZ;
}
