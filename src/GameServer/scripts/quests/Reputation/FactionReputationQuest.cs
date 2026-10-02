using System;
using System.Collections.Generic;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Network;

namespace DOL.GS.Quests
{
    /// <summary>
    /// Repeatable Shrouded Isles reputation hunt: slay ten of a neutral
    /// faction's enemies and return to its emissary. Each turn-in trusts the
    /// player 10 points more (5 after a reroll, like a rerolled bounty's XP).
    /// Target, progress and reroll state live on the ordinary Quest row.
    /// </summary>
    public sealed class FactionReputationQuest : RewardQuest
    {
        public const int RequiredKillCount = 10;
        public const int FullReputationGain = 10;
        public const int RerolledReputationGain = 5;
        private const int NameByteLimit = 56;
        private readonly object _completionLock = new();

        public FactionReputationQuest() : base() { }
        public FactionReputationQuest(GamePlayer player) : this(player, 1) { }
        public FactionReputationQuest(GamePlayer player, int step) : base(player, step) { }

        public FactionReputationQuest(GamePlayer player, DbQuest quest) : base(player, quest)
        {
            QuestGiver = FactionEmissaryRuntime.GetEmissary(player.Realm);
            RestoreGoal();
            ShowMarker();
        }

        public override int MaxQuestCount => int.MaxValue;
        public override int Level => Math.Max(1, (int)TargetLevel);

        private FactionEmissaryDefinition Definition => FactionEmissaryRuntime.GetDefinition(m_questPlayer?.Realm ?? eRealm.None);

        public override string Name => $"{Definition?.FactionName ?? "Faction"} Favor: {Fit(TargetName, NameByteLimit)}";
        public override string Story => Definition == null ? string.Empty : FactionEmissaryRuntime.Story(Definition);
        public override string Summary => $"Slay {RequiredKillCount} of the faction's enemies and return to its emissary for reputation.";
        public override string Conclusion => "The emissary marks your deed. Another hunt waits whenever you are ready.";

        public override string Description
        {
            get
            {
                if (string.IsNullOrEmpty(TargetName))
                    return "Speak to the faction's emissary to receive a hunt.";
                return FormatJournalDescription(TargetName, ZoneName, Progress, RequiredKills, IsReady,
                    Definition?.FactionName ?? "the faction", ReputationGain);
            }
        }

        public string TargetName => GetCustomProperty("targetname") ?? string.Empty;
        public string ZoneName => GetCustomProperty("zonename") ?? "the marked area";
        public byte TargetLevel => byte.TryParse(GetCustomProperty("targetlevel"), out byte level) ? level : (byte)0;
        public int TargetFactionId => ReadInt("targetfaction");
        public bool WasRerolled => GetCustomProperty("rerolled") == "1";
        public int RequiredKills => ReadInt("required");
        public int Progress => ReadInt("goal1Current");
        public bool IsReady => Step == 2 && Goals.Count > 0 && Goals[0].IsAchieved;
        public int ReputationGain => ReputationGainFor(WasRerolled);

        public static int ReputationGainFor(bool rerolled) => rerolled ? RerolledReputationGain : FullReputationGain;

        public FactionTargetCandidate Target => string.IsNullOrEmpty(TargetName) ? null : new FactionTargetCandidate
        {
            Name = TargetName,
            FactionId = TargetFactionId,
            RegionId = (ushort)ReadInt("region"),
            ZoneId = (ushort)ReadInt("zone"),
            ZoneName = ZoneName,
            X = ReadInt("x"),
            Y = ReadInt("y"),
            Z = ReadInt("z"),
            TypicalLevel = TargetLevel
        };

        /// <summary>Kept under the 255-byte journal field so a saved quest can never break login.</summary>
        public static string FormatJournalDescription(string targetName, string zoneName, int progress, int required,
            bool ready, string factionName, int gain)
        {
            string target = Fit(targetName, NameByteLimit);
            string zone = Fit(zoneName, NameByteLimit);
            string action = ready
                ? $"{progress}/{required} {target} slain. Return to the emissary."
                : $"Slay {target}: {progress}/{required}.";
            string full = $"{action} In {zone}; the red dot marks them on that zone's map. Reward: +{gain} {Fit(factionName, 24)} reputation.";
            return BaseServer.DefaultEncoding.GetByteCount(full) <= byte.MaxValue ? full : $"{action} In {zone}.";
        }

