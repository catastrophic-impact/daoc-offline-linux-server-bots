using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DOL.Logging;

namespace DOL.GS.Quests
{
    /// <summary>
    /// The Quest Journal's QUEST GUIDE button (owner 2026-10-07): the period Allakhazam walkthrough of each active quest,
    /// read from classic-quest-guides.json next to the server (research/period-data/quests/build_quest_guides.py).
    /// The button sends /task; each click shows the next page, then the next active quest. /questguide [quest] [page]
    /// opens any classic quest's guide. Quests made for this server (bounties, reputation, the Sluaghbinder epic) have
    /// no Allakhazam page: their own journal text is shown instead.
    /// </summary>
    public static class QuestGuide
    {
        public const string FileName = "classic-quest-guides.json";
        public const string PeriodNote = "Period-accurate Allakhazam quest guide information (2001-2004).";
        /// <summary>Characters of text per window: the detail-window packet holds about 2 KB.</summary>
        public const int PageCharacters = 1400;
        private const int LineCharacters = 250;
        private const long CursorMilliseconds = 10 * 60 * 1000;
        private static readonly Logger Log = LoggerManager.Create(typeof(QuestGuide));
        private static readonly ConcurrentDictionary<GamePlayer, Cursor> Cursors = new();
        private static Dictionary<string, Guide> _guides = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _loadedWrite;

        public sealed class Guide
        {
            public string Name { get; set; }
            public string Realm { get; set; }
            public string Source { get; set; }
            public string Updated { get; set; }
            public List<string> Lines { get; set; } = new();
        }

        private sealed class GuideFile
        {
            public Dictionary<string, Guide> Guides { get; set; } = new();
        }

        private sealed record Cursor(string[] Quests, int Quest, int Page, long At);

        /// <summary>The QUEST GUIDE button: the next page of the player's active quests' guides.</summary>
        public static void ShowNext(GamePlayer player)
        {
            if (player == null) return;
            AbstractQuest[] active = player.QuestList.Keys.ToArray();
            if (active.Length == 0)
            {
                player.Out.SendCustomTextWindow("Quest Guide", new List<string>
                {
                    PeriodNote, " ", "You have no active quests.",
                    "Accept a quest and click QUEST GUIDE again, or type /questguide <quest name> to read any classic quest's walkthrough."
                });
                return;
            }
            string[] names = active.Select(q => q.Name).ToArray();
            long now = GameLoop.GameLoopTime;
            int quest = 0, page = 0;
            if (Cursors.TryGetValue(player, out Cursor cursor) && now - cursor.At < CursorMilliseconds && cursor.Quests.SequenceEqual(names))
            {
                quest = cursor.Quest;
                page = cursor.Page + 1;
                if (page >= Pages(player, active[quest]).Count)
                {
                    quest = (quest + 1) % active.Length;
                    page = 0;
                }
            }
            Cursors[player] = new Cursor(names, quest, page, now);
            Send(player, active[quest], page, active.Length > 1 ? $"Quest {quest + 1} of {active.Length}. " : string.Empty);
        }

