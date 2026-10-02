using System;
using System.Linq;
using System.Numerics;
using DOL.Database;
using DOL.GS.Scripts;

namespace DOL.GS;

public sealed partial class AutonomousWorldBotController
{
    private OFTeleporter _frontierPorter;
    private GameMerchant _medallionMerchant;
    private long _nextPorterSearch;
    private Vector3? _frontierWaitingPoint;
    private long _nextWaitingPointSearch;

    private bool TryReturnFromForeignFrontier(GameBot bot)
    {
        // PvE/services cannot walk through another realm's border gates to get
        // home. Use the same real porter/ticket boarding used by outbound RvR.
        ushort home = bot.Realm switch { eRealm.Albion => 1, eRealm.Midgard => 100, eRealm.Hibernia => 200, _ => 0 };
        if (home == 0 || bot.CurrentRegionID == home || bot.CurrentRegionID is not (1 or 100 or 200) ||
            AutonomousObjectiveAssignments.Parse(bot.PersistentRecord?.ObjectiveKind) == eAutonomousObjectiveKind.RvR ||
            GameRelic.IsPlayerCarryingRelic(bot)) return false;
        var passage = AutonomousFrontierTransport.Destination(bot.Realm, home);
        return passage != null && TryFrontierTransport(bot, new("return-home", passage.Location.Name, "frontier",
            home, passage.Location.X, passage.Location.Y, passage.Location.Z, bot.Level, false, true));
    }

