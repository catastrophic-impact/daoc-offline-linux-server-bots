using System;
using System.Collections;
using DOL.AI.Brain;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;

namespace DOL.GS.Quests.Hibernia;

/// <summary>
/// Stationary, ten-minute service NPCs earned from the Sluaghbinder epic
/// quests.  They deliberately use ordinary NPC interaction classes rather
/// than GameSummonedPet, so they do not follow, attack, move, or consume the
/// normal combat-pet slot.
/// </summary>
public static class SluaghbinderEpicServices
{
    private const int LifetimeMilliseconds = 10 * 60 * 1000;

    public static void Spawn(GamePlayer player, int spellId)
    {
        if (player == null || player.CurrentRegion == null)
            return;

        GameNPC service = spellId switch
        {
            59080 => Configure(player, new SluaghbinderEpicMerchant(), "Skeletal Merchant", "<Merchant>", 24),
            59081 => Configure(player, new SluaghbinderEpicHastener(), "Skeletal Hastener", "<Hastener>", 25),
            59082 => Configure(player, new SluaghbinderEpicHealer(), "Skeletal Healer", "<Healer>", 108),
            // 938 is the charred-skeletal silhouette.  It is deliberately
            // different from the healer (108), merchant (24), hastener (25),
            // and exchanger (2213) service models.
            59083 => Configure(player, new SluaghbinderEpicTeleporter(), "Skeletal Teleporter", "<Teleporter>", 938),
            59084 => Configure(player, new RealmExchangeBroker(), "Skeletal Tradesman", "<Realm Exchange>", 2213),
            _ => null,
        };

        if (service == null)
            return;

        service.AddToWorld();
        new ECSGameTimer(service, new ECSGameTimer.ECSTimerCallback(_ =>
        {
            if (service.ObjectState == GameObject.eObjectState.Active)
                service.Delete();
            return 0;
        }), LifetimeMilliseconds);

        player.Out.SendMessage(
            $"{service.Name} answers your call and remains for ten minutes. It is stationary and will not fight or follow.",
            eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private static T Configure<T>(GamePlayer player, T service, string name, string tag, ushort model)
        where T : GameNPC
    {
        // Replace the constructor's default StandardMobBrain before placing
        // the NPC.  Doing this while it is still at its zeroed spawn point
        // avoids any pre-world brain-stop correction and guarantees that the
        // service can never patrol, acquire aggro, or enter combat.
        service.SetOwnBrain(new BlankBrain());
        Point2D point = player.GetPointFromHeading(player.Heading, 96);
        service.Name = name;
        service.GuildName = tag;
        service.Model = model;
        service.Size = 40;
        service.Level = 1;
        service.Realm = player.Realm;
        service.CurrentRegion = player.CurrentRegion;
        service.CurrentRegionID = player.CurrentRegionID;
        service.X = point.X;
        service.Y = point.Y;
        service.Z = player.Z;
        service.Heading = player.Heading;
        service.MaxSpeedBase = 0;
        service.Flags |= GameNPC.eFlags.PEACE;
        service.LoadedFromScript = true;
        return service;
    }

    private sealed class SluaghbinderEpicMerchant : GameMerchant
    {
        // A null trade list is intentional: the client still receives an
        // empty merchant packet, which opens the normal sell window without
        // inventing purchasable stock or requiring a database item-list row.
        public override void SendMerchantWindow(GamePlayer player)
        {
            if (player != null)
                player.Out.SendMerchantWindow(null, eMerchantWindowType.Normal);
        }

        public override bool Interact(GamePlayer player)
        {
            if (player?.Realm != Realm)
                return false;
            return base.Interact(player);
        }
    }

    private sealed class SluaghbinderEpicHastener : GameHastener
    {
        public override bool Interact(GamePlayer player)
        {
            if (player?.Realm != Realm)
                return false;
            return base.Interact(player);
        }
    }

    private sealed class SluaghbinderEpicHealer : GameNPC
    {
        public override bool Interact(GamePlayer player)
        {
            if (player?.Realm != Realm || !base.Interact(player))
                return false;

            TurnTo(player, 5000);
            ECSGameEffect pveIllness = EffectListService.GetEffectOnTarget(player, eEffect.ResurrectionIllness);
            pveIllness?.End();
            ECSGameEffect rvrIllness = EffectListService.GetEffectOnTarget(player, eEffect.RvrResurrectionIllness);
            rvrIllness?.End();

            if (player.TotalConstitutionLostAtDeath > 0)
            {
                player.TotalConstitutionLostAtDeath = 0;
                player.Out.SendCharStatsUpdate();
                player.Out.SendMessage("The skeletal healer restores your lost Constitution without charge.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
            else
            {
                player.Out.SendMessage("The skeletal healer finds no lost Constitution to restore.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }

            return true;
        }
    }

    private sealed class SluaghbinderEpicTeleporter : HiberniaTeleporter
    {
        public override bool Interact(GamePlayer player)
        {
            if (player?.Realm != Realm)
                return false;
            return base.Interact(player);
        }

        public override bool WhisperReceive(GameLiving source, string text)
        {
            if (source is not GamePlayer player || player.Realm != Realm)
                return false;
            return base.WhisperReceive(source, text);
        }
    }
}
