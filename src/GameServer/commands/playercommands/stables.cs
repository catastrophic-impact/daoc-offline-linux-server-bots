using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DOL.Database;
using DOL.GS.Movement;

namespace DOL.GS.Commands
{
    /// <summary>Displays the realm's actual Classic and Shrouded Isles ticket network.</summary>
    [CmdAttribute("&stables", ePrivLevel.Player,
        "Show your realm's Classic and Shrouded Isles stable connections",
        "/stables [page]", "/stables classic", "/stables si")]
    public sealed class StablesCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private const int PageCharacterLimit = 850;

        private sealed record Stable(string Source, string Master, string[] Destinations);
        private sealed record Page(string Section, string[] Lines);

        public void OnCommand(GameClient client, string[] args)
        {
            if (client?.Player == null || !TryGetRegions(client.Player.Realm, out ushort classicId, out ushort islesId))
                return;

            Stable[] classic = ReadStables(classicId, client.Player.Realm);
            Stable[] isles = ReadStables(islesId, client.Player.Realm);
            var pages = new List<Page>();
            pages.AddRange(PaginateLines(classic.Select(FormatStable), PageCharacterLimit)
                .Select(lines => new Page("Classic", lines)));
            pages.AddRange(PaginateLines(isles.Select(FormatStable), PageCharacterLimit)
                .Select(lines => new Page("Shrouded Isles", lines)));

            int page = 1;
            if (args.Length > 2 || args.Length == 2 &&
                !int.TryParse(args[1], out page) &&
                !args[1].Equals("classic", StringComparison.OrdinalIgnoreCase) &&
                !args[1].Equals("si", StringComparison.OrdinalIgnoreCase) &&
                !args[1].Equals("isles", StringComparison.OrdinalIgnoreCase))
            {
                DisplaySyntax(client);
                return;
            }
            if (args.Length == 2 &&
                (args[1].Equals("si", StringComparison.OrdinalIgnoreCase) ||
                 args[1].Equals("isles", StringComparison.OrdinalIgnoreCase)))
                page = pages.FindIndex(entry => entry.Section == "Shrouded Isles") + 1;
            else if (args.Length == 2 && args[1].Equals("classic", StringComparison.OrdinalIgnoreCase))
                page = 1;

            if (page < 1 || page > pages.Count)
            {
                DisplayMessage(client, $"Choose page 1-{pages.Count}. Use /stables si to jump to Shrouded Isles.");
                return;
            }

            int classicRoutes = classic.Sum(stable => stable.Destinations.Length);
            int islesRoutes = isles.Sum(stable => stable.Destinations.Length);
            var lines = new List<string>
            {
                $"{classicRoutes} Classic and {islesRoutes} Shrouded Isles direct routes. Arrows are one-way ticket connections.",
                "Neutral dragonfly, gryphon and wyvern stable tickets are included.",
                $"Page {page}/{pages.Count}. Use /stables <page>, /stables classic or /stables si.",
                ""
            };
            lines.AddRange(pages[page - 1].Lines);
            lines.AddRange(CrossingLines(classicId, islesId, client.Player.Realm));
            client.Out.SendCustomTextWindow($"{client.Player.Realm} stables - {pages[page - 1].Section} {page}/{pages.Count}", lines);
        }

        public static bool TryGetRegions(eRealm realm, out ushort classicId, out ushort islesId)
        {
            (classicId, islesId) = realm switch
            {
                eRealm.Albion => ((ushort)1, (ushort)51),
                eRealm.Midgard => ((ushort)100, (ushort)151),
                eRealm.Hibernia => ((ushort)200, (ushort)181),
                _ => ((ushort)0, (ushort)0)
            };
            return classicId != 0;
        }

