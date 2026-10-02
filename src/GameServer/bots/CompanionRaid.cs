using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using DOL.GS.PacketHandler;

namespace DOL.GS
{
    public static class CompanionRaid
    {
        private sealed class Session
        {
            public Group Group;
            public GamePlayer Owner;
            public ECSGameTimer Timer;
            public ushort Region;
            public readonly string[] Sent = new string[80];
            public readonly CompanionRaidResurrectionReservations<GameLiving> Resurrection = new();
        }

        private static readonly ConditionalWeakTable<GamePlayer, Session> Sessions = new();

        public static bool Open(GamePlayer player, int capacity = 40)
        {
            if (player?.Level != 50 || capacity is not (40 or 80)) return false;
            Group group = player.Group;
            if (group == null)
            {
                group = new Group(player);
                GroupMgr.AddGroup(group);
                if (!group.AddMember(player)) return false;
            }
            if (!group.EnableCompanionRaid(player, capacity)) return false;
            if (!Sessions.TryGetValue(player, out Session session))
            {
                session = new Session { Owner = player, Group = group };
                Sessions.Add(player, session);
                session.Timer = new ECSGameTimer(player) { Callback = _ => Tick(session) };
                session.Timer.Start(500);
            }
            session.Group = group;
            Array.Clear(session.Sent);
            Update(session);
            Send(player, 2, (byte)capacity);
            return true;
        }

        public static void Close(GamePlayer player)
        {
            if (player == null || !Sessions.TryGetValue(player, out Session session)) return;
            session.Timer.Stop();
            session.Resurrection.Clear();
            Sessions.Remove(player);
            if (player.Client?.ClientState == GameClient.eClientState.Playing)
                Send(player, 3);
        }

        private static int Tick(Session session)
        {
            if (session.Owner.Group != session.Group || session.Owner.ObjectState != GameObject.eObjectState.Active ||
                session.Owner.Client?.ClientState != GameClient.eClientState.Playing)
            {
                Close(session.Owner);
                return 0;
            }
            Update(session);
            return 500;
        }

        private static void Update(Session session)
        {
            if (session.Region != session.Owner.CurrentRegionID)
            {
                session.Region = session.Owner.CurrentRegionID;
                Array.Clear(session.Sent);
            }
            var members = session.Group.GetMembersInTheGroup();
            for (int i = 0; i < session.Group.MaximumMemberCount; i++)
            {
                GameLiving member = i < members.Count ? members[i] : null;
                bool local = member != null && member.CurrentRegionID == session.Owner.CurrentRegionID &&
                             member.ObjectState == GameObject.eObjectState.Active;
                string name = member == null ? "" : (!member.IsAlive ? "[DEAD] " : !local ? "[AWAY] " : "") + member.Name;
                if (name.Length > 31) name = name[..31];
                byte health = member?.IsAlive == true ? member.HealthPercent : (byte)0;
                byte power = member?.ManaPercent ?? 0;
                ushort id = local ? member.ObjectID : (ushort)0;
                string key = $"{id}|{health}|{power}|{name}";
                if (session.Sent[i] == key) continue;
                Send(session.Owner, 1, (byte)i, health, power, id, name);
                session.Sent[i] = key;
            }
        }

        private static void Send(GamePlayer player, byte operation, byte slot = 0,
            byte health = 0, byte power = 0, ushort id = 0, string name = "")
        {
            // Fixed 128-byte body. Native parser validates size, marker, version,
            // index, health/power bounds and the final name terminator before use.
            using var packet = PooledObjectFactory.GetForTick<GSTCPPacketOut>().Init((byte)eServerPackets.DebugMode);
            packet.WriteByte(0);
            packet.WriteByte(0x52);
            packet.WriteByte(1);
            packet.WriteByte(operation);
            packet.WriteByte(slot);
            packet.WriteByte(Math.Min((byte)100, health));
            packet.WriteByte(Math.Min((byte)100, power));
            packet.WriteByte(0);
            packet.WriteByte((byte)id);
            packet.WriteByte((byte)(id >> 8));
            packet.WriteByte(0);
            packet.WriteByte(0);
            byte[] text = Encoding.ASCII.GetBytes(name);
            for (int i = 0; i < 32; i++) packet.WriteByte(i < Math.Min(31, text.Length) ? text[i] : (byte)0);
            for (int i = 44; i < 128; i++) packet.WriteByte(0);
            player.Out.SendTCP(packet);
        }