        /// <summary>/questguide: a quest by name (active quests first, then any classic quest of the player's realm).</summary>
        public static bool Show(GamePlayer player, string query, int page)
        {
            if (player == null || string.IsNullOrWhiteSpace(query)) return false;
            AbstractQuest active = player.QuestList.Keys.FirstOrDefault(q => q.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                                   ?? player.QuestList.Keys.FirstOrDefault(q => q.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (active != null)
            {
                Send(player, active, page, string.Empty);
                return true;
            }
            Guide guide = Find(player.Realm, query);
            if (guide == null) return false;
            List<List<string>> pages = Paginate(GuideLines(guide.Name, guide));
            SendPage(player, guide.Name, pages, page, string.Empty);
            return true;
        }

        public static Guide Find(eRealm realm, string name)
        {
            Load();
            string realmName = GlobalConstants.RealmToName(realm);
            if (_guides.TryGetValue($"{realmName}|{name}", out Guide guide)) return guide;
            return _guides.Values.FirstOrDefault(g => string.Equals(g.Realm, realmName, StringComparison.OrdinalIgnoreCase) &&
                                                      g.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        private static void Send(GamePlayer player, AbstractQuest quest, int page, string prefix) =>
            SendPage(player, quest.Name, Pages(player, quest), page, prefix);

        private static void SendPage(GamePlayer player, string name, List<List<string>> pages, int page, string prefix)
        {
            page = Math.Clamp(page, 0, pages.Count - 1);
            var lines = new List<string> { $"{name} - page {page + 1} of {pages.Count}" };
            // /task opens this guide too, so the player's current task (if any) stays one click away
            player.GameTask?.CheckTaskExpired();
            if (page == 0 && player.GameTask is { TaskActive: true } task)
                lines.Add($"Your task: {task.Name} (expires {task.TimeOut.ToShortTimeString()}; /task status for details).");
            lines.AddRange(pages[page]);
            lines.Add(" ");
            lines.Add(prefix + (pages.Count > page + 1 ? "Click QUEST GUIDE again for the next page." : "Click QUEST GUIDE again for your next quest.") +
                      " /questguide <quest name> [page] opens any quest's guide.");
            player.Out.SendCustomTextWindow("Quest Guide", lines);
        }

        private static List<List<string>> Pages(GamePlayer player, AbstractQuest quest)
        {
            Guide guide = Find(player.Realm, quest.Name);
            if (guide != null && string.Equals(guide.Name, quest.Name, StringComparison.OrdinalIgnoreCase))
                return Paginate(GuideLines(quest.Name, guide));
            var lines = new List<string>
            {
                "This quest belongs to this server (it is not one of the period quests), so Allakhazam has no walkthrough for it.",
                " "
            };
            if (!string.IsNullOrWhiteSpace(quest.Description)) lines.Add(quest.Description);
            return Paginate(lines);
        }

        /// <summary>A guide's text with the period note and its source at the top of the walkthrough.</summary>
        public static List<string> GuideLines(string name, Guide guide)
        {
            var lines = new List<string> { PeriodNote, "Source: " + guide.Source };
            if (!string.IsNullOrWhiteSpace(guide.Updated)) lines.Add("Walkthrough last updated: " + guide.Updated);
            lines.Add(" ");
            lines.AddRange(guide.Lines);
            return lines;
        }

        /// <summary>Lines wrapped to the packet's line size and grouped into windows of about PageCharacters.</summary>
        public static List<List<string>> Paginate(IEnumerable<string> lines)
        {
            var pages = new List<List<string>> { new() };
            int size = 0;
            foreach (string line in lines.SelectMany(Wrap))
            {
                if (size + line.Length + 2 > PageCharacters && pages[^1].Count > 0)
                {
                    pages.Add(new List<string>());
                    size = 0;
                }
                pages[^1].Add(line);
                size += line.Length + 2;
            }
            return pages;
        }

        private static IEnumerable<string> Wrap(string line)
        {
            string rest = string.IsNullOrEmpty(line) ? " " : line;
            while (rest.Length > LineCharacters)
            {
                int cut = rest.LastIndexOf(' ', LineCharacters);
                if (cut <= 0) cut = LineCharacters;
                yield return rest[..cut];
                rest = rest[cut..].TrimStart();
            }
            yield return rest;
        }

        private static void Load()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, FileName);
                if (!File.Exists(path)) return;
                DateTime write = File.GetLastWriteTimeUtc(path);
                if (write == _loadedWrite) return;
                GuideFile file = JsonSerializer.Deserialize<GuideFile>(File.ReadAllText(path));
                _guides = new Dictionary<string, Guide>(file?.Guides ?? new(), StringComparer.OrdinalIgnoreCase);
                _loadedWrite = write;
            }
            catch (Exception ex)
            {
                Log.Error("Could not load classic-quest-guides.json; the Quest Guide shows journal text only", ex);
                _loadedWrite = DateTime.MinValue;
            }
        }
    }
}
