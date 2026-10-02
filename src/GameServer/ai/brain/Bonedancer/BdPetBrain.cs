using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS;

namespace DOL.AI.Brain
{
    public abstract class BdPetBrain : ControlledMobBrain
    {
        protected const int BASEFORMATIONDIST = 50;

        public BdPetBrain(GameLiving Owner) : base(Owner)
        {
            IsMainPet = false;
        }

        /// <summary>
        /// Are minions assisting the commander?
        /// </summary>
        public bool MinionsAssisting => Owner is CommanderPet commander && commander.MinionsAssisting;

        protected override GameLiving CalculateNextAttackTarget()
        {
            return MinionsAssisting ? Owner.TargetObject as GameLiving : base.CalculateNextAttackTarget();
        }

        public override void SetAggressionState(eAggressionState state)
        {
            if (MinionsAssisting)
                base.SetAggressionState(state);
            else
                base.SetAggressionState(eAggressionState.Passive);

            // Attack immediately rather than waiting for the next Think()
            if (AggressionState is not eAggressionState.Passive)
                Attack(Owner.TargetObject);
        }

        public override void OnAttackedByEnemy(AttackData ad)
        {
            // Any attack on a subpet is handled as if it was the commander that was attacked.
            // This will propagate the event to every subpet.
            if (ad.CausesCombat && Owner is CommanderPet owner && owner.Brain is CommanderBrain ownerBrain)
                ownerBrain.OnAttackedByEnemy(ad);
        }

        public override void UpdatePetWindow() { }

        /// <summary>
        /// The Bonedancer is one ownership step above the commander.  Resolve
        /// that living directly instead of GetPlayerOwner(), which intentionally
        /// skips a temporary GameBot and returns its human group leader.
        /// </summary>
        protected GameLiving BonedancerOwner => Owner is GameNPC
            {
                Brain: IControlledBrain commanderBrain
            }
                ? commanderBrain.Owner
                : GetLivingOwner();

        /// <summary>
        /// Bonedancer support minions use ordinary asynchronous NPC casts. Do
        /// not let their brain submit another defensive spell while the first
        /// request is waiting to start or its cast bar is still running. Without
        /// this gate the Patroller alternates its haste and damage shield every
        /// Think tick and neither cast is allowed to finish.
        /// </summary>
        public override bool CheckSpells(eCheckSpellType type)
        {
            if (Body?.IsCasting == true || Body?.castingComponent?.HasPendingSkillRequests == true ||
                Body?.castingComponent is NpcCastingComponent { HasPendingLosCheckRequests: true })
                return true;

            return base.CheckSpells(type);
        }

        /// <summary>
        /// Bonedancer support minions already select only alive, same-realm,
        /// same-region targets inside the spell's real range. Requiring a
        /// nearby human client to answer an NPC LoS packet makes Patrollers and
        /// Menders work differently when observed and leaves them endlessly
        /// resubmitting the same buff while no player is present. Their finite
        /// support casts therefore use the server-side target/range checks.
        /// </summary>
        public static bool UsesPlayerClientLosForSupportCasts => false;

        protected override bool ShouldCheckLosForDefensiveSpell(Spell spell, GameLiving target) =>
            UsesPlayerClientLosForSupportCasts;

        /// <summary>
        /// Builds the finite, in-range support roster for Bonedancer healer and
        /// buffer minions. It contains the Bonedancer's real group (GamePlayer
        /// and GameBot members), the commander tree and party pets, but never a
        /// distant/offline living that would make the minion retry forever.
        /// </summary>
        protected IReadOnlyList<GameLiving> BonedancerSupportTargets(Spell spell, bool includePets)
        {
            if (spell == null)
                return [];

            int range = Math.Max(0, spell.CalculateEffectiveRange(Body));
            var targets = new List<GameLiving>(16);
            var seen = new HashSet<GameLiving>();

            void Add(GameLiving living)
            {
                if (living == null || !seen.Add(living) || !living.IsAlive ||
                    living.ObjectState is not GameObject.eObjectState.Active ||
                    living.CurrentRegion != Body.CurrentRegion ||
                    !GameServer.ServerRules.IsSameRealm(Body, living, true) ||
                    living != Body && (range <= 0 || !Body.IsWithinRadius(living, range)))
                    return;
                targets.Add(living);
            }

            void AddPetTree(GameLiving living)
            {
                IControlledBrain main = living?.ControlledBrain;
                if (main?.Body == null)
                    return;
                Add(main.Body);
                if (main.Body.ControlledNpcList == null)
                    return;
                foreach (IControlledBrain child in main.Body.ControlledNpcList)
                    Add(child?.Body);
            }

            GameLiving bonedancer = BonedancerOwner;
            IEnumerable<GameLiving> group = bonedancer?.Group?.GetMembersInTheGroup() ??
                                             (bonedancer == null ? [] : [bonedancer]);
            foreach (GameLiving member in group)
                Add(member);

            Add(Owner);
            Add(Body);
            if (Owner is GameNPC commander && commander.ControlledNpcList != null)
                foreach (IControlledBrain sibling in commander.ControlledNpcList)
                    Add(sibling?.Body);

            if (includePets)
                foreach (GameLiving member in group)
                    AddPetTree(member);

            return targets;
        }

