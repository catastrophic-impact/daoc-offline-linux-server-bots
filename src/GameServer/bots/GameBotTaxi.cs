using DOL.GS.Movement;

namespace DOL.GS;

/// <summary>
/// Single authoritative mover for a persistent or temporary GameBot stable
/// ride. GameBot is NPC-backed and therefore cannot be inserted into the
/// native GamePlayer rider array. The taxi owns the waypoint path and copies
/// its real server position to the bot; clients receive the normal riding
/// association and never see two independently drifting movers.
/// </summary>
public sealed class GameBotTaxi : GameTaxi
{
    public GameBot Rider { get; }

    public GameBotTaxi(GameBot rider)
    {
        Rider = rider;
    }

    public GameBotTaxi(GameBot rider, INpcTemplate template) : base(template)
    {
        Rider = rider;
    }

    public void SynchronizeRider()
    {
        if (Rider?.IsOnStableMasterRoute != true || Rider.ObjectState is not eObjectState.Active ||
            CurrentRegion == null || Rider.CurrentRegion != CurrentRegion)
            return;

        Rider.SynchronizeStableRoutePosition(this);
    }
}
