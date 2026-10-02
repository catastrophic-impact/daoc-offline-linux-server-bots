using System;
using System.Linq;
using System.Reflection;
using DOL.Logging;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;

namespace DOL.GS
{
public class BotCommand : AbstractCommandHandler, ICommandHandler
{
    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    
    public void OnCommand(GameClient client, string[] args)
    {
        if (args.Length < 2)
        {
            AutonomousPlayerPilot.Toggle(client.Player);
            return;
        }

        var player = client.Player;
        var subCmd = args[1].ToLower();

        try
        {
            switch (subCmd)
            {
                case "status":
                    client.Out.SendMessage(AutonomousPlayerPilot.GetStatus(client.Player), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                case "off":
                    if (AutonomousPlayerPilot.IsActive(client.Player))
                        AutonomousPlayerPilot.Disable(client.Player);
                    else
                        client.Out.SendMessage("BOT MODE is already off.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                case "on":
                    AutonomousPlayerPilot.Enable(client.Player);
                    break;
                case "create":
                    HandleCreate(client, args);
                    break;
                case "spawn":
                    HandleSpawn(client, args);
                    break;
                case "list":
                    HandleList(client);
                    break;
                case "delete":
                    HandleDelete(client, args);
                    break;
                case "despawn":
                    HandleDespawn(client, args);
                    break;
                case "invite":
                    HandleInvite(client, args);
                    break;
                case "follow":
                    HandleFollow(client, args);
                    break;
                case "stay":
                    HandleStay(client, args);
                    break;
                case "hold":
                    HandleHold(client, args);
                    break;
                case "resume":
                    HandleResume(client, args);
                    break;
                case "stance":
                    HandleStance(client, args);
                    break;
                default:
                    client.Out.SendMessage($"Unknown bot command: {subCmd}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    DisplayHelp(client);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"Bot command error: {ex.Message}", ex);
            client.Out.SendMessage("An error occurred processing your bot command.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }

    private void HandleCreate(GameClient client, string[] args)
    {
        if (args.Length < 6)
        {
            client.Out.SendMessage("Usage: /bot create <name> <classId> <raceId> <genderId>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            client.Out.SendMessage("Example: /bot create Aylia 2 1 0", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        var name = args[2];
        if (!byte.TryParse(args[3], out var classId) ||
            !byte.TryParse(args[4], out var raceId) ||
            !byte.TryParse(args[5], out var genderId))
        {
            client.Out.SendMessage("Class, Race, and Gender must be numbers.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        // Validate class/race/gender IDs (you can map to enums later)
        if (classId < 1 || classId > 50) // adjust range as needed
        {
            client.Out.SendMessage("Invalid class ID.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        var bot = BotManager.CreateBot(client.Player, name, classId, raceId, genderId);
        if (bot == null)
        {
            client.Out.SendMessage("Failed to create bot. Check class/race/gender IDs or bot limit.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        try
        {
            bot.SaveToDatabase();
        }
        catch (Exception ex)
        {
            log.Warn($"Bot '{bot.Name}' could not be saved to database — continuing in-memory only.", ex);
        }

        if (!BotManager.SpawnBot(bot))
        {
            client.Out.SendMessage("Failed to spawn bot into the world.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        // Auto-invite to group
        if (client.Player.Group == null)
        {
            var group = new Group(client.Player);
            GroupMgr.AddGroup(group);
            group.AddMember(client.Player);
        }

        if (client.Player.Group != null)
            client.Player.Group.AddMember(bot);

        // Auto-follow
        bot.Follow(client.Player, BotManager.FOLLOW_DISTANCE, BotManager.MAX_FOLLOW_DISTANCE);
        if (bot.Brain is DOL.AI.Brain.BotBrain brain)
            brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);

        client.Out.SendMessage($"Bot '{bot.Name}' created, spawned, and following you!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleSpawn(GameClient client, string[] args)
    {
        if (args.Length < 3)
        {
            client.Out.SendMessage("Usage: /bot spawn <name>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        var botName = args[2];
        var bot = BotManager.LoadBotByName(client.Player, botName);
        if (bot == null)
        {
            client.Out.SendMessage($"No bot named '{botName}' found.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        if (!BotManager.SpawnBot(bot))
        {
            client.Out.SendMessage("Failed to spawn bot into the world.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        // Auto-invite to group
        if (client.Player.Group == null)
        {
            var group = new Group(client.Player);
            GroupMgr.AddGroup(group);
            group.AddMember(client.Player);
        }
        client.Player.Group.AddMember(bot);

        // Auto-follow
        bot.Follow(client.Player, BotManager.FOLLOW_DISTANCE, BotManager.MAX_FOLLOW_DISTANCE);
        if (bot.Brain is DOL.AI.Brain.BotBrain brain)
            brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);

        client.Out.SendMessage($"{bot.Name} spawned, grouped, and following you.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleList(GameClient client)
    {
        var bots = BotManager.GetSavedBotsForOwner(client.Player).ToList();
        if (bots.Count == 0)
        {
            client.Out.SendMessage("You have no saved bots.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        var activeBots = BotManager.GetBotsForOwner(client.Player).ToList();
        client.Out.SendMessage($"You have {bots.Count} saved bot(s) ({activeBots.Count} active):", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        foreach (var bot in bots)
        {
            var className = BotManager.GetClassNameById(bot.ClassId);
            var isActive = activeBots.Any(b => b.Name == bot.Name);
            var status = isActive ? "[ACTIVE]" : "[SAVED]";
            client.Out.SendMessage($"  {status} {bot.Name} - Lv{bot.Level} {className}",
                eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }

    private void HandleDelete(GameClient client, string[] args)
    {
        var bot = ResolveBotTarget(client, args, 2);
        if (bot == null) return;

        var name = bot.Name;
        BotManager.DeleteBot(bot); // ← Removes from DB permanently
        client.Out.SendMessage($"Bot '{name}' permanently deleted.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleDespawn(GameClient client, string[] args)
    {
        var bot = ResolveBotTarget(client, args, 2);
        if (bot == null) return;

        BotManager.DespawnBot(bot); // ← Sets is_active=0, removes from world/group
        client.Out.SendMessage($"{bot.Name} despawned.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleInvite(GameClient client, string[] args)
    {
        var bot = ResolveBotTarget(client, args, 2);
        if (bot == null) return;

        if (client.Player.Group == null)
        {
            var group = new Group(client.Player);
            GroupMgr.AddGroup(group);
            group.AddMember(client.Player);
        }

        if (client.Player.Group.AddMember(bot))
        {
            client.Out.SendMessage($"{bot.Name} invited to group.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
        else
        {
            client.Out.SendMessage($"{bot.Name} is already in a group.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }

    private void HandleFollow(GameClient client, string[] args)
    {
        var bot = ResolveBotTarget(client, args, 2);
        if (bot == null) return;

        bot.Follow(client.Player, BotManager.FOLLOW_DISTANCE, BotManager.MAX_FOLLOW_DISTANCE);
        if (bot.Brain is DOL.AI.Brain.BotBrain brain)
            brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
        client.Out.SendMessage($"{bot.Name} is now following you.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleStay(GameClient client, string[] args)
    {
        var bot = ResolveBotTarget(client, args, 2);
        if (bot == null) return;

        if (bot.Brain is DOL.AI.Brain.BotBrain brain)
            brain.FSM.SetCurrentState(eFSMStateType.IDLE);
        client.Out.SendMessage($"{bot.Name} is holding position.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleHold(GameClient client, string[] args)
    {
        var bot = ResolveBotTarget(client, args, 2);
        if (bot == null) return;

        if (bot.Brain is DOL.AI.Brain.BotBrain brain)
            brain.FSM.SetCurrentState(eFSMStateType.PASSIVE);
        client.Out.SendMessage($"{bot.Name} AI suspended.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleResume(GameClient client, string[] args)
    {
        var bot = ResolveBotTarget(client, args, 2);
        if (bot == null) return;

        if (bot.Brain is DOL.AI.Brain.BotBrain brain)
            brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
        client.Out.SendMessage($"{bot.Name} AI resumed.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private void HandleStance(GameClient client, string[] args)
    {
        if (args.Length < 3)
        {
            client.Out.SendMessage("Usage: /bot stance <auto|melee|ranged|passive> [name]", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        if (!Enum.TryParse<eBotStance>(args[2], true, out var stance))
        {
            client.Out.SendMessage("Valid stances: auto, melee, ranged, passive", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        var bot = ResolveBotTarget(client, args, 3);
        if (bot == null) return;

        bot.Stance = stance;
        client.Out.SendMessage($"{bot.Name} stance set to {stance}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private GameBot ResolveBotTarget(GameClient client, string[] args, int nameIndex)
    {
        string botName = null;

        // If name provided, use it
        if (args.Length > nameIndex)
        {
            botName = args[nameIndex];
        }
        // Else, if targeting a bot, use target
        else if (client.Player.TargetObject is GameBot targetedBot && targetedBot.Owner == client.Player)
        {
            return targetedBot;
        }
        else
        {
            client.Out.SendMessage("No bot name specified and no bot targeted.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return null;
        }

        var bot = BotManager.GetBotByName(client.Player, botName);
        if (bot == null)
        {
            client.Out.SendMessage($"No bot named '{botName}' found.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return null;
        }

        return bot;
    }

    private void DisplayHelp(GameClient client)
    {
        client.Out.SendMessage("Bot System Commands:", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot create <name> <classId> <raceId> <genderId>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot spawn <name>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot list", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot delete <name>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot despawn <name>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot invite <name>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot follow [name] → uses target if blank", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot stay [name] → uses target if blank", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot hold [name] → suspend AI", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot resume [name] → resume AI", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        client.Out.SendMessage("/bot stance <auto|melee|ranged|passive> [name]", eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }
}
}
