using System;
using System.Linq;
using System.Text;
using System.Threading;
using DOL.Database;
using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
    /// <summary>Ephemeral, owner-only player UI. No bot upkeep or persistent NPC records.</summary>
    public sealed class PlayerCharmSelectionMenu : GameNPC
    {
        public const int LifetimeMilliseconds = 600_000;
        public const int PageSize = 12;
        private const string MenuKey = "OfflineDAoC.PlayerCharmMenu";
        private const string ChoiceKey = "OfflineDAoC.PlayerCharmChoice";
        private sealed record Choice(int SpellId, int Level, ushort Region, long Expires, DbMob Template);
        private readonly GamePlayer _owner;
        private readonly Spell _spell;
        private readonly SpellLine _line;
        private readonly DbMob[] _choices;
        private readonly int _level;
        private readonly long _expires;
        private readonly GameObject _previousTarget;
        private ECSGameTimer _timer;
        private int _page;
        private int _closed;

        private PlayerCharmSelectionMenu(GamePlayer owner, Spell spell, SpellLine line, DbMob[] choices)
        {
            _owner = owner;
            _spell = spell;
            _line = line;
            _choices = choices;
            _level = owner.Level;
            _expires = GameLoop.GameLoopTime + LifetimeMilliseconds;
            _previousTarget = owner.TargetObject;
            Name = "Charm Creature Menu";
            Realm = owner.Realm;
            Model = 150;
            Size = 1;
            Level = 1;
            Flags = eFlags.PEACE | eFlags.DONTSHOWNAME;
            LoadedFromScript = true;
            X = owner.X;
            Y = owner.Y;
            Z = owner.Z;
            CurrentRegion = owner.CurrentRegion;
        }

        public static bool Supports(GamePlayer owner, Spell spell) => owner?.CharacterClass != null &&
            spell?.SpellType == eSpellType.Charm &&
            PlayerGeneratedCharmPolicy.TryGetRank((eCharacterClass)owner.CharacterClass.ID, spell.ID, out _, out _);

        private static bool CanChoose(GamePlayer owner, Spell spell) => Supports(owner, spell) &&
            owner.IsAlive && owner.ObjectState == eObjectState.Active && owner.CurrentRegion != null &&
            !owner.CurrentRegion.IsCapitalCity && owner.ControlledBrain == null &&
            PlayerGeneratedCharmPolicy.TargetLevel((eCharacterClass)owner.CharacterClass.ID, spell.ID, owner.Level) > 0;

        public static void Open(GamePlayer owner, Spell spell, SpellLine line)
        {
            owner.TempProperties.GetProperty<PlayerCharmSelectionMenu>(MenuKey)?.Close();
            owner.TempProperties.RemoveProperty(ChoiceKey);
            if (!CanChoose(owner, spell))
            {
                Tell(owner, "Release your existing pet first, and use this charm while alive outside a capital city.");
                return;
            }
            DbMob[] choices = AutonomousPetSupport.GetPlayerCharmChoices(owner, spell)
                .OrderBy(mob => mob.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (choices.Length == 0)
            {
                Tell(owner, "No eligible creatures are available for this charm rank at your current level.");
                return;
            }
            var menu = new PlayerCharmSelectionMenu(owner, spell, line, choices);
            if (!menu.AddToWorld())
            {
                menu.Delete();
                Tell(owner, "The creature menu could not open. Please try again.");
                return;
            }
            owner.TempProperties.SetProperty(MenuKey, menu);
            menu._timer = new ECSGameTimer(menu, _ =>
            {
                if (!menu.IsCurrent()) { menu.Close(); return 0; }
                return 5000;
            }, 5000);
            menu.ShowPage();
        }

        private bool IsCurrent() => _closed == 0 && GameLoop.GameLoopTime < _expires &&
            CanChoose(_owner, _spell) && _owner.Level == _level &&
            _owner.CurrentRegion == CurrentRegion && _owner.IsWithinRadius(this, 512) &&
            _owner.Client?.ClientState == GameClient.eClientState.Playing;

        public static string ChoiceLabel(int index, string name) =>
            $"{index + 1}: {name.Replace('[', '(').Replace(']', ')').Replace('\n', ' ').Replace('\r', ' ')}";

        /// <summary>The menu line: name plus how it fights, e.g. "3: drakoran mage (Caster)".</summary>
        public static string ChoiceLabel(int index, DbMob mob) =>
            $"{ChoiceLabel(index, mob.Name)} ({CharmCreatureRoles.Label(CharmCreatureRoles.Classify(mob))})";

        public static string BuildPage(DbMob[] choices, int page, int petLevel)
        {
            int pages = Math.Max(1, (choices.Length + PageSize - 1) / PageSize);
            page = Math.Clamp(page, 0, pages - 1);
            var text = new StringBuilder($"Choose your level {petLevel} pet. Nothing is summoned until you select it.\nPage {page + 1}/{pages}\n\n");
            for (int i = page * PageSize; i < Math.Min(choices.Length, (page + 1) * PageSize); i++)
                text.Append('[').Append(ChoiceLabel(i, choices[i])).Append("]\n");
            if (page > 0) text.Append("[Previous]  ");
            if (page + 1 < pages) text.Append("[Next]  ");
            text.Append("[Cancel]\nMenu expires after 10 minutes, or when you leave this location.");
            return text.ToString();
        }

        private void ShowPage()
        {
            _owner.TargetObject = this;
            _owner.Out.SendChangeTarget(this);
            int petLevel = PlayerGeneratedCharmPolicy.TargetLevel((eCharacterClass)_owner.CharacterClass.ID, _spell.ID, _level);
            _owner.Out.SendMessage(BuildPage(_choices, _page, petLevel), eChatType.CT_Say, eChatLoc.CL_PopupWindow);
        }

        public override bool WhisperReceive(GameLiving source, string text)
        {
            if (!ReferenceEquals(source, _owner)) return false;
            if (!IsCurrent()) { Close(); return false; }
            if (text == "Cancel") { Close(); return true; }
            if (text == "Next" || text == "Previous")
            {
                _page = Math.Clamp(_page + (text == "Next" ? 1 : -1), 0, (_choices.Length - 1) / PageSize);
                ShowPage();
                return true;
            }
            int index = -1;
            for (int i = _page * PageSize; i < Math.Min(_choices.Length, (_page + 1) * PageSize); i++)
                if (string.Equals(text, ChoiceLabel(i, _choices[i]), StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            if (index < 0) return false;
            // Recheck spell ownership after any training/respec while this menu was open.
            bool learned = _owner.GetAllUsableSkills().Any(entry => entry.Item1 is Spell known && known.ID == _spell.ID) ||
                _owner.GetAllUsableListSpells().Any(entry => entry.Item2.OfType<Spell>().Any(known => known.ID == _spell.ID));
            if (!learned) { Close(); Tell(_owner, "This charm is no longer available. Cast your current charm again."); return true; }
            if (!Close()) return false;
            _owner.TempProperties.SetProperty(ChoiceKey, new Choice(_spell.ID, _level, _owner.CurrentRegionID,
                GameLoop.GameLoopTime + 30_000, _choices[index]));
            if (!_owner.CastSpell(_spell, _line))
            {
                _owner.TempProperties.RemoveProperty(ChoiceKey);
                Tell(_owner, "The charm could not start. Cast the charm again to choose your pet.");
            }
            return true;
        }

        public static DbMob TakeChoice(GamePlayer owner, Spell spell)
        {
            Choice choice = owner.TempProperties.GetProperty<Choice>(ChoiceKey);
            owner.TempProperties.RemoveProperty(ChoiceKey);
            return choice != null && choice.SpellId == spell.ID && choice.Level == owner.Level &&
                choice.Region == owner.CurrentRegionID && GameLoop.GameLoopTime < choice.Expires && CanChoose(owner, spell)
                ? choice.Template : null;
        }

        private bool Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return false;
            _timer?.Stop();
            _timer = null;
            if (ReferenceEquals(_owner.TempProperties.GetProperty<PlayerCharmSelectionMenu>(MenuKey), this))
                _owner.TempProperties.RemoveProperty(MenuKey);
            if (ReferenceEquals(_owner.TargetObject, this))
            {
                _owner.TargetObject = _previousTarget?.ObjectState == eObjectState.Active &&
                    _previousTarget.CurrentRegion == _owner.CurrentRegion ? _previousTarget : null;
                if (_owner.Client?.ClientState == GameClient.eClientState.Playing)
                    _owner.Out.SendChangeTarget(_owner.TargetObject);
            }
            Delete();
            return true;
        }

        private static void Tell(GamePlayer player, string text) =>
            player.Out.SendMessage(text, eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }
}
