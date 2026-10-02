using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using static DOL.GS.ServerRules.IServerRules;

namespace DOL.GS;

/// <summary>
/// Treats a persistent autonomous gamebot as a player only for RvR realm-point
/// credit. Ordinary NPCs, controlled pets, and temporary /spawn companions
/// remain ineligible victims.
/// </summary>
public static class AutonomousBotRealmPointRewards
{
    internal const string LastRealmPointDeathTickProperty = "autonomous.rvr.last.realm.point.death.tick";

    public static bool IsEligibleVictim(GameNPC npc) =>
        npc is GameBot { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false };

    public static int GetPlayerEquivalentRealmPointValue(byte level, int realmLevel)
    {
        // This is the pre-1.81 player formula used by GamePlayer.RealmPointsValue.
        int modifiedLevel = level - 20;
        return Math.Max(1, modifiedLevel * modifiedLevel) + realmLevel;
    }

    public static int CalculateRealmPointReward(int victimRealmPointValue, int victimRealmLevel,
        int awarderRealmPointValue, int awarderRealmLevel, int participantCount,
        int groupContributorCount, double damagePercent, bool applyRealmRankAdjustment)
    {
        if (victimRealmPointValue <= 0 || awarderRealmPointValue <= 0 ||
            participantCount <= 0 || damagePercent <= 0)
        {
            return 0;
        }

        int baseRealmPoints = victimRealmPointValue / participantCount;
        baseRealmPoints = Math.Min(baseRealmPoints, awarderRealmPointValue * 2);
        int realmPoints = (int)(baseRealmPoints * Math.Min(1.0, damagePercent));

        if (applyRealmRankAdjustment)
        {
            realmPoints = (int)(realmPoints *
                (1.0 + 2.0 * (victimRealmLevel - awarderRealmLevel) / 900.0));
        }

        if (groupContributorCount > 1)
            realmPoints += (int)(realmPoints * (groupContributorCount - 1) * 0.125);

        return Math.Max(0, realmPoints);
    }

    public static void Award(GameBot killedBot, GameObject killer)
    {
        if (!IsEligibleVictim(killedBot))
            return;

        long now = GameLoop.GameLoopTime;
        long previousDeath = killedBot.TempProperties.GetProperty<long>(
            LastRealmPointDeathTickProperty, -1);
        long worthInterval = Math.Max(0, Properties.RP_WORTH_SECONDS) * 1000L;
        bool isWorthRealmPoints = previousDeath < 0 || now - previousDeath >= worthInterval;

        // Every death resets the same repeat-kill window used for a real player,
        // including a death caused by an NPC or by somebody who receives no RP.
        killedBot.TempProperties.SetProperty(LastRealmPointDeathTickProperty, now);

        KeyValuePair<GameLiving, double>[] rawContributors;
        lock (killedBot.XpGainersLock)
            rawContributors = killedBot.XPGainers.ToArray();

        Dictionary<GameLiving, double> hostileContributors = new();
        foreach (KeyValuePair<GameLiving, double> pair in rawContributors)
        {
            GameLiving credited = ResolveRootRewardOwner(pair.Key);
            if (credited == null || credited.Realm == eRealm.None || credited.Realm == killedBot.Realm)
                continue;

            hostileContributors[credited] = hostileContributors.TryGetValue(credited, out double existing)
                ? existing + pair.Value
                : pair.Value;
        }

        double totalDamage = hostileContributors.Sum(pair => pair.Value);
        if (totalDamage <= 0)
            return;

        Dictionary<GamePlayer, EntityCountTotalDamagePair> playerContributions = new();
        Dictionary<Group, EntityCountTotalDamagePair> groupContributions = new();

        foreach (KeyValuePair<GameLiving, double> pair in hostileContributors)
        {
            // Persistent gamebots remain part of the damage denominator, just
            // like another real participant, but this path only pays connected
            // players. Temporary companions have already resolved to the owner.
            if (pair.Key is not GamePlayer player ||
                player.ObjectState is not GameObject.eObjectState.Active ||
                !player.IsWithinRadius(killedBot, WorldMgr.MAX_EXPFORKILL_DISTANCE))
            {
                continue;
            }

            AddContribution(player, pair.Value, player, playerContributions);
            if (player.Group != null)
                AddContribution(player, pair.Value, player.Group, groupContributions);
        }

        if (playerContributions.Count == 0)
            return;

        GameLiving creditedKiller = ResolveRootRewardOwner(killer as GameLiving);
        int victimValue = GetPlayerEquivalentRealmPointValue(killedBot.Level, killedBot.RealmLevel);

        foreach (KeyValuePair<GamePlayer, EntityCountTotalDamagePair> pair in playerContributions)
        {
            GamePlayer player = pair.Key;
            lock (player.AwardLock)
            {
                EntityCountTotalDamagePair contribution = pair.Value;
                if (player.Group != null && groupContributions.TryGetValue(player.Group, out EntityCountTotalDamagePair group))
                    contribution = group;

                double damagePercent = Math.Min(1.0, contribution.Damage / totalDamage);
                int contributorCount = Math.Max(1, contribution.Count);
                int groupContributorCount = player.Group == null ? 1 : contributorCount;
                int realmPointsEarned = 0;

                if (isWorthRealmPoints)
                {
                    DbBattleground battleground = GameServer.KeepManager.GetBattleground(player.CurrentRegionID);
                    bool applyRankAdjustment = battleground == null || player.RealmLevel < battleground.MaxRealmLevel;
                    realmPointsEarned = CalculateRealmPointReward(victimValue, killedBot.RealmLevel,
                        player.RealmPointsValue, player.RealmLevel, contributorCount,
                        groupContributorCount, damagePercent, applyRankAdjustment);

                    if (realmPointsEarned > 0)
                        player.GainRealmPoints(realmPointsEarned, true);
                }
                else
                {
                    player.Out.SendMessage($"{killedBot.Name} has been killed recently and is worth no realm points!",
                        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                bool deathBlow = ReferenceEquals(player, creditedKiller);
                bool soloKill = damagePercent >= 1.0 && contributorCount == 1;
                player.UpdateKillStatsOnPlayerKill(killedBot.Realm, deathBlow, soloKill, realmPointsEarned);
            }
        }
    }

    public static GameLiving ResolveRootRewardOwner(GameLiving source)
    {
        GameLiving current = source;
        for (int depth = 0; depth < 16 && current is GameNPC npc &&
             npc.Brain is IControlledBrain controlled &&
             controlled.GetLivingOwner() is GameLiving owner; depth++)
        {
            current = owner;
        }

        return current;
    }

    private static void AddContribution<T>(GameLiving participant, double damage, T entity,
        Dictionary<T, EntityCountTotalDamagePair> contributions) where T : class, IGameStaticItemOwner
    {
        if (contributions.TryGetValue(entity, out EntityCountTotalDamagePair value))
        {
            value.Count++;
            value.Damage += damage;
            if (value.HighestLevelPlayer.Level < participant.Level)
                value.HighestLevelPlayer = participant;
        }
        else
        {
            contributions[entity] = new EntityCountTotalDamagePair(1, damage, participant);
        }
    }
}
