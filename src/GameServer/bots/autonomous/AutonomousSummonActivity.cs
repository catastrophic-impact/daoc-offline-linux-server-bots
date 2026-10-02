using System;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>One activation handshake; native brains retain behavior and expiration.</summary>
    public static class AutonomousSummonActivity
    {
        private sealed class Activation
        {
            // A released charm body may remain in the world. Do not keep its
            // former controlling brain (and former bot owner) alive with it.
            public WeakReference<DOL.AI.ABrain> Brain;
            public bool Matches(DOL.AI.ABrain brain) => Brain.TryGetTarget(out var current) && ReferenceEquals(current, brain);
        }
        private sealed class Recovery { public long NextScan; }
        private static readonly ConditionalWeakTable<GameNPC, Activation> Activated = new();
        private static readonly ConditionalWeakTable<GameBot, Recovery> Recoveries = new();

        public static GameBot AutonomousOwner(GameNPC actor)
        {
            if (actor is not GameSummonedPet)
            {
                // Charm/tame retains the original GameNPC body. Only the bot's
                // actual registered controlled pet qualifies, not an arbitrary
                // NPC whose brain merely refers to that bot. Charm loss removes
                // this relationship before restoring the creature's wild brain.
                return actor?.Brain is ControlledMobBrain charm &&
                    charm.Owner is GameBot { IsAutonomousWorldBot: true } charmer &&
                    ReferenceEquals(charmer.ControlledBrain, charm) && ReferenceEquals(charm.Body, actor)
                    ? charmer : null;
            }
            GameLiving current = actor;
            for (int depth = 0; depth < 16; depth++)
            {
                if (current is not GameNPC { Brain: IControlledBrain brain }) return null;
                current = brain.Owner;
                if (current is GameBot bot) return bot.IsAutonomousWorldBot ? bot : null;
                if (current == null || ReferenceEquals(current, actor)) return null;
            }
            return null;
        }

        public static bool IsVisibleOrAutonomousSummon(GameNPC actor) =>
            actor.IsVisibleToPlayersOrBots || AutonomousOwner(actor) != null;

        public static bool ActivateOnce(GameNPC pet) => ActivateOnce(pet, false);

        // Native summon handlers assign full health immediately AFTER AddToWorld.
        // Only that successful initial-add path may handshake before the health
        // assignment. Later discovery/recovery must never activate a dead pet.
        internal static bool ActivateAfterWorldAdd(GameSummonedPet pet) => ActivateOnce(pet, true);

        private static bool ActivateOnce(GameNPC pet, bool justAdded)
        {
            if (pet == null) return false;
            DOL.AI.ABrain brain = pet.Brain;
            if (Activated.TryGetValue(pet, out var previous) && previous.Matches(brain)) return false;
            if ((!justAdded && !pet.IsAlive) || pet.ObjectState != GameObject.eObjectState.Active ||
                brain == null || brain.ServiceObjectId.IsPendingRemoval ||
                AutonomousOwner(pet) == null) return false;
            lock (Activated)
            {
                if (Activated.TryGetValue(pet, out previous) && previous.Matches(brain)) return false;
                if (!ReferenceEquals(pet.Brain, brain) || AutonomousOwner(pet) == null) return false;
                pet.OnUpdateOrCreateForPlayer();
                // A normal summon has one brain lifetime. A wild creature may
                // later be legitimately charmed again with a NEW native brain.
                Activated.Remove(pet);
                Activated.Add(pet, new Activation { Brain = new(brain) });
                return true;
            }
        }

        public static void RecoverOwner(GameBot bot)
        {
            if (bot?.IsAutonomousWorldBot != true || !bot.IsAlive ||
                bot.ObjectState != GameObject.eObjectState.Active) return;
            RecoverTree(bot.ControlledBrain?.Body, 0);
            if (!BotAnimistPolicy.AppliesTo(bot)) return;
            var recovery = Recoveries.GetOrCreateValue(bot);
            long now = GameLoop.GameLoopTime;
            if (now < recovery.NextScan) return;
            recovery.NextScan = now + 10_000 + bot.ObjectID % 1000;
            foreach (GameNPC npc in bot.GetNPCsInRadius(4000))
                if (npc is TurretFnfPet turret && AutonomousOwner(turret) == bot)
                    ActivateOnce(turret);
        }

        private static void RecoverTree(GameNPC pet, int depth)
        {
            if (pet == null || depth >= 4 || !pet.IsAlive || pet.ObjectState != GameObject.eObjectState.Active) return;
            ActivateOnce(pet);
            // Bonedancer minions belong to the commander, not directly to the bot.
            if (pet.ControlledNpcList != null)
                foreach (IControlledBrain child in pet.ControlledNpcList)
                    RecoverTree(child?.Body, depth + 1);
        }
    }
}
