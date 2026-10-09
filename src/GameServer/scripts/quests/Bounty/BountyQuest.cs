using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Network;

namespace DOL.GS.Quests
{
    /// <summary>
    /// One persistent, repeatable contract per player. The target, required count,
    /// progress, assignment level, and reroll penalty live on the ordinary Quest
    /// row, so the native journal survives logout without another progress table.
    /// </summary>
    public sealed class BountyQuest : RewardQuest
    {
        private const int TargetDisplayByteLimit = 56;
        private const string LastCompletedTargetKey = "BountyLastCompletedTargetName";

        /// <summary>
        /// Saved as the "format" property. Bounties saved by 0.33 and earlier have
        /// no format key; they keep working as Normal hunts with the new kill
        /// count and can be swapped once, free, for a new-style hunt.
        /// </summary>
        public const string CurrentFormat = "2";
        private static readonly Logging.Logger Log = Logging.LoggerManager.Create(typeof(BountyQuest));
        private readonly object _completionLock = new();
        private BountyTargetCandidate _targetCache;

        public BountyQuest() : base() { }
        public BountyQuest(GamePlayer player) : this(player, 1) { }
        public BountyQuest(GamePlayer player, int step) : base(player, step) { }

        public BountyQuest(GamePlayer player, DbQuest quest) : base(player, quest)
        {
            QuestGiver = BountyMasterRuntime.GetMaster(player.Realm);
            RestoreGoal();
            ShowMarker();
        }

        public override int MaxQuestCount => int.MaxValue;
        public override int Level => AssignedLevel == 0 ? 1 : AssignedLevel;
        public override string Name => FormatQuestName(Target, Difficulty);

        public override string Story =>
            "The roads are never safe for long. Take one hunt at a time, follow the mark on your map, " +
            "and bring proof of the kill back to your realm's Bounty Master.";

        public override string Summary =>
            "A random monster at your level (6 or 12 levels higher on hard or very hard), or a great named foe at level 50. " +
            "Complete the hunt and return for equipment and experience.";

        public override string Conclusion => "The road is safer for your work. Another contract waits whenever you are ready.";

        public override string Description
        {
            get
            {
                BountyTargetCandidate target = Target;
                if (target == null)
                    return "Speak to a Bounty Master to receive a hunt.";
                return FormatJournalDescription(target, AssignedLevel, WasRerolled,
                    Progress, RequiredKills, Step == 2, Difficulty);
            }
        }

        /// <summary>
        /// The 1.127 QuestEntry packet writes this field as a Pascal string.
        /// More than 255 encoded bytes throws while sending the journal and can
        /// prevent a character with a saved bounty from logging in. Keep the
        /// objective and turn-in state first, with flavor text only when it fits.
        /// </summary>
        public static string FormatJournalDescription(BountyTargetCandidate target, byte assignedLevel,
            bool rerolled, int progress, int requiredKills, bool ready,
            BountyDifficulty difficulty = BountyDifficulty.Normal)
        {
            if (target == null)
                return "Speak to a Bounty Master to receive a hunt.";

            string targetName = FitPacketText(target.Name, TargetDisplayByteLimit);
            string zoneName = FitPacketText(target.ZoneName, TargetDisplayByteLimit);
            string action = ready
                ? $"{progress}/{requiredKills} {targetName} slain. Return to the Bounty Master."
                : $"Slay {targetName}: {progress}/{requiredKills}.";
            string direction = target.IsDungeon
                ? $"Inside {zoneName}."
                : $"In {zoneName}.";
            int bulbs = BountyDifficultyRules.Bulbs(difficulty, rerolled);
            string reward = assignedLevel == 50
                ? "Reward: 100g and 1-3 exceptional class items."
                : $"Reward: {bulbs} bulb{(bulbs == 1 ? "" : "s")} of Lv{assignedLevel} XP, " +
                  $"1-3 Lv{BountyDifficultyRules.GearLevel(assignedLevel, difficulty)} class items.";
            string essential = $"{action} {direction} {reward} Map: /bountylocation.";
            string flavored = $"{GetHuntReason(target)} {essential}";

            // Preserve lore when possible, but never sacrifice packet safety or
            // the live kill count and return instructions for a long target name.
            if (BaseServer.DefaultEncoding.GetByteCount(flavored) <= byte.MaxValue)
                return flavored;
            if (BaseServer.DefaultEncoding.GetByteCount(essential) <= byte.MaxValue)
                return essential;

            // Even malformed saved counts must not make a login packet fail.
            // Drop optional reward/map text as whole clauses, never mid-word.
            return $"{action} {direction}";
        }

