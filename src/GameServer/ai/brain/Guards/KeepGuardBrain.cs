using DOL.GS;
using DOL.GS.Keeps;

namespace DOL.AI.Brain
{
    public class KeepGuardBrain : StandardMobBrain
    {
        protected GameKeepGuard _keepGuardBody;

        public override GameNPC Body
        {
            get => _keepGuardBody ?? base.Body;
            set
            {
                _keepGuardBody = value as GameKeepGuard;
                base.Body = value;
            }
        }

        public override int ThinkInterval => 500;

        public KeepGuardBrain() : base()
        {
            FSM.Add(new GuardState_RETURN_TO_SPAWN(this));
        }

        public void SetAggression(int aggroLevel, int aggroRange)
        {
            AggroLevel = aggroLevel;
            AggroRange = aggroRange;
        }

        protected override void CheckPlayerAggro()
        {
            foreach (GamePlayer player in Body.GetPlayersInRadius((ushort) AggroRange))
            {
                if (!CanAggroTarget(player))
                    continue;

                if (Body is not GuardStealther && player.IsStealthed)
                    continue;

                if (player.effectListComponent.ContainsEffectForEffectType(eEffect.Shade))
                    continue;

                WarMapMgr.AddGroup((byte) player.CurrentZone.ID, player.X, player.Y, player.Name, (byte) player.Realm);
                SendAggroLosCheck(player, player);
                // We don't know if the LoS check will be positive, so we have to ask other players
            }
        }

        protected override void CheckNpcAggro()
        {
            foreach (GameNPC npc in Body.GetNPCsInRadius((ushort)AggroRange))
            {
                GameBot autonomous = npc as GameBot ?? (npc.Brain as IControlledBrain)?.GetLivingOwner() as GameBot;
                if (autonomous?.IsAutonomousWorldBot == true)
                {
                    if (!npc.IsAlive || npc.IsStealthed || !CanAggroTarget(npc) ||
                        !PathfindingProvider.Instance.HasLineOfSight(Body.CurrentZone, new(Body.X, Body.Y, Body.Z),
                            new(npc.X, npc.Y, npc.Z), PathfindingProvider.Instance.DefaultFilters)) continue;
                    AddToAggroList(npc);
                    continue;
                }
                // Non-pet NPCs are ignored.
                if (npc is GameKeepGuard || npc.Brain == null || npc.Brain is not IControlledBrain npcBrain)
                    continue;

                GamePlayer player = npcBrain.GetPlayerOwner();

                if (player == null)
                    continue;

                if (!CanAggroTarget(npc))
                    continue;

                WarMapMgr.AddGroup((byte) player.CurrentZone.ID, player.X, player.Y, player.Name, (byte) player.Realm);
                SendAggroLosCheck(player, npc);
                // We don't know if the LoS check will be positive, so we have to ask other players.
            }
        }

        public override bool CanAggroTarget(GameLiving target)
        {
            if (AggroLevel <= 0 || !GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                return false;

            GameBot autonomous = target as GameBot ?? ((target as GameNPC)?.Brain as IControlledBrain)?.GetLivingOwner() as GameBot;
            if (autonomous?.IsAutonomousWorldBot == true)
                return autonomous.Realm != eRealm.None && autonomous.Realm != Body.Realm;

            GamePlayer checkPlayer = null;

            if (target is GameNPC targetNpc && targetNpc.Brain is IControlledBrain targetBrain)
                checkPlayer = targetBrain.GetPlayerOwner();
            else if (target is GamePlayer targetPlayer)
                checkPlayer = targetPlayer;

            if (checkPlayer == null || !GameServer.KeepManager.IsEnemy(_keepGuardBody, checkPlayer, true))
                return false;

            return true;
        }
    }
}
