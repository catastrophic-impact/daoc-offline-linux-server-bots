using DOL.GS;
using DOL.GS.Quests;

namespace DOL.AI.Brain
{
    /// <summary>
    /// A classic quest monster's fellows walk its rounds with it and fight beside it (classic-quests.json "Fellows").
    /// Fjall's friends ran with him from the fenrir fort to the Vindsaul gate in the period walkthroughs ("Search out
    /// Fjall and his fellows in Yggdra Forest"). Without their leader in reach (dead, or out on its run when they
    /// respawn) they keep their own spawn like any other monster.
    /// </summary>
    public class QuestFellowBrain : StandardMobBrain
    {
        /// <summary>The leader is joined, and followed, within this distance.</summary>
        public const int LeaderReach = 6000;
        public const int FollowDistance = 120;

        private GameNPC _leader;
        private long _nextLookupTick;

        public QuestFellowBrain() : base()
        {
            FSM.Add(new QuestFellowState_IDLE(this));
            FSM.Add(new QuestFellowState_AGGRO(this));
        }

        /// <summary>The living leader within <see cref="LeaderReach"/>, or null.</summary>
        public GameNPC Leader
        {
            get
            {
                // A respawned leader is the same object, so a lookup is needed only once (or after it is deleted).
                if ((_leader == null || _leader.ObjectState == GameObject.eObjectState.Deleted) &&
                    GameServiceUtils.ShouldTick(_nextLookupTick))
                {
                    _nextLookupTick = GameLoop.GameLoopTime + 10_000;
                    _leader = FindLeader();
                }

                GameNPC leader = _leader;
                return leader != null && leader.IsAlive && leader.ObjectState == GameObject.eObjectState.Active &&
                    leader.CurrentRegion == Body.CurrentRegion && Body.IsWithinRadius(leader, LeaderReach) ? leader : null;
            }
        }

        private GameNPC FindLeader()
        {
            string name = ClassicQuests.FellowLeaderName(Body.InternalID);
            if (string.IsNullOrEmpty(name))
                return null;

            GameNPC best = null;
            int bestDistance = int.MaxValue;
            foreach (GameNPC npc in WorldMgr.GetNPCsByNameFromRegion(name, Body.CurrentRegionID, Body.Realm))
            {
                int distance = npc.SpawnPoint.GetDistanceTo(Body.SpawnPoint);
                if (distance < bestDistance)
                {
                    best = npc;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>The leader's fight is the fellows' fight and theirs is the leader's.</summary>
        public void ShareFight(GameNPC leader)
        {
            if (leader?.Brain is not StandardMobBrain leaderBrain)
                return;
            if (leaderBrain.HasAggro && !HasAggro)
                leaderBrain.AddAggroListTo(this);
            else if (HasAggro && !leaderBrain.HasAggro)
                AddAggroListTo(leaderBrain);
        }
    }

    public class QuestFellowState_IDLE : StandardMobState_IDLE
    {
        private readonly QuestFellowBrain _fellow;

        public QuestFellowState_IDLE(QuestFellowBrain brain) : base(brain)
        {
            _fellow = brain;
        }

        public override void Think()
        {
            GameNPC leader = _fellow.Leader;
            if (leader == null)
            {
                if (_fellow.Body.movementComponent.FollowTarget != null)
                    _fellow.Body.StopFollowing();
                base.Think();
                return;
            }

            _fellow.ShareFight(leader);
            if (_fellow.HasAggro || _fellow.CheckProximityAggro())
            {
                _fellow.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return;
            }

            // Away from its spawn with the leader: stay with the leader instead of walking home.
            if (_fellow.Body.movementComponent.FollowTarget != leader)
                _fellow.Body.Follow(leader, QuestFellowBrain.FollowDistance, QuestFellowBrain.LeaderReach);
        }
    }

    public class QuestFellowState_AGGRO : StandardMobState_AGGRO
    {
        private readonly QuestFellowBrain _fellow;

        public QuestFellowState_AGGRO(QuestFellowBrain brain) : base(brain)
        {
            _fellow = brain;
        }

        public override void Think()
        {
            _fellow.ShareFight(_fellow.Leader);
            base.Think();
        }
    }
}