    private bool TryFrontierTransport(GameBot bot, CampDestination destination)
    {
        if (bot.CurrentRegionID == destination.RegionId || bot.CurrentRegionID is not (1 or 100 or 200) ||
            destination.RegionId is not (1 or 100 or 200) || GameRelic.IsPlayerCarryingRelic(bot)) return false;
        var passage = AutonomousFrontierTransport.Destination(bot.Realm,destination.RegionId);
        if (passage == null) return false;
        bool returningToPve = passage.Medallion == "home_necklace" &&
            AutonomousObjectiveAssignments.Parse(bot.PersistentRecord?.ObjectiveKind) != eAutonomousObjectiveKind.RvR;
        var party=AutonomousFrontierTransport.BoardingParty(bot, passage);
        if(party.Any(GameRelic.IsPlayerCarryingRelic))
        {
            foreach(var member in party) member.TempProperties.RemoveProperty(AutonomousFrontierTransport.RequestKey);
            return false;
        }
        // A full backpack must not cancel every member's approach before the
        // owner can reach the real merchant and sell ordinary vendor trash.
        if (_frontierPorter?.ObjectState != GameObject.eObjectState.Active || _frontierPorter.CurrentRegion != bot.CurrentRegion)
        {
            if (GameLoop.GameLoopTime < _nextPorterSearch) return false;
            _nextPorterSearch = GameLoop.GameLoopTime + 30_000;
            _frontierPorter = AutonomousFrontierTransport.NearestPorter(bot);
            _medallionMerchant = null;
            _frontierWaitingPoint = null;
            _nextWaitingPointSearch = 0;
        }
        if (_frontierPorter == null) return false;
        // Declare the transport intent before approaching its merchant. A
        // returning PvE bot may need its own portal-keep door to BUY the ticket.
        // Boarding still requires the real purchased medallion and all safety checks.
        bot.TempProperties.SetProperty(AutonomousFrontierTransport.RequestKey,new AutonomousFrontierTransport.Request(
            _frontierPorter,passage,bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}"));
        if (AutonomousFrontierTransport.Ticket(bot,passage) == null)
        {
            if (_medallionMerchant?.ObjectState != GameObject.eObjectState.Active ||
                !_medallionMerchant.TradeItems.GetAllItems().Values.OfType<DbItemTemplate>().Any(item=>item.Id_nb==passage.Medallion))
                _medallionMerchant = _frontierPorter.GetNPCsInRadius(3000).OfType<GameMerchant>()
                    .Where(npc => (npc.Realm==bot.Realm || npc.Realm==eRealm.None) && npc.TradeItems?.GetAllItems().Values.OfType<DbItemTemplate>().Any(item=>item.Id_nb==passage.Medallion)==true)
                    .OrderBy(_frontierPorter.GetDistanceTo).FirstOrDefault();
            if (_medallionMerchant == null) return false;
            if (!ApproachSupplyMerchant(bot, _medallionMerchant))
            {
                SetRvrStatus(bot,"Collecting frontier medallion",destination.MonsterName,$"Walking to {_medallionMerchant.Name} for {passage.Medallion}");
                return true;
            }
            var template = _medallionMerchant.TradeItems.GetAllItems().Values.OfType<DbItemTemplate>().First(item=>item.Id_nb==passage.Medallion);
            var slot = bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack,eInventorySlot.LastBackpack);
            if (slot == eInventorySlot.Invalid)
            {
                // A full backpack after RvR must not force a walk through enemy
                // gates. Sell one ordinary disposable item at this real merchant;
                // never delete an item or grant a free ticket/teleport.
                var trash = AutonomousBotEconomy.FindVendorTrashCandidate(bot);
                if (trash != null)
                    AutonomousBotEconomy.TrySellToVendor(bot, _medallionMerchant, trash, out _);
                SetRvrStatus(bot, "Making room for frontier medallion", destination.MonsterName,
                    "At the real merchant; preserving equipped items and protected upgrades");
                return true;
            }
            if (slot==eInventorySlot.Invalid || template.Price<0 || template.Price>0 && !AutonomousBotEconomy.TrySpend(bot.DatabaseID,template.Price))
                return false; // Existing legal dungeon itinerary remains available.
            var ticket = GameInventoryItem.Create(template);
            if (ticket == null || !bot.Inventory.AddItem(slot,ticket))
            {
                if(template.Price>0) AutonomousBotEconomy.AddMoney(bot.DatabaseID,template.Price);
                return false;
            }
            AutonomousBotEconomy.MarkInventoryChanged(bot);
            AutonomousBotStatusPersistence.Queue(bot,true);
        }
        // Ordinary warbands retain their shared departure. Committed siege
        // responders board independently; defenders receive a bounded priority slice.
        if (!_frontierWaitingPoint.HasValue && GameLoop.GameLoopTime >= _nextWaitingPointSearch)
        {
            _nextWaitingPointSearch = GameLoop.GameLoopTime + 10_000;
            if (AutonomousFrontierTransport.TryWaitingPoint(bot, _frontierPorter, out var waiting))
                _frontierWaitingPoint = waiting;
        }
        if (!_frontierWaitingPoint.HasValue)
        {
            SetRvrStatus(bot,"Finding teleporter waiting space",destination.MonsterName,"Waiting for a reachable spot clear of the teleporter");
            return true;
        }
        if (Vector3.Distance(new(bot.X,bot.Y,bot.Z),_frontierWaitingPoint.Value) > 40)
            IssuePath(bot,_frontierWaitingPoint.Value);
        else { bot.StopMovingOnPath(); bot.StopMoving(); }
        AutonomousFrontierTransport.WakeBoardingPorter(bot,
            bot.TempProperties.GetProperty<AutonomousFrontierTransport.Request>(AutonomousFrontierTransport.RequestKey));
        SetRvrStatus(bot,"Boarding frontier teleporter",destination.MonsterName,
            AutonomousFrontierTransport.HasDefenderPriority(bot, passage)
                ? $"At {_frontierPorter.Name}; priority defense departure to {passage.Location.Name}"
                : AutonomousFrontierTransport.HasCommittedSiegePassage(bot, passage)
                    ? $"At {_frontierPorter.Name}; boarding independently for the siege at the next departure to {passage.Location.Name}"
                    : $"At {_frontierPorter.Name}; waiting for the warband and the native departure to {passage.Location.Name}");
        return true;
    }
}
