using System.Linq;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;

namespace DOL.GS.Commands
{
    /// <summary>Opens the period Allakhazam walkthrough of a quest (owner 2026-10-07). The Quest Journal's QUEST GUIDE
    /// button pages through the active quests (/task); this opens one by name.</summary>
    [CmdAttribute("&questguide", ePrivLevel.Player,
        "Read the period (2001-2004) Allakhazam walkthrough of a quest",
        "/questguide - next page of your active quests' guides",
        "/questguide <quest name> [page]")]
    public class QuestGuideCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client?.Player == null || IsSpammingCommand(client.Player, "questguide"))
                return;

            if (args.Length < 2)
            {
                QuestGuide.ShowNext(client.Player);
                return;
            }

            int page = 1;
            string[] words = args.Skip(1).ToArray();
            if (words.Length > 1 && int.TryParse(words[^1], out int asked))
            {
                page = asked;
                words = words[..^1];
            }
            string name = string.Join(" ", words);
            if (!QuestGuide.Show(client.Player, name, page - 1))
                client.Out.SendMessage($"No quest guide matches \"{name}\".", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }
}