        /// <summary>
        /// Shards of Bone belongs only on this Bonedancer's commander tree:
        /// the Patroller itself, its commander, and the commander's other
        /// subpets. It must not leak onto unrelated group members or their pets.
        /// </summary>
        protected IReadOnlyList<GameLiving> BonedancerCommanderTreeTargets(Spell spell)
        {
            if (spell == null)
                return [];

            int range = Math.Max(0, spell.CalculateEffectiveRange(Body));
            var targets = new List<GameLiving>(4);
            var seen = new HashSet<GameLiving>();

            void Add(GameLiving living)
            {
                if (living == null || !seen.Add(living) || !living.IsAlive ||
                    living.ObjectState is not GameObject.eObjectState.Active ||
                    living.CurrentRegion != Body.CurrentRegion ||
                    !GameServer.ServerRules.IsSameRealm(Body, living, true) ||
                    living != Body && (range <= 0 || !Body.IsWithinRadius(living, range)))
                    return;

                targets.Add(living);
            }

            Add(Body);
            Add(Owner);

            if (Owner is GameNPC commander && commander.ControlledNpcList != null)
                foreach (IControlledBrain sibling in commander.ControlledNpcList)
                    Add(sibling?.Body);

            return targets;
        }

        public override void FollowOwner()
        {
            // A commander can refresh sub-pet formation orders every think tick.
            // Never let that restart movement while an asynchronous spell request
            // is pending or a cast bar is running; doing so made long Bone Mage
            // and support casts appear to cancel forever.
            if (Body.IsCasting || Body.castingComponent.HasPendingSkillRequests ||
                Body.castingComponent is NpcCastingComponent { HasPendingLosCheckRequests: true })
            {
                if (Body.IsMoving)
                    Body.StopMoving();
                return;
            }

            if (Body.IsAttacking)
                Disengage();

            Body.Follow(Owner, MIN_OWNER_FOLLOW_DIST, MAX_OWNER_FOLLOW_DIST);
        }

        public override bool CheckFormation(ref int x, ref int y, ref int z)
        {
            if (Body.IsCasting || Body.castingComponent.HasPendingSkillRequests ||
                Body.attackComponent.AttackState || Body.attackComponent.AttackerTracker.Count != 0)
                return false;

            GameNPC commander = (GameNPC) Owner;
            double heading = commander.Heading * Point2D.HEADING_TO_RADIAN;
            int i = 0;

            // How much do we want to slide back and left/right.
            int perp_slide = 0;
            int par_slide = 0;

            for (; i < commander.ControlledNpcList.Length; i++)
            {
                if (commander.ControlledNpcList[i] == this)
                    break;
            }

            switch (commander.Formation)
            {
                case GameNPC.eFormationType.Triangle:
                {
                    par_slide = BASEFORMATIONDIST;
                    perp_slide = BASEFORMATIONDIST;

                    if (i != 0)
                        par_slide = BASEFORMATIONDIST * 2;

                    break;
                }
                case GameNPC.eFormationType.Line:
                {
                    par_slide = BASEFORMATIONDIST * (i + 1);
                    break;
                }
                case GameNPC.eFormationType.Protect:
                {
                    switch (i)
                    {
                        case 0:
                        {
                            par_slide = -BASEFORMATIONDIST * 2;
                            break;
                        }
                        case 1:
                        case 2:
                        {
                            par_slide = -BASEFORMATIONDIST;
                            perp_slide = BASEFORMATIONDIST;
                            break;
                        }
                    }

                    break;
                }
            }

            // Slide backwards.
            x += (int) ((double) commander.FormationSpacing * par_slide * Math.Cos(heading - Math.PI / 2));
            y += (int) ((double) commander.FormationSpacing * par_slide * Math.Sin(heading - Math.PI / 2));

            // In addition with sliding backwards, slide the other two pets sideways.
            switch (i)
            {
                case 1:
                {
                    x += (int) ((double) commander.FormationSpacing * perp_slide * Math.Cos(heading - Math.PI));
                    y += (int) ((double) commander.FormationSpacing * perp_slide * Math.Sin(heading - Math.PI));
                    break;
                }
                case 2:
                {
                    x += (int) ((double) commander.FormationSpacing * perp_slide * Math.Cos(heading));
                    y += (int) ((double) commander.FormationSpacing * perp_slide * Math.Sin(heading));
                    break;
                }
            }

            return true;
        }

        public override eWalkState WalkState
        {
            get => eWalkState.Follow;
            set { }
        }
    }
}