        private static Stable[] ReadStables(ushort regionId, eRealm realm)
        {
            Region region = WorldMgr.GetRegion(regionId);
            if (region == null)
                return [];

            var stables = new List<Stable>();
            foreach (GameStableMaster master in region.Objects.OfType<GameStableMaster>()
                         .Where(master => master.ObjectState == GameObject.eObjectState.Active &&
                                          (master.Realm == realm || master.Realm == eRealm.None) &&
                                          master.TradeItems != null))
            {
                var tickets = new List<DbItemTemplate>();
                foreach (DictionaryEntry entry in master.TradeItems.GetAllItems())
                {
                    if (entry.Value is not DbItemTemplate ticket || ticket.Item_Type != 40)
                        continue;
                    PathPoint path = MovementMgr.LoadPath(ticket.Id_nb);
                    if (path?.Next == null || path.Type != EPathType.Once ||
                        Math.Abs((long)path.X - master.X) >= 500 ||
                        Math.Abs((long)path.Y - master.Y) >= 500)
                        continue;
                    tickets.Add(ticket);
                }
                if (tickets.Count == 0)
                    continue;

                string source = SourceName(master, tickets[0].Id_nb);
                string[] destinations = tickets.Select(ticket => TicketDestination(ticket.Name))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                if (destinations.Length > 0)
                    stables.Add(new Stable(source, master.Name, destinations));
            }
            return stables.OrderBy(stable => stable.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(stable => stable.Master, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string FormatStable(Stable stable)
        {
            return $"{stable.Source} ({stable.Master}) -> {string.Join(", ", stable.Destinations)}";
        }

        private static string TicketDestination(string name)
        {
            string value = name?.Trim() ?? string.Empty;
            int marker = value.IndexOf("ticket to", StringComparison.OrdinalIgnoreCase);
            return marker < 0 ? value : value[(marker + "ticket to".Length)..].Trim();
        }

        private static string SourceName(GameStableMaster master, string pathId)
        {
            string area = master.CurrentRegion?.GetAreasOfSpot(master.X, master.Y, master.Z)
                .OfType<AbstractArea>().FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Description))?.Description;
            if (!string.IsNullOrWhiteSpace(area))
                return area;

            string[] parts = pathId?.Split('_') ?? [];
            if (parts.Length >= 3 &&
                (parts[0].Equals("HS", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("HR", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("Dragonfly", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("Gryphon", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("Wyvern", StringComparison.OrdinalIgnoreCase)))
                return HumanizeSource(parts[1]);
            return master.CurrentZone?.Description ?? master.Name;
        }

        public static string HumanizeSource(string token)
        {
            string known = token switch
            {
                "AdribardsRetreat" => "Adribard's Retreat",
                "Aegirhamm" => "Aegirhamn",
                "Camp" => "Lammia Camp",
                "CampStation" => "Campacorentin Station",
                "CamelotNoth" => "Camelot North",
                "Cotswold" => "Cotswold Village",
                "FortGwyntel" => "Fort Gwyntell",
                "GronyrsFarm" => "Gronyr's Farm",
                "GroveofAalidFeie" => "Grove of Aalid Feie",
                "IarnDwarf" => "Iarn Dwarf Camp",
                "Mantid" => "Mantid Town",
                "Moderna" => "Modernagrav",
                "ParthananFarm" or "ParthenonFarm" => "East Lough Derg",
                "TirNaNog" or "TirNaNogNord" => "Tir na Nog North",
                "TirnamBeo" => "Tir na mBeo",
                _ => null
            };
            return known ?? Regex.Replace(token ?? string.Empty, "(?<=[a-z])(?=[A-Z])", " ");
        }

        public static IReadOnlyList<string[]> PaginateLines(IEnumerable<string> entries, int maxCharacters)
        {
            var pages = new List<string[]>();
            var lines = new List<string>();
            int length = 0;
            foreach (string line in entries)
            {
                if (lines.Count > 0 && (length + line.Length + 2 > maxCharacters || lines.Count >= 20))
                {
                    pages.Add(lines.ToArray());
                    lines.Clear();
                    length = 0;
                }
                lines.Add(line);
                length += line.Length + 2;
            }
            if (lines.Count > 0)
                pages.Add(lines.ToArray());
            if (pages.Count == 0)
                pages.Add(["No stable tickets are currently available in this region."]);
            return pages;
        }

        private static IEnumerable<string> CrossingLines(ushort classicId, ushort islesId, eRealm realm)
        {
            DbZonePoint[] crossings = DOLDB<DbZonePoint>.SelectObjects(DB.Column("SourceRegion").IsEqualTo(classicId))
                .Concat(DOLDB<DbZonePoint>.SelectObjects(DB.Column("SourceRegion").IsEqualTo(islesId)))
                .Where(point => (point.SourceRegion == classicId && point.TargetRegion == islesId ||
                                 point.SourceRegion == islesId && point.TargetRegion == classicId) &&
                                (point.Realm == 0 || point.Realm == (ushort)realm))
                .OrderBy(point => point.SourceRegion == classicId ? 0 : 1).ToArray();
            if (crossings.Length == 0)
                yield break;

            yield return "";
            yield return "Classic / Isles zone crossings (not stable tickets):";
            foreach (DbZonePoint point in crossings)
            {
                string from = WorldMgr.GetRegion(point.SourceRegion)?.GetSpotDescription(point.SourceX, point.SourceY, point.SourceZ)
                    ?? $"region {point.SourceRegion}";
                string to = WorldMgr.GetRegion(point.TargetRegion)?.GetSpotDescription(point.TargetX, point.TargetY, point.TargetZ)
                    ?? $"region {point.TargetRegion}";
                yield return $"{from} -> {to}";
            }
        }
    }
}