        public static string FormatQuestName(BountyTargetCandidate target,
            BountyDifficulty difficulty = BountyDifficulty.Normal) => target == null
            ? "The Realm's Bounty"
            : difficulty == BountyDifficulty.Normal
                ? $"Bounty: {FitPacketText(target.Name, TargetDisplayByteLimit)}"
                : $"{BountyDifficultyRules.DisplayName(difficulty)} Bounty: {FitPacketText(target.Name, TargetDisplayByteLimit)}";

        public static string FormatGoalName(BountyTargetCandidate target) => target == null
            ? "Slay the target"
            : $"Slay {FitPacketText(target.Name, TargetDisplayByteLimit)}";

        private static string FitPacketText(string value, int maxBytes)
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

        public byte AssignedLevel => ReadByte("assigned");
        public bool WasRerolled => GetCustomProperty("rerolled") == "1";
        public BountyDifficulty Difficulty => BountyDifficultyRules.Parse(GetCustomProperty("difficulty"));
        public bool IsLegacyFormat => IsLegacyFormatValue(GetCustomProperty("format"));

        /// <summary>A 0.33-or-older leveling bounty the player may swap once, free.</summary>
        public bool CanUpdateLegacy => IsLegacyFormat && Step is 1 or 2 && AssignedLevel is >= 1 and < 50 &&
                                       m_questPlayer?.Level < 50;

        public static bool IsLegacyFormatValue(string savedFormat) => savedFormat != CurrentFormat;
        public int RequiredKills => ReadInt("required");
        public int Progress => ReadInt("goal1Current");
        public bool IsReady => Step == 2 && Goals.Count > 0 && Goals[0].IsAchieved;

        public BountyTargetCandidate Target
        {
            get
            {
                if (_targetCache != null)
                    return _targetCache;

                string id = GetCustomProperty("targetid");
                if (string.IsNullOrEmpty(id) || m_questPlayer == null)
                    return null;

                // The catalog may change when a camp is temporarily wiped or
                // respawning. A saved contract must remain huntable regardless.
                BountyTargetCandidate live = BountyTargetCatalog.Resolve(m_questPlayer.Realm, AssignedLevel, id);
                if (live != null)
                    return _targetCache = live;

                return _targetCache = new BountyTargetCandidate
                {
                    Name = GetCustomProperty("targetname") ?? string.Empty,
                    Level = ReadByte("targetlevel"),
                    RegionId = (ushort)ReadInt("region"),
                    ZoneId = (ushort)ReadInt("zone"),
                    ZoneName = GetCustomProperty("zonename") ?? "marked area",
                    X = ReadInt("x"),
                    Y = ReadInt("y"),
                    Z = ReadInt("z"),
                    RepresentativeMobId = id,
                    IsDungeon = GetCustomProperty("dungeon") == "1",
                    IsEpic = GetCustomProperty("epic") == "1"
                };
            }
        }

        public override bool CheckQuestQualification(GamePlayer player)
        {
            if (player == null || player.Realm is not (eRealm.Albion or eRealm.Midgard or eRealm.Hibernia))
                return false;

            if (player.IsDoingQuest(typeof(BountyQuest)) != null)
                return true;

            // The quest icon is queried often while players move near town.
            // Candidate discovery scans the world's spawn catalog and belongs
            // only in Assign(), never in this hot-path qualification check.
            return player.Level is >= 1 and <= 50;
        }

