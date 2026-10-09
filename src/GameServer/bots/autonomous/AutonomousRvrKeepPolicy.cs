using DOL.GS.Keeps;

namespace DOL.GS
{
    /// <summary>Portal keeps are protected travel hubs, never siege objectives.
    /// Relic keeps remain valid raid objectives even though the relic, rather
    /// than the keep itself, is the capturable objective.</summary>
    public static class AutonomousRvrKeepPolicy
    {
        public static bool IsSiegeObjective(AbstractGameKeep keep)
        {
            // Battleground central keeps start neutral (realm none) and are taken from that state.
            return keep != null && !keep.IsPortalKeep &&
                   (keep.Realm != eRealm.None || BattlegroundBrackets.IsBattlegroundRegion(keep.Region));
        }
    }
}
