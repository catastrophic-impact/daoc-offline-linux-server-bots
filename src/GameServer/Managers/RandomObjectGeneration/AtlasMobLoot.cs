using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DOL.Database;
using DOL.Logging;

namespace DOL.GS {

    /// <summary>
    /// ROGMobGenerator
    /// At the moment this generator only adds ROGs to the loot
    /// </summary>
    public class ROGMobGenerator : LootGeneratorBase {

        //base chance in %
        public static ushort BASE_ROG_CHANCE = 14;
        private static readonly Logger Log = LoggerManager.Create(typeof(ROGMobGenerator));
        private static long _nextErrorLogTick;


        /// <summary>
        /// Generate loot for given mob
        /// </summary>
        /// <param name="mob"></param>
        /// <param name="killer"></param>
        /// <returns></returns>
        public override LootList GenerateLoot(GameNPC mob, GameObject killer)
        {
            LootList loot = base.GenerateLoot(mob, killer);

            try
            {
                // BotBrain implements IControlledBrain but is NOT ControlledMobBrain.
                // More importantly a persistent bot's class pet has no human owner.
                // Resolve the actual player-like owner before any class/realm rolls.
                GameLiving owner = ResolveEquipmentLootOwner(killer);
                if (owner == null)
                {
                    return loot;
                }

                int killedcon = owner.GetConLevel(mob);

                //grey con dont drop loot
                if (killedcon <= -3)
                {
                    return loot;
                }

                eCharacterClass classForLoot = GetLootClass(owner);
                // allow the leader to decide the loot realm
                if (owner.Group?.LivingLeader is GameLiving leader && IsLootParticipant(leader))
                    owner = leader;

                // chance to get a RoG Item
                int chance = BASE_ROG_CHANCE + ((killedcon < 0 ? killedcon + 1 : killedcon) * 3);

                //chance = 100;
                
                BattleGroup bg = (owner as GamePlayer)?.TempProperties.GetProperty<BattleGroup>(BattleGroup.BATTLEGROUP_PROPERTY);

                if (bg != null)
                {
                    var maxDropCap = bg.PlayerCount / 50;
                    if (maxDropCap < 1) maxDropCap = 1;
                    if (mob is GameEpicNPC)
                        maxDropCap *= 2;
                    chance = 2;

                    int numDrops = 0;
                    foreach (GamePlayer bgMember in bg.Members.Keys)
                    {
                        if(bgMember.GetDistance(owner) > WorldMgr.VISIBILITY_DISTANCE)
                            continue;
                        
                        if (RollDropChance(chance) && numDrops < maxDropCap)
                        {
                            classForLoot = GetRandomClassFromBattlegroup(bg);
                            var item = GenerateItemTemplate(owner, classForLoot, (byte)(mob.Level + 1), killedcon);
                            loot.AddFixed(item, 1);
                            numDrops++;
                        }
                    }
                }
                //players below level 50 will always get loot for their class, 
                //or a valid class for one of their groupmates
                else if (owner.Group != null)
                {
                    var MaxDropCap = Math.Round((decimal) (owner.Group.MemberCount)/3);
                    if (MaxDropCap < 1) MaxDropCap = 1;
                    if (MaxDropCap > 3) MaxDropCap = 3;
                    if (mob.Level > 65) MaxDropCap++; //increase drop cap beyond lvl 60
                    int guaranteedDrop = mob.Level > 67 ? 1 : 0; //guarantee a drop for very high level mobs
                    
                    if (mob.Level > 27)
                        chance -= 3;

                    if (mob.Level > 40)
                        chance -= 3;

                    int numDrops = 0;
                    //roll for an item for each player in the group
                    foreach (GameLiving groupPlayer in GetLootParticipants(owner.Group))
                    {
                        if(groupPlayer.GetDistance(owner) > WorldMgr.VISIBILITY_DISTANCE)
                            continue;
                        
                        if (RollDropChance(chance) && numDrops < MaxDropCap)
                        {
                            classForLoot = GetRandomClassFromGroup(owner.Group);
                            var item = GenerateItemTemplate(owner, classForLoot, (byte)(mob.Level + 1), killedcon);
                            loot.AddFixed(item, 1);
                            numDrops++;
                        }
                    }

                    //if we're under the cap, add in the guaranteed drop
                    if (numDrops < MaxDropCap && guaranteedDrop > 0)
                    {
                        classForLoot = GetRandomClassFromGroup(owner.Group);
                        var item = GenerateItemTemplate(owner, classForLoot, (byte)(mob.Level + 1), killedcon);
                        loot.AddFixed(item, 1);
                    }

                    //classForLoot = GetRandomClassFromGroup(player.Group);
                    //chance += 4 * player.Group.GetPlayersInTheGroup().Count; //4% extra drop chance per group member
                }
                else
                {
                    int tmpChance = owner is GamePlayer player ? Math.Max(0, player.OutOfClassROGPercent) : 0;
                    if (owner.Level == 50 && Util.Chance(tmpChance))
                    {
                        classForLoot = GetRandomClassFromRealm(owner.Realm);
                    }

                    chance += 10; //solo drop bonus
                    
                    if (RollDropChance(chance))
                    {
                        DbItemTemplate item = GenerateItemTemplate(owner, classForLoot, (byte)(mob.Level + 1), killedcon, false);
                        loot.AddFixed(item, 1);
                    }
                    //tmp.GenerateItemQuality(killedcon);
                    //tmp.CapUtility(mob.Level + 1);
                    
                    /*
                    if (mob.Level < 5)
                    {
                        chance += 50;
                        loot.AddRandom(chance, item, 1);
                    }
                    else if (mob.Level < 10)
                        loot.AddRandom(chance + (100 - mob.Level * 10), item, 1);
                    //25% bonus drop rate at lvl 5, down to normal chance at level 10
                    else
                        loot.AddRandom(chance, item, 1);
                        */
                }

                //chance = 100;

               

            }
            catch (Exception exception)
            {
                // Do not hide a generator failure as an unlucky roll forever.
                // One global error per minute, no per-kill disk/database traffic.
                long now = Environment.TickCount64;
                long next = Interlocked.Read(ref _nextErrorLogTick);
                if (Log.IsErrorEnabled && now >= next && Interlocked.CompareExchange(ref _nextErrorLogTick, now + 60_000, next) == next)
                    Log.Error($"ROG_LOOT_FAILED mob=\"{mob?.Name}\" killer=\"{killer?.Name}\" killerType={killer?.GetType().Name}", exception);
                return loot;
            }

            return loot;
        }