        public static bool IsMember(GameBot bot) => bot?.Group?.IsCompanionRaid == true &&
            bot.IsTemporaryGroupHelper && !bot.IsAutonomousWorldBot && bot.Group.CompanionRaidOwner == bot.Owner;

        private static bool UnderAttack(GameLiving member) => member.IsBeingInterruptedByOther ||
            GameLoop.GameLoopTime - member.LastAttackedByEnemyTick < 3000 ||
            member.attackComponent.AttackerTracker.Attackers.Any(attacker => attacker.IsAlive && attacker.TargetObject == member);

        private static bool UrgentHealing(GameBot caster) => caster.Group.GetMembersInTheGroup().Any(member =>
            member.CurrentRegionID == caster.CurrentRegionID && (member.IsAlive && member.HealthPercent < 60 ||
                member.ControlledBrain?.Body is GameNPC pet && pet.IsAlive && pet.HealthPercent < 50));

        public static bool ContinueResurrection(GameBot caster)
        {
            if (!IsMember(caster)) return false;
            if (!UnderAttack(caster) && !(caster.CanCastHealSpells && UrgentHealing(caster))) return true;
            caster.StopCurrentSpellcast();
            CancelResurrection(caster);
            return false;
        }

        public static GameLiving ReserveResurrection(GameBot caster, Spell spell)
        {
            if (!IsMember(caster) || !Sessions.TryGetValue(caster.Owner, out Session session)) return null;
            if (caster.IsCasting || UnderAttack(caster) || caster.IsCrowdControlled || caster.IsSilenced)
            {
                // A currently valid resurrection cast retains its reservation.
                if (!caster.IsCasting || UnderAttack(caster)) session.Resurrection.ReleaseCaster(caster);
                return null;
            }
            var members = session.Group.GetMembersInTheGroup();
            bool combat = members.Any(member => member.IsAlive && member.InCombat);
            bool urgent = caster.CanCastHealSpells && UrgentHealing(caster);
            int healers = members.OfType<GameBot>().Count(bot => bot.IsAlive && bot.CanCastHealSpells &&
                !bot.IsCrowdControlled && !bot.IsSilenced && !UnderAttack(bot) && bot.ManaPercent >= 20 &&
                bot.CurrentRegionID == caster.CurrentRegionID);
            if (urgent) { session.Resurrection.ReleaseCaster(caster); return null; }
            int range = caster.castingComponent.CalculateSpellRange(spell);
            foreach (GameLiving corpse in members.Where(member => !member.IsAlive && member.ObjectState == GameObject.eObjectState.Active &&
                member.CurrentRegionID == caster.CurrentRegionID && caster.IsWithinRadius(member, range))
                .OrderBy(member => member == caster.Owner ? 0 : 1).ThenBy(caster.GetDistanceTo))
            {
                if (corpse.TempProperties.GetProperty<GameLiving>("RESURRECT_CASTER") != null) continue;
                // This matches the native resurrection handler's corpse-dependent
                // cost, not Spell.Power (which is not the actual resurrection cost).
                int cost = (int)(caster.MaxMana * Math.Max(0.1f, 0.5f + (corpse.Level-caster.Level)/(float)caster.Level));
                if (caster.Mana < cost) continue;
                if (session.Resurrection.TryReserve(caster, corpse, GameLoop.GameLoopTime, spell.CastTime,
                    true, combat, healers, false, caster.CanCastHealSpells)) return corpse;
            }
            return null;
        }

        public static void CancelResurrection(GameBot caster)
        {
            if (caster?.Owner != null && Sessions.TryGetValue(caster.Owner, out Session session))
                session.Resurrection.ReleaseCaster(caster);
        }
    }
}
