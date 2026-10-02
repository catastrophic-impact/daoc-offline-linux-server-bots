using System.Collections.Generic;
using DOL.GS.Effects;

namespace DOL.GS
{
    public class ECSPulseEffect : ECSGameSpellEffect, IConcentrationEffect, IPooledList<ECSPulseEffect>
    {
        /// <summary>
        /// The name of the owner
        /// </summary>
        public override string OwnerName => $"Pulse: {SpellHandler.Spell.Name}";
        public System.Collections.Concurrent.ConcurrentDictionary<GameLiving, ECSGameSpellEffect> ChildEffects { get; } = new();

        public bool RemoveChildIfCurrent(GameLiving owner, ECSGameSpellEffect effect) =>
            ((ICollection<KeyValuePair<GameLiving, ECSGameSpellEffect>>)ChildEffects)
                .Remove(new KeyValuePair<GameLiving, ECSGameSpellEffect>(owner, effect));

        public ECSPulseEffect(in ECSGameEffectInitParams initParams, int pulseFreq)
            : base (initParams)
        {
            PulseFreq = pulseFreq;
            EffectType = eEffect.Pulse;
            StartTick = GameLoop.GameLoopTime;
            NextTick = pulseFreq + GameLoop.GameLoopTime;
        }

        public override void OnStartEffect()
        {
            Spell spell = SpellHandler.Spell;
            Owner.ActivePulseSpells.AddOrUpdate(spell.SpellType, spell, (x, y) => spell);
        }

        public override void OnStopEffect()
        {
            Owner.ActivePulseSpells.TryRemove(SpellHandler.Spell.SpellType, out _);

            if (SpellHandler.Spell.IsFocus)
            {
                foreach (var pair in ChildEffects)
                {
                    ECSGameSpellEffect effect = pair.Value;
                    if (effect.EffectType is eEffect.FocusShield)
                        effect.End();
                }
            }

            ChildEffects.Clear();
        }
    }
}