        protected virtual bool RollDropChance(int chance)
        {
            return Util.Chance(chance);
        }

        public static GameLiving ResolveEquipmentLootOwner(GameObject killer)
        {
            GameLiving owner = ResolveLootOwner(killer);
            // Helpers never own loot; their real owner may receive the kill's
            // normal loot through the existing reward-owner/pickup path.
            if (owner is GameBot { IsTemporaryGroupHelper: true } helper)
                owner = helper.Owner;
            return IsLootParticipant(owner) ? owner : null;
        }

        private static bool IsLootParticipant(GameLiving living)
        {
            return living is GamePlayer || living is GameBot { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false };
        }

        private static IEnumerable<GameLiving> GetLootParticipants(Group group)
        {
            return group.GetMembersInTheGroup().Where(IsLootParticipant);
        }

        private static eCharacterClass GetLootClass(GameLiving participant)
        {
            // IGamePlayer is the bot adapter, not an interface on GamePlayer.
            return (eCharacterClass)(participant is GamePlayer player
                ? player.CharacterClass.ID : ((GameBot)participant).CharacterClass.ID);
        }

        protected virtual DbItemTemplate GenerateItemTemplate(GameLiving owner, eCharacterClass classForLoot, byte lootLevel, int killedcon, bool adjustQuality = true)
        {
            DbItemTemplate item = null;
                
                
            GeneratedUniqueItem tmp = AtlasROGManager.GenerateMonsterLootROG(owner.Realm, classForLoot, lootLevel, owner.CurrentZone?.IsOF ?? false);
            if (adjustQuality)
                tmp.GenerateItemQuality(killedcon);
            //tmp.CapUtility(mob.Level + 1);
            item = tmp;
            item.MaxCount = 1;

            return item;
        }

