using System;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>Shared persistent/temporary bot ranged policy; never grants ammo to a human inventory.</summary>
    public static class BotRangedCombat
    {
        public static bool IsRangedWeaponType(eObjectType type) => type is
            eObjectType.Fired or eObjectType.Longbow or eObjectType.CompositeBow or
            eObjectType.RecurvedBow or eObjectType.Crossbow or eObjectType.Thrown;

        public static bool IsUsableWeapon(DbInventoryItem item) => item != null && IsUsableTemplate(item.Template);

        public static bool IsUsableTemplate(DbItemTemplate item) => item != null &&
            IsRangedWeaponType((eObjectType)item.Object_Type) && item.Item_Type == Slot.RANGED &&
            item.DPS_AF > 0 && item.SPD_ABS > 0 && item.Model > 0;

        public static string AbilityFor(eObjectType type) => type switch
        {
            eObjectType.Fired => "Weaponry: Shortbows",
            eObjectType.Longbow => "Weaponry: Longbows",
            eObjectType.CompositeBow => "Weaponry: Composite Bows",
            eObjectType.RecurvedBow => "Weaponry: Recurved Bows",
            eObjectType.Crossbow => "Weaponry: Crossbow",
            eObjectType.Thrown => "Weaponry: Thrown",
            _ => string.Empty,
        };

        public static bool CanUse(GameBot bot, DbInventoryItem item) => IsUsableWeapon(item) &&
            item.LevelRequirement <= bot.Level && bot.HasAbilityToUseItem(item.Template);

        public static bool NeedsAttackStart(bool attacking, GameObject previousTarget, GameObject target) =>
            !attacking || previousTarget != target;

        public static bool IsDedicatedArcher(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Scout or eCharacterClass.Hunter or eCharacterClass.Ranger;

        /// <summary>
        /// Persistent autonomous archers own their bow positioning themselves,
        /// including when a real player temporarily leads their group.
        /// Temporary /spawn companions retain the established companion
        /// movement path. Letting the generic NPC attack starter attach its
        /// melee-distance Follow order makes an autonomous archer stop, draw,
        /// chase, and restart forever without releasing the shot.
        /// </summary>
        public static bool UsesAutonomousBowPositioning(
            bool autonomousWorldBot,
            bool temporaryGroupHelper,
            bool playerLedGroup,
            eCharacterClass characterClass) =>
            autonomousWorldBot && !temporaryGroupHelper &&
            IsDedicatedArcher(characterClass);

        public static bool UsesAutonomousBowPositioning(GameNPC npc) =>
            npc is GameBot bot && bot.CharacterClass != null &&
            UsesAutonomousBowPositioning(bot.IsAutonomousWorldBot,
                bot.IsTemporaryGroupHelper, bot.IsPlayerLedGroup,
                (eCharacterClass)bot.CharacterClass.ID);

        /// <summary>
        /// The native NPC attack action may opportunistically change to a
        /// distance-slot weapon. Ordinary NPC templates retain that behavior,
        /// but a GameBot may do it only for a dedicated archer class. Otherwise
        /// a Warrior/Shadowblade carrying real throwing loot oscillates between
        /// the bot brain's melee selection and the NPC action's ranged selection
        /// without ever completing either attack.
        /// </summary>
        public static bool AllowsAutomaticNpcRangedSwitch(
            bool isGameBot,
            eCharacterClass characterClass,
            bool canUseRangedWeapon) =>
            !isGameBot || canUseRangedWeapon && IsDedicatedArcher(characterClass);

        /// <summary>
        /// A successful instant Beastcraft/utility spell is allowed in the
        /// middle of an NPC-backed bow cycle. Only a real cast in progress may
        /// cancel the draw. Repeatedly stopping for instant spells resets Aim
        /// to None and produces an endless prepare animation without a shot.
        /// </summary>
        public static bool PreserveDrawAfterInstantSpell(
            bool spellAction,
            bool casting,
            bool pendingCast,
            eActiveWeaponSlot activeSlot,
            eRangedAttackState rangedState) =>
            spellAction && !casting && !pendingCast &&
            activeSlot == eActiveWeaponSlot.Distance &&
            rangedState != eRangedAttackState.None;

        /// <summary>
        /// Once a dedicated archer has begun a real bow cycle, that cycle owns
        /// the action until the shot releases or the native attack component
        /// stops it. Re-running the general spell selector during Aim can queue
        /// a Beastcraft/path spell and reset the NPC-backed draw indefinitely.
        /// Buff and pet maintenance resume naturally between combats.
        /// </summary>
        public static bool BowCycleOwnsAction(
            eCharacterClass characterClass,
            bool attackState,
            eActiveWeaponSlot activeSlot,
            eRangedAttackState rangedState) =>
            IsDedicatedArcher(characterClass) && attackState &&
            activeSlot == eActiveWeaponSlot.Distance;

        /// <summary>
        /// A queued or aiming bow attack is an asynchronous action. The bot
        /// brain may pulse several times before the attack service releases the
        /// arrow; those pulses must not reselect the target, issue a follow, or
        /// restart the attack. Native range/LOS checks still stop the shot, and
        /// a real close-range attacker still permits the normal melee fallback.
        /// </summary>
        public static bool BowDrawOwnsDecision(
            eCharacterClass characterClass,
            bool attackState,
            eActiveWeaponSlot activeSlot,
            bool targetAlive,
            bool targetInRangedRange,
            bool closeMeleePressure,
            bool groupProtectionRequired) =>
            IsDedicatedArcher(characterClass) && attackState &&
            activeSlot == eActiveWeaponSlot.Distance && targetAlive &&
            targetInRangedRange && !closeMeleePressure && !groupProtectionRequired;

        /// <summary>
        /// A Hunter's ordinary pet buffs are maintenance, not part of its
        /// ranged rotation. Once either the owner or its pet has a live combat
        /// target, the owner must finish bow attacks instead of temporarily
        /// selecting the pet and starting another cast. Pet commands still run
        /// from AttackMostWanted, and upkeep resumes after combat.
        /// </summary>
        public static bool CombatOwnsArcherPetUpkeep(
            eCharacterClass characterClass,
            bool hasLiveCombatTarget,
            bool ownerInCombat,
            bool ownerAttacking,
            bool bowAttackActive) =>
            IsDedicatedArcher(characterClass) &&
            (hasLiveCombatTarget || ownerInCombat || ownerAttacking || bowAttackActive);

        /// <summary>
        /// Decides whether the bot AI should select its distance slot for this
        /// attack. Auto stance uses bows only for the three dedicated archer
        /// classes. Other classes may retain real throwing/crossbow inventory,
        /// but never select it through bot combat AI; spell-capable hybrids can
        /// still pull through the earlier offensive-spell path. Merely owning a
        /// distance item must not turn a Warrior into a ranged bot.
        /// </summary>
        public static bool ShouldUseRangedWeapon(
            eCharacterClass characterClass,
            eBotStance stance,
            bool targetInMeleeRange,
            bool hasUsableRangedWeapon)
        {
            if (!hasUsableRangedWeapon || !IsDedicatedArcher(characterClass))
                return false;

            return stance == eBotStance.Ranged ||
                   stance == eBotStance.Auto && !targetInMeleeRange;
        }

        public static bool ShouldCloseToRangedRange(
            bool useRangedWeapon,
            int distanceToTarget,
            int rangedAttackRange) =>
            useRangedWeapon && rangedAttackRange > 0 && distanceToTarget > rangedAttackRange;

        public static int BowApproachDistance(int meleeAttackRange, int rangedAttackRange) =>
            Math.Max(meleeAttackRange + 100, rangedAttackRange - 100);

        // The shared RoG generator has no Thrown case: it creates a nameless,
        // model-zero item with no DPS or delay. Correct only bot-created items.
        // Installed bronze/iron/... throwing axes use model 333 and delay 20.
        public static DbItemTemplate CreateThrowingWeapon(eRealm realm, eCharacterClass characterClass, byte level) => new()
        {
            Id_nb = $"bot_throwing_{Guid.NewGuid():N}",
            Name = characterClass == eCharacterClass.Shadowblade ? "throwing knives" : "throwing axes",
            Model = characterClass == eCharacterClass.Shadowblade ? 1 : 333,
            Realm = (int)realm, Level = level, LevelRequirement = level,
            Object_Type = (int)eObjectType.Thrown, Item_Type = Slot.RANGED,
            Type_Damage = (int)eDamageType.Slash, DPS_AF = 12 + 3 * level, SPD_ABS = 20,
            Quality = level == 50 ? 99 : 95, Condition = 50000, MaxCondition = 50000,
            Durability = 50000, MaxDurability = 50000, IsPickable = true,
            IsDropable = false, IsTradable = false, MaxCount = 1,
        };

        public static DbItemTemplate CreateStarter(eRealm realm, eCharacterClass characterClass, eObjectType type)
        {
            if (!IsRangedWeaponType(type)) return null;
            DbItemTemplate appearance = type == eObjectType.Thrown
                ? CreateThrowingWeapon(realm, characterClass, 1)
                : new GeneratedUniqueItem(false, realm, characterClass, 1, type, eInventorySlot.DistanceWeapon);
            // A basic level-one starting weapon, not a free level-scaled RoG
            // upgrade. Copy only weapon fields; no magical bonuses or procs.
            return new DbItemTemplate
            {
                Id_nb = $"bot_starter_ranged_{(int)realm}_{(int)type}", Name = $"training {type}",
                Realm = (int)realm, Level = 1, LevelRequirement = 1,
                Object_Type = (int)type, Item_Type = Slot.RANGED, Model = appearance.Model,
                Type_Damage = appearance.Type_Damage, DPS_AF = 15,
                SPD_ABS = type == eObjectType.Thrown ? 20 : type == eObjectType.Crossbow ? 33 : 40,
                Quality = 85, Condition = 50000, MaxCondition = 50000,
                Durability = 50000, MaxDurability = 50000, IsPickable = true,
                IsDropable = false, IsTradable = false, MaxCount = 1,
            };
        }

        public static bool EnsureStarter(GameBot bot)
        {
            if (bot.Inventory == null || bot.CharacterClass == null) return false;
            DbInventoryItem existing = bot.Inventory.GetItem(eInventorySlot.DistanceWeapon);
            if (existing != null)
            {
                // Repair malformed weapon fields only. Keep the same inventory
                // object/slot even with a full bag; never touch an instrument or
                // overwrite a valid weapon earned through loot or purchases.
                eObjectType existingType = (eObjectType)existing.Object_Type;
                if (IsRangedWeaponType(existingType))
                {
                    if (IsUsableWeapon(existing) || !bot.HasAbilityToUseItem(existing.Template)) return false;
                    DbItemTemplate basic = CreateStarter(bot.Realm, (eCharacterClass)bot.CharacterClass.ID, existingType);
                    // A shared item-template must not change any other character.
                    if (existing.Template is not DbItemUnique)
                    {
                        existing.Template = new DbItemUnique(existing.Template);
                        existing.ITemplate_Id = null;
                        existing.UTemplate_Id = existing.Template.Id_nb;
                    }
                    if (existing.DPS_AF <= 0) existing.DPS_AF = basic.DPS_AF;
                    if (existing.SPD_ABS <= 0) existing.SPD_ABS = basic.SPD_ABS;
                    if (existing.Model <= 0) existing.Model = basic.Model;
                    if (existing.Type_Damage <= 0) existing.Type_Damage = basic.Type_Damage;
                    existing.Item_Type = Slot.RANGED;
                    existing.Dirty = true;
                    return true;
                }

                // A malformed distance slot (commonly a staff left by an old
                // generator) must not block a Hunter's bow.  Preserve the real
                // item by moving it to a backpack slot; never delete or merge
                // it, and leave it untouched when the backpack is genuinely full.
                eInventorySlot displaced = bot.Inventory.FindFirstEmptySlot(
                    eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
                if (displaced == eInventorySlot.Invalid ||
                    !bot.Inventory.MoveItem(eInventorySlot.DistanceWeapon, displaced, Math.Max(1, existing.Count)))
                    return false;
            }

            eObjectType realmBow = bot.Realm == eRealm.Midgard ? eObjectType.CompositeBow :
                bot.Realm == eRealm.Hibernia ? eObjectType.RecurvedBow : eObjectType.Longbow;
            foreach (eObjectType type in new[] { realmBow, eObjectType.Longbow, eObjectType.CompositeBow,
                eObjectType.RecurvedBow, eObjectType.Crossbow, eObjectType.Fired, eObjectType.Thrown })
            {
                DbItemTemplate starter = CreateStarter(bot.Realm, (eCharacterClass)bot.CharacterClass.ID, type);
                if (!bot.HasAbilityToUseItem(starter)) continue;
                var item = GameInventoryItem.Create(new DbItemUnique(starter));
                item.Creator = nameof(BotRangedCombat);
                return bot.Inventory.AddItem(eInventorySlot.DistanceWeapon, item);
            }
            return false;
        }
    }
}
