using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>One native song source at a time; never extend its child effects or skip recast timers.</summary>
    public static class BotSongTwistPolicy
    {
        public readonly record struct Song(int Id, int CastMilliseconds, int CooldownMilliseconds, long RemainingMilliseconds);

        public static bool IsManagedSong(GameLiving living, Spell spell)
        {
            if (living is not GameBot bot || bot.CharacterClass == null ||
                spell == null || !spell.IsPulsing || spell.IsHarmful || spell.IsFocus)
                return false;
            eCharacterClass characterClass = (eCharacterClass)bot.CharacterClass.ID;
            if (BotBrain.IsClassicSongClass(characterClass))
                return BotBrain.IsMaintainableClassBuff(spell);
            // Tank chants use a separate selector: never send Paladin/Warden
            // through the performer code that drops songs for solo melee.
            return spell.IsInstantCast && !spell.NeedInstrument && (characterClass switch
            {
                eCharacterClass.Paladin => spell.SpellType is eSpellType.EnduranceRegenBuff or
                    eSpellType.SpecArmorFactorBuff or eSpellType.DamageAdd or eSpellType.CombatHeal,
                eCharacterClass.Warden => spell.SpellType is eSpellType.SpeedEnhancement or
                    eSpellType.Bladeturn or eSpellType.DamageAdd,
                _ => false
            });
        }

        public static bool IsMobileSong(GameLiving living, Spell spell)
        {
            return IsManagedSong(living, spell) &&
                (spell.IsInstantCast || spell.NeedInstrument || spell.MoveCast);
        }

        // Only temporary player companions use this travel pool. Bards have
        // a real endurance song; Skalds and Minstrels do not, so their useful
        // second travel song is health regeneration. Power and combat chants
        // must not take the speed anchor while the owner is moving.
        public static bool IsCompanionTravelSong(eCharacterClass characterClass, eSpellType type) =>
            characterClass switch
            {
                eCharacterClass.Bard => type is eSpellType.SpeedEnhancement or eSpellType.EnduranceRegenBuff,
                eCharacterClass.Skald or eCharacterClass.Minstrel =>
                    type is eSpellType.SpeedEnhancement or eSpellType.HealthRegenBuff,
                _ => false
            };

        // Only one helpful pulse source is legal. Generic buff selection must
        // not independently replace a tank's anchor with a resist/endurance
        // pulse; those are deliberately not part of the chosen core rotation.
        public static bool IsReservedPulse(GameLiving living, Spell spell) =>
            IsManagedSong(living, spell) ||
            living is GameBot bot && bot.CharacterClass != null &&
            (eCharacterClass)bot.CharacterClass.ID is eCharacterClass.Paladin or eCharacterClass.Warden &&
            spell != null && spell.IsPulsing && !spell.IsHarmful && !spell.IsFocus &&
            BotBrain.IsMaintainableClassBuff(spell);

        public static bool HasMobileSongCast(GameLiving living)
        {
            if (living is not GameBot bot || bot.CharacterClass == null)
                return false;

            CastingComponent casting = living.castingComponent;
            if (casting == null) return false;
            Spell active = casting.SpellHandler?.Spell;
            Spell queued = casting.QueuedSpellHandler?.Spell;
            bool pending = casting.TryPeekPendingSpell(out Spell requested);
            // Never use a song to authorize movement during an ordinary heal,
            // hostile spell, or unknown ability request queued alongside it.
            return (active != null || queued != null || pending) &&
                (active == null || IsMobileSong(living, active)) &&
                (queued == null || IsMobileSong(living, queued)) &&
                (!pending || IsMobileSong(living, requested));
        }

        public static eSpellType WardenAnchor(bool traveling, bool combat, bool grouped, bool hasBladeturn)
        {
            if (traveling && !combat) return eSpellType.SpeedEnhancement;
            if (hasBladeturn && (combat || grouped)) return eSpellType.Bladeturn;
            return eSpellType.DamageAdd;
        }

        public static int Choose(int primaryId, int activeId, IReadOnlyList<Song> songs)
        {
            Song primary = songs.FirstOrDefault(song => song.Id == primaryId);
            if (primary.Id == 0) return 0;
            // Return to the anchor immediately after the secondary takes. In
            // particular Skald must not leave speed while its 8s reuse is up.
            if (activeId != primaryId)
                return primary.CooldownMilliseconds <= 0 ? primaryId : 0;
            if (primary.CooldownMilliseconds > 0) return 0;

            foreach (Song secondary in songs.Where(song => song.Id != primaryId && song.CooldownMilliseconds <= 0)
                         .OrderBy(song => song.RemainingMilliseconds))
            {
                int roundTrip = Math.Max(0, secondary.CastMilliseconds) + Math.Max(0, primary.CastMilliseconds) + 1000;
                if (primary.RemainingMilliseconds > roundTrip && secondary.RemainingMilliseconds <= roundTrip)
                    return secondary.Id;
            }
            return 0;
        }
    }
}