        private eCharacterClass GetRandomClassFromGroup(Group group)
        {
            List<eCharacterClass> validClasses = new List<eCharacterClass>();

            foreach (GameLiving member in GetLootParticipants(group))
            {
                validClasses.Add(GetLootClass(member));
            }
            eCharacterClass ranClass = validClasses[Util.Random(validClasses.Count - 1)];

            return ranClass;
        }
        
        private eCharacterClass GetRandomClassFromBattlegroup(BattleGroup battlegroup)
        {
            List<eCharacterClass> validClasses = new List<eCharacterClass>();

            foreach (GamePlayer player in battlegroup.Members.Keys)
            {
                validClasses.Add((eCharacterClass)player.CharacterClass.ID);
            }
            eCharacterClass ranClass = validClasses[Util.Random(validClasses.Count - 1)];

            return ranClass;
        }

        private eCharacterClass GetRandomClassFromRealm(eRealm realm)
        {
            List<eCharacterClass> classesForRealm = new List<eCharacterClass>();
            switch (realm)
            {
                case eRealm.Albion:
                    classesForRealm.Add(eCharacterClass.Armsman);
                    classesForRealm.Add(eCharacterClass.Cabalist);
                    classesForRealm.Add(eCharacterClass.Cleric);
                    classesForRealm.Add(eCharacterClass.Friar);
                    classesForRealm.Add(eCharacterClass.Infiltrator);
                    classesForRealm.Add(eCharacterClass.Mercenary);
                    classesForRealm.Add(eCharacterClass.Necromancer);
                    classesForRealm.Add(eCharacterClass.Paladin);
                    classesForRealm.Add(eCharacterClass.Reaver);
                    classesForRealm.Add(eCharacterClass.Scout);
                    classesForRealm.Add(eCharacterClass.Sorcerer);
                    classesForRealm.Add(eCharacterClass.Theurgist);
                    classesForRealm.Add(eCharacterClass.Wizard);
                    break;
                case eRealm.Midgard:
                    classesForRealm.Add(eCharacterClass.Berserker);
                    classesForRealm.Add(eCharacterClass.Bonedancer);
                    classesForRealm.Add(eCharacterClass.Healer);
                    classesForRealm.Add(eCharacterClass.Hunter);
                    classesForRealm.Add(eCharacterClass.Runemaster);
                    classesForRealm.Add(eCharacterClass.Savage);
                    classesForRealm.Add(eCharacterClass.Shadowblade);
                    classesForRealm.Add(eCharacterClass.Skald);
                    classesForRealm.Add(eCharacterClass.Spiritmaster);
                    classesForRealm.Add(eCharacterClass.Thane);
                    classesForRealm.Add(eCharacterClass.Warrior);
                    break;
                case eRealm.Hibernia:
                    classesForRealm.Add(eCharacterClass.Animist);
                    classesForRealm.Add(eCharacterClass.Bard);
                    classesForRealm.Add(eCharacterClass.Blademaster);
                    classesForRealm.Add(eCharacterClass.Champion);
                    classesForRealm.Add(eCharacterClass.Druid);
                    classesForRealm.Add(eCharacterClass.Eldritch);
                    classesForRealm.Add(eCharacterClass.Enchanter);
                    classesForRealm.Add(eCharacterClass.Hero);
                    classesForRealm.Add(eCharacterClass.Mentalist);
                    classesForRealm.Add(eCharacterClass.Nightshade);
                    classesForRealm.Add(eCharacterClass.Ranger);
                    classesForRealm.Add(eCharacterClass.Valewalker);
                    classesForRealm.Add(eCharacterClass.Warden);
                    break;
            }

            return classesForRealm[Util.Random(classesForRealm.Count - 1)];
        }
    }
}