        public override bool CheckQuestQualification(GamePlayer player)
        {
            if (player == null || FactionEmissaryRuntime.GetDefinition(player.Realm) == null)
                return false;

            if (player.IsDoingQuest(typeof(FactionReputationQuest)) != null)
                return true;

            return player.Level is >= 1 and <= 50;
        }

        public override void OnQuestAssigned(GamePlayer player)
        {
            QuestGiver = FactionEmissaryRuntime.GetEmissary(player.Realm);
            if (!Assign(false, null))
            {
                player.Out.SendMessage("No hunt could be chosen right now. Please speak to the emissary again.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                AbortQuest();
                return;
            }

            base.OnQuestAssigned(player);
            player.Out.SendMessage($"New hunt: {RequiredKills} {TargetName} in {ZoneName}. The red dot marks them on that zone's map.",
                eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        }

        public override void Notify(DOLEvent e, object sender, EventArgs args)
        {
            if (e == GameLivingEvent.EnemyKilled && sender == m_questPlayer &&
                args is EnemyKilledEventArgs killed && killed.Target is GameNPC npc && Step == 1 &&
                Target?.Matches(npc) == true && Goals.Count > 0)
            {
                Goals[0].Advance();
                if (Goals[0].IsAchieved)
                {
                    Step = 2;
                    m_questPlayer.Out.SendMessage("Hunt complete. Return to the emissary to claim your reputation.",
                        eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                    FactionEmissaryRuntime.UpdateIndicator(m_questPlayer);
                }
            }

            base.Notify(e, sender, args);
        }

        /// <summary>Rerolls can be repeated; the reduced reward stays until the hunt is turned in.</summary>
        public bool Reroll()
        {
            if (m_questPlayer == null || Step is not (1 or 2))
                return false;

            string previous = TargetName;
            if (!Assign(true, previous))
            {
                m_questPlayer.Out.SendMessage("No different hunt is available right now. Your hunt and reward are unchanged.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }

            m_questPlayer.Out.SendMessage($"New hunt: {RequiredKills} {TargetName} in {ZoneName}. This hunt now grants +{RerolledReputationGain} reputation.",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            return true;
        }

        public bool Claim()
        {
            lock (_completionLock)
            {
                FactionEmissaryDefinition definition = Definition;
                GameNPC emissary = FactionEmissaryRuntime.GetEmissary(m_questPlayer?.Realm ?? eRealm.None);
                if (!IsReady || m_questPlayer == null || definition == null || emissary == null ||
                    emissary.CurrentRegionID != m_questPlayer.CurrentRegionID ||
                    !emissary.IsWithinRadius(m_questPlayer, 600))
                    return false;

                Faction faction = FactionMgr.GetFactionByID(definition.FactionId);
                if (faction == null)
                {
                    m_questPlayer.Out.SendMessage("This faction's records cannot be found; your hunt stays complete.",
                        eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    return false;
                }

                int gain = ReputationGain;
                int newAggro = faction.AdjustAggroLevel(m_questPlayer, -gain);

                // The hunted monsters' own faction likes you less by the same
                // amount, but only if it was already hostile by default. A
                // faction that starts friendly or neutral is never touched.
                Faction enemy = FactionMgr.GetFactionByID(TargetFactionId);
                string enemyNote = string.Empty;
                if (ShouldPenalizeEnemy(faction, enemy))
                {
                    enemy.AdjustAggroLevel(m_questPlayer, gain);
                    enemyNote = $" The {enemy.Name} will remember this.";
                }

                ReputationMapMarkers.Clear(m_questPlayer);
                base.FinishQuest();
                // Repeatable: keep no growing list of identical completed rows.
                m_questPlayer.RemoveFinishedQuest(this);
                DeleteFromDatabase();

                emissary.SayTo(m_questPlayer, FactionEmissaryRuntime.PaidLine(definition));
                m_questPlayer.Out.SendMessage($"{definition.FactionName} reputation +{gain}: now {FactionEmissaryRuntime.DescribeStanding(newAggro)}.{enemyNote}",
                    eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                FactionEmissaryRuntime.UpdateIndicator(m_questPlayer);
                return true;
            }
        }

        public static bool ShouldPenalizeEnemy(Faction faction, Faction enemy) =>
            faction != null && enemy != null && enemy != faction &&
            enemy.BaseAggroLevel > 75 &&
            (faction.EnemyFactions.Contains(enemy) || enemy.EnemyFactions.Contains(faction));

        public override void FinishQuest() => Claim();

        public override void AbortQuest()
        {
            ReputationMapMarkers.Clear(m_questPlayer);
            base.AbortQuest();
            FactionEmissaryRuntime.UpdateIndicator(m_questPlayer);
        }

        public void ShowMarker()
        {
            FactionTargetCandidate target = Target;
            if (target != null && m_questPlayer != null && Step is 1 or 2)
                ReputationMapMarkers.Set(m_questPlayer, target.RegionId, target.X, target.Y, target.Z);
        }

        private bool Assign(bool rerolled, string previousName)
        {
            FactionEmissaryDefinition definition = Definition;
            if (definition == null)
                return false;

            IReadOnlyList<FactionTargetCandidate> candidates =
                FactionReputationTargets.GetCandidates(definition.FactionId, definition.HuntRegionId);
            FactionTargetCandidate[] pool = FactionReputationTargets.SelectPool(candidates, m_questPlayer.Level, previousName);
            if (pool.Length == 0)
                return false;

            FactionTargetCandidate target = pool[Random.Shared.Next(pool.Length)];
            // Once a hunt has been rerolled it stays at the reduced reward.
            bool reduced = rerolled || WasRerolled;

            ReputationMapMarkers.Clear(m_questPlayer);
            lock (m_customProperties)
            {
                m_customProperties["targetname"] = Safe(target.Name);
                m_customProperties["targetlevel"] = target.TypicalLevel.ToString();
                m_customProperties["targetfaction"] = target.FactionId.ToString();
                m_customProperties["region"] = target.RegionId.ToString();
                m_customProperties["zone"] = target.ZoneId.ToString();
                m_customProperties["zonename"] = Safe(target.ZoneName);
                m_customProperties["x"] = target.X.ToString();
                m_customProperties["y"] = target.Y.ToString();
                m_customProperties["z"] = target.Z.ToString();
                m_customProperties["required"] = RequiredKillCount.ToString();
                m_customProperties["rerolled"] = reduced ? "1" : "0";
                m_customProperties["goal1Current"] = "0";
                SaveCustomProperties();
            }

            Step = 1;
            RestoreGoal();
            ShowMarker();
            m_questPlayer.Out.SendQuestUpdate(this);
            FactionEmissaryRuntime.UpdateIndicator(m_questPlayer);
            return true;
        }

        private void RestoreGoal()
        {
            Goals.Clear();
            FactionTargetCandidate target = Target;
            if (target == null || RequiredKills < 1)
                return;

            QuestGoal goal = AddGoal($"Slay {Fit(target.Name, NameByteLimit)}", QuestGoal.GoalType.KillTask, RequiredKills, null);
            Zone zone = WorldMgr.GetRegion(target.RegionId)?.GetZone(target.X, target.Y);
            if (zone != null)
                goal.SetWaypoint(zone.ID, target.X - zone.XOffset, target.Y - zone.YOffset);
        }

        private int ReadInt(string key) => int.TryParse(GetCustomProperty(key), out int value) ? value : 0;

        private static string Safe(string value) => (value ?? string.Empty).Replace(';', ',').Replace('=', '-');

        private static string Fit(string value, int maxBytes)
        {
            value ??= string.Empty;
            if (BaseServer.DefaultEncoding.GetByteCount(value) <= maxBytes)
                return value;

            const string suffix = "...";
            int length = value.Length;
            while (length > 0 && BaseServer.DefaultEncoding.GetByteCount(value.AsSpan(0, length)) > maxBytes - suffix.Length)
                length--;
            return value[..length].TrimEnd() + suffix;
        }
    }
}
