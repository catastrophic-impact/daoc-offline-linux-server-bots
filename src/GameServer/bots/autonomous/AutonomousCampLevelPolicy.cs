using System.Collections.Generic;
using System.Linq;

namespace DOL.GS;

/// <summary>Keep period-authored camp locations, but never advertise a mob
/// level that is absent from the matching live spawns near that location.</summary>
public static class AutonomousCampLevelPolicy
{
    public static int[] ObservedAuthoredLevels(IEnumerable<int> authoredLevels, IEnumerable<int> liveLevels)
    {
        if (authoredLevels == null || liveLevels == null)
            return [];
        HashSet<int> authored = authoredLevels.Where(level => level > 0).ToHashSet();
        int[] observed = liveLevels.Where(level => level > 0)
            .Distinct().OrderBy(level => level).ToArray();
        int[] historicallyMatching = observed.Where(authored.Contains).ToArray();
        // Prefer matching period levels when the live camp has them. If this
        // server's authentic spawn is a different level, use its observed level
        // rather than deleting the entire otherwise reachable camp from goals.
        return historicallyMatching.Length > 0 ? historicallyMatching : observed;
    }
}