        public override void OnQuestAssigned(GamePlayer player)
        {
            QuestGiver = BountyMasterRuntime.GetMaster(player.Realm);
            BountyDifficulty difficulty = BountyMasterRuntime.TakeOfferedDifficulty(player);
            string previousName = GetLastCompletedTargetName(player);
            if (!Assign(player.Level, false, previousName, difficulty))
            {
                string kind = difficulty == BountyDifficulty.Normal || player.Level >= 50
                    ? "bounty"
                    : $"{BountyDifficultyRules.DisplayName(difficulty).ToLowerInvariant()} bounty";
                player.Out.SendMessage(string.IsNullOrWhiteSpace(previousName)
                        ? $"No safe {kind} could be selected right now. Please speak to the Bounty Master again."
                        : $"No different monster is eligible for a {kind} at this level right now. Your last hunt will not be assigned twice in a row.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                AbortQuest();
                return;
            }

            base.OnQuestAssigned(player);
            string label = Difficulty == BountyDifficulty.Normal
                ? "New bounty"
                : $"New {BountyDifficultyRules.DisplayName(Difficulty).ToLowerInvariant()} bounty";
            player.Out.SendMessage($"{label}: {Target.Name} in {Target.ZoneName}. {RequiredKills} kill{(RequiredKills == 1 ? "" : "s")} required.",
                eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        }

        public override void Notify(DOLEvent e, object sender, EventArgs args)
        {
            if (e == GameLivingEvent.EnemyKilled && sender == m_questPlayer &&
                args is EnemyKilledEventArgs killed && killed.Target is GameNPC npc && Step == 1 &&
                Target?.Matches(npc) == true && Goals.Count > 0 &&
                BountyDifficultyRules.KillCounts(Difficulty, Target.Level, npc.Level))
            {
                Goals[0].Advance();
                if (Goals[0].IsAchieved)
                {
                    Step = 2;
                    m_questPlayer.Out.SendMessage("Bounty complete. Return to your Bounty Master for the reward.",
                        eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                    BountyMasterRuntime.UpdateIndicator(m_questPlayer);
                }
            }

            base.Notify(e, sender, args);
        }

        /// <summary>
        /// Rerolls can be repeated, but the half-XP flag never clears. A reroll may
        /// keep the difficulty or pick an easier one, and pays half of the new one.
        /// </summary>
        public bool Reroll(BountyDifficulty difficulty = BountyDifficulty.Normal)
        {
            if (m_questPlayer == null || Step is not (1 or 2))
                return false;

            if (AssignedLevel == 50)
                difficulty = BountyDifficulty.Normal;
            else if (difficulty > Difficulty)
            {
                m_questPlayer.Out.SendMessage("A reroll can only keep this bounty's difficulty or choose an easier one.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }

            BountyTargetCandidate previous = Target;
            if (!Assign(AssignedLevel, true, previous?.Name, difficulty))
            {
                m_questPlayer.Out.SendMessage("No different monster is available for this level right now. Your bounty and reward are unchanged.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }

            int bulbs = BountyDifficultyRules.Bulbs(Difficulty, true);
            string rerollReward = AssignedLevel == 50
                ? "The level-50 gold and gear reward is unchanged."
                : $"This {BountyDifficultyRules.DisplayName(Difficulty).ToLowerInvariant()} contract now pays {bulbs} XP bulb{(bulbs == 1 ? "" : "s")} at its assigned level.";
            m_questPlayer.Out.SendMessage($"New target: {Target.Name} in {Target.ZoneName}. {rerollReward}",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            return true;
        }

        /// <summary>An outleveled contract may be replaced for free, including after a reroll.</summary>
        public bool RefreshOutleveled()
        {
            if (m_questPlayer == null || m_questPlayer.Level <= AssignedLevel || Step is not (1 or 2))
                return false;

            BountyDifficulty difficulty = m_questPlayer.Level >= 50 ? BountyDifficulty.Normal : Difficulty;
            if (!Assign(m_questPlayer.Level, false, null, difficulty))
            {
                m_questPlayer.Out.SendMessage("There are no suitable monsters at your present level. Your old bounty remains active.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }

            m_questPlayer.Out.SendMessage($"Your old contract has been replaced without penalty. New target: {Target.Name} in {Target.ZoneName}.",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            return true;
        }

        /// <summary>
        /// Swaps a 0.33-or-older leveling bounty for a new-style hunt at the player's
        /// level and any difficulty, free. A half-XP flag from an old reroll stays.
        /// </summary>
        public bool UpdateLegacy(BountyDifficulty difficulty)
        {
            if (m_questPlayer == null || !CanUpdateLegacy)
                return false;

            BountyTargetCandidate previous = Target;
            if (!Assign((byte)m_questPlayer.Level, WasRerolled, previous?.Name, difficulty))
            {
                m_questPlayer.Out.SendMessage($"No {BountyDifficultyRules.DisplayName(difficulty).ToLowerInvariant()} bounty is available at your level right now. Your old bounty remains active.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }

            m_questPlayer.Out.SendMessage($"Your old contract has been updated without penalty. New {BountyDifficultyRules.DisplayName(Difficulty).ToLowerInvariant()} target: " +
                $"{Target.Name} in {Target.ZoneName}, {RequiredKills} kills.",
                eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            return true;
        }

        /// <summary>
        /// Called when the player enters the world. A bounty saved by 0.33 or
        /// earlier keeps its target as a Normal hunt but needs only the new kill
        /// count; a great-foe bounty is unchanged and simply marked current.
        /// </summary>
        public void NormalizeLegacyOnLogin()
        {
            if (m_questPlayer == null || !IsLegacyFormat || Step is not (1 or 2))
                return;

            if (AssignedLevel is < 1 or >= 50)
            {
                SetCustomProperty("format", CurrentFormat);
                return;
            }

            (int required, int progress) = LegacyKillCounts(AssignedLevel, RequiredKills, Progress);
            if (required != RequiredKills || progress != Progress)
            {
                lock (m_customProperties)
                {
                    m_customProperties["required"] = required.ToString();
                    m_customProperties["goal1Current"] = progress.ToString();
                    SaveCustomProperties();
                }

                RestoreGoal();
                if (Step == 1 && progress >= required)
                    Step = 2;
                m_questPlayer.Out.SendQuestUpdate(this);
                BountyMasterRuntime.UpdateIndicator(m_questPlayer);
            }

            string update = CanUpdateLegacy
                ? " Your Bounty Master can also update it once, free, to a normal, hard or very hard hunt."
                : string.Empty;
            m_questPlayer.Out.SendMessage($"Your bounty is from an older version and now needs {required} kills ({progress}/{required}).{update}",
                eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        /// <summary>An old saved kill count drops to the new bracket; progress never exceeds it.</summary>
        public static (int Required, int Progress) LegacyKillCounts(byte assignedLevel, int savedRequired, int savedProgress)
        {
            int required = Math.Min(Math.Max(savedRequired, 1), RequiredKillsForLevel(assignedLevel));
            return (required, Math.Clamp(savedProgress, 0, required));
        }

        public bool Claim()
        {
            lock (_completionLock)
            {
                if (!IsReady || m_questPlayer == null || QuestGiver == null ||
                    QuestGiver.Realm != m_questPlayer.Realm ||
                    QuestGiver.CurrentRegionID != m_questPlayer.CurrentRegionID ||
                    !QuestGiver.IsWithinRadius(m_questPlayer, 600))
                    return false;

                string completedTargetName = Target?.Name;
                BountyRewardResult reward = BountyRewardService.Grant(m_questPlayer, AssignedLevel, WasRerolled, Difficulty);
                if (!reward.Granted)
                {
                    m_questPlayer.Out.SendMessage(reward.Reason ?? "Your bounty reward cannot be delivered yet.",
                        eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    return false;
                }

                BountyMapMarkers.Clear(m_questPlayer);
                base.FinishQuest();

                // Repeatable bounties do not need a growing permanent list of
                // identical completed quests. Keep no completed Quest rows.
                m_questPlayer.RemoveFinishedQuest(this);
                DeleteFromDatabase();
                RememberCompletedTarget(m_questPlayer, completedTargetName);
                m_questPlayer.Out.SendMessage(AssignedLevel == 50
                        ? $"Bounty paid: 100 gold and {reward.ItemCount} exceptional class item(s)."
                        : $"Bounty paid: {reward.ExperienceGranted:N0} experience and {reward.ItemCount} class item(s).",
                    eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                BountyMasterRuntime.UpdateIndicator(m_questPlayer);
                return true;
            }
        }

        public override void FinishQuest() => Claim();

        public override void AbortQuest()
        {
            BountyMapMarkers.Clear(m_questPlayer);
            base.AbortQuest();
            BountyMasterRuntime.UpdateIndicator(m_questPlayer);
        }

        public void ShowMarker()
        {
            BountyTargetCandidate target = Target;
            if (target != null && m_questPlayer != null && Step is 1 or 2)
                BountyMapMarkers.Set(m_questPlayer, target.RegionId, target.X, target.Y, target.Z);
        }

        /// <summary>
        /// Select by monster name, not representative spawn ID: one species can
        /// appear at several levels and camps. No consecutive assignment repeats
        /// the previous species, including level-50 targets and rerolls.
        /// </summary>
        public static BountyTargetCandidate[] GetAssignmentChoices(
            IReadOnlyList<BountyTargetCandidate> pool, string previousName)
        {
            if (pool == null || pool.Count == 0)
                return Array.Empty<BountyTargetCandidate>();

            return BountyTargetCatalog.ExcludeMonsterName(pool, previousName);
        }

        private bool Assign(byte level, bool rerolled, string previousName,
            BountyDifficulty difficulty = BountyDifficulty.Normal)
        {
            if (level >= 50)
                difficulty = BountyDifficulty.Normal;

            IReadOnlyList<BountyTargetCandidate> pool = level == 50
                ? BountyTargetCatalog.GetEpicCandidates(m_questPlayer.Realm)
                : BountyTargetCatalog.GetEligible(m_questPlayer.Realm, level, previousName, difficulty);
            if (pool.Count == 0)
                return false;

            BountyTargetCandidate[] choices = GetAssignmentChoices(pool, previousName);
            if (choices.Length == 0)
                return false;

            BountyTargetCandidate target = choices[Random.Shared.Next(choices.Length)];
            int required = level == 50 ? 1 : RequiredKillsForLevel(level);

            BountyMapMarkers.Clear(m_questPlayer);
            lock (m_customProperties)
            {
                m_customProperties["targetid"] = target.RepresentativeMobId;
                m_customProperties["targetname"] = Safe(target.Name);
                m_customProperties["targetlevel"] = target.Level.ToString();
                m_customProperties["assigned"] = level.ToString();
                m_customProperties["region"] = target.RegionId.ToString();
                m_customProperties["zone"] = target.ZoneId.ToString();
                m_customProperties["zonename"] = Safe(target.ZoneName);
                m_customProperties["x"] = target.X.ToString();
                m_customProperties["y"] = target.Y.ToString();
                m_customProperties["z"] = target.Z.ToString();
                m_customProperties["dungeon"] = target.IsDungeon ? "1" : "0";
                m_customProperties["epic"] = target.IsEpic ? "1" : "0";
                m_customProperties["required"] = required.ToString();
                m_customProperties["rerolled"] = rerolled ? "1" : "0";
                m_customProperties["difficulty"] = BountyDifficultyRules.Save(difficulty);
                m_customProperties["format"] = CurrentFormat;
                m_customProperties["goal1Current"] = "0";
                SaveCustomProperties();
            }

            _targetCache = target;
            Step = 1;
            RestoreGoal();
            ShowMarker();
            m_questPlayer.Out.SendQuestUpdate(this);
            BountyMasterRuntime.UpdateIndicator(m_questPlayer);
            return true;
        }

        private static string GetLastCompletedTargetName(GamePlayer player)
        {
            string cached = player.TempProperties.GetProperty<string>(LastCompletedTargetKey);
            if (!string.IsNullOrWhiteSpace(cached))
                return cached;

            try
            {
                DbCoreCharacterXCustomParam record = DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
                    DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId)
                        .And(DB.Column("KeyName").IsEqualTo(LastCompletedTargetKey)));
                if (!string.IsNullOrWhiteSpace(record?.Value))
                    player.TempProperties.SetProperty(LastCompletedTargetKey, record.Value);
                return record?.Value;
            }
            catch (Exception ex)
            {
                // Bounties remain available if this optional history read fails.
                Log.Warn($"Could not read last completed bounty target for {player.ObjectId}", ex);
                return null;
            }
        }

        private static void RememberCompletedTarget(GamePlayer player, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            // The reward and quest close first. A history-write failure must
            // never leave a paid quest claimable again.
            player.TempProperties.SetProperty(LastCompletedTargetKey, name);
            try
            {
                DbCoreCharacterXCustomParam record = DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
                    DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId)
                        .And(DB.Column("KeyName").IsEqualTo(LastCompletedTargetKey)));
                if (record == null)
                    GameServer.Database.AddObject(new DbCoreCharacterXCustomParam(player.ObjectId,
                        LastCompletedTargetKey, name));
                else
                {
                    record.Value = name;
                    GameServer.Database.SaveObject(record);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save last completed bounty target for {player.ObjectId}", ex);
            }
        }

        private void RestoreGoal()
        {
            Goals.Clear();
            BountyTargetCandidate target = Target;
            if (target == null || RequiredKills < 1)
                return;

            QuestGoal goal = AddGoal(FormatGoalName(target), QuestGoal.GoalType.KillTask, RequiredKills, null);
            Zone zone = WorldMgr.GetRegion(target.RegionId)?.GetZone(target.X, target.Y);
            if (zone != null)
                goal.SetWaypoint(zone.ID, target.X - zone.XOffset, target.Y - zone.YOffset);
        }

        /// <summary>Kills needed on every difficulty: 5 below level 20, then 10, 15 and 20 in the 20s, 30s and 40s.</summary>
        public static int RequiredKillsForLevel(int level) => level switch
        {
            >= 50 => 1,
            >= 40 => 20,
            >= 30 => 15,
            >= 20 => 10,
            _ => 5
        };

        private static string Safe(string value) => (value ?? string.Empty).Replace(';', ',').Replace('=', '-');

        private int ReadInt(string key) => int.TryParse(GetCustomProperty(key), out int value) ? value : 0;
        private byte ReadByte(string key) => byte.TryParse(GetCustomProperty(key), out byte value) ? value : (byte)0;

        private static string GetHuntReason(BountyTargetCandidate target)
        {
            if (target.IsEpic)
                return target.Name switch
                {
                    "Golestandt" => "The dragon Golestandt has cast a shadow over Dartmoor. Bring down the beast before more villages burn.",
                    "Crypt Lord" => "The Crypt Lord commands Caer Sidi's restless dead. Break the tomb's hold on the living.",
                    "Warlord Dorinakka" => "Dorinakka rallies the war-dead inside Caer Sidi. End the warlord's bloody muster.",
                    "Gjalpinulva" => "The dragon Gjalpinulva wakes beneath Malmohus. Midgard calls for hunters bold enough to face her.",
                    "King Tuscar" => "King Tuscar rules the frozen halls with a brutal hand. Carry the fight into Tuscaran Glacier.",
                    "Queen Kula" => "Queen Kula's court has taken too many lives in the glacier. Put an end to her reign.",
                    "Cuuldurach the Glimmer King" => "Cuuldurach's wings darken Sheeroe Hills. The realm asks you to face the Glimmer King.",
                    "Olcasgean" => "Olcasgean bends Galladoria's ancient forces to its will. Break that power at its source.",
                    "Xaga" => "Deep in Galladoria, Xaga twists the wild growth into a deadly snare. Cut it down.",
                    _ => $"A great foe named {target.Name} threatens the realm. Gather your strength and end the danger."
                };

            string name = target.Name.ToLowerInvariant();
            if (HasAny(name, "skeleton", "zombie", "wight", "ghost", "spirit", "spectre", "wraith", "undead"))
                return $"The dead called {target.Name} trouble travelers and will not lie still.";
            if (HasAny(name, "wolf", "boar", "bear", "cat", "spider", "serpent", "hatchling", "dragonfly"))
                return $"The {target.Name} have grown bold along the road and prey upon passersby.";
            if (HasAny(name, "mage", "witch", "sorcer", "warlock", "shaman", "enchant", "faerie", "sprite"))
                return $"The {target.Name} wield dangerous old magic near the settled paths.";
            if (HasAny(name, "bandit", "brigand", "raider", "thief", "outlaw", "pirate"))
                return $"The {target.Name} ambush caravans and leave the roads unsafe.";
            if (HasAny(name, "goblin", "orc", "fomor", "troll", "giant", "ogre"))
                return $"The {target.Name} have gathered in force and threaten nearby folk.";
            return $"The {target.Name} have become a danger to travelers and adventurers.";
        }

        private static bool HasAny(string value, params string[] words) => words.Any(value.Contains);
    }
}
