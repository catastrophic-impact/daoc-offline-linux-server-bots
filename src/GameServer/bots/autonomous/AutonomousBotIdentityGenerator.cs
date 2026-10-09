using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>
    /// Generates era-valid identities for autonomous Classic + Shrouded Isles bots.
    /// This class never creates or spawns a bot; it only produces an identity when
    /// the population controller is explicitly asked to create one.
    /// </summary>
    public static class AutonomousBotIdentityGenerator
    {
        public sealed record Identity(string Name, eRealm Realm, eGender Gender, eCharacterClass CharacterClass, eRace Race);

        private sealed record ClassChoice(eCharacterClass CharacterClass, params eRace[] Races);

        private static readonly IReadOnlyDictionary<eRealm, ClassChoice[]> Choices =
            new Dictionary<eRealm, ClassChoice[]>
            {
                [eRealm.Albion] =
                [
                    new(eCharacterClass.Paladin, eRace.Avalonian, eRace.Briton, eRace.Highlander, eRace.Saracen),
                    new(eCharacterClass.Armsman, eRace.Avalonian, eRace.Briton, eRace.Highlander, eRace.Inconnu, eRace.Saracen),
                    new(eCharacterClass.Scout, eRace.Briton, eRace.Highlander, eRace.Inconnu, eRace.Saracen),
                    new(eCharacterClass.Minstrel, eRace.Briton, eRace.Highlander, eRace.Saracen),
                    new(eCharacterClass.Theurgist, eRace.Avalonian, eRace.Briton),
                    new(eCharacterClass.Cleric, eRace.Avalonian, eRace.Briton, eRace.Highlander),
                    new(eCharacterClass.Wizard, eRace.Avalonian, eRace.Briton),
                    new(eCharacterClass.Sorcerer, eRace.Avalonian, eRace.Briton, eRace.Inconnu, eRace.Saracen),
                    new(eCharacterClass.Infiltrator, eRace.Briton, eRace.Inconnu, eRace.Saracen),
                    new(eCharacterClass.Friar, eRace.Briton),
                    new(eCharacterClass.Mercenary, eRace.Avalonian, eRace.Briton, eRace.Highlander, eRace.Inconnu, eRace.Saracen),
                    new(eCharacterClass.Necromancer, eRace.Briton, eRace.Inconnu, eRace.Saracen),
                    new(eCharacterClass.Cabalist, eRace.Avalonian, eRace.Briton, eRace.Inconnu, eRace.Saracen),
                    new(eCharacterClass.Reaver, eRace.Briton, eRace.Inconnu, eRace.Saracen),
                ],
                [eRealm.Midgard] =
                [
                    new(eCharacterClass.Thane, eRace.Dwarf, eRace.Norseman, eRace.Troll),
                    new(eCharacterClass.Warrior, eRace.Dwarf, eRace.Kobold, eRace.Norseman, eRace.Troll, eRace.Valkyn),
                    new(eCharacterClass.Shadowblade, eRace.Kobold, eRace.Norseman, eRace.Valkyn),
                    new(eCharacterClass.Skald, eRace.Dwarf, eRace.Kobold, eRace.Norseman, eRace.Troll),
                    new(eCharacterClass.Hunter, eRace.Dwarf, eRace.Kobold, eRace.Norseman, eRace.Valkyn),
                    new(eCharacterClass.Healer, eRace.Dwarf, eRace.Norseman),
                    new(eCharacterClass.Spiritmaster, eRace.Kobold, eRace.Norseman),
                    new(eCharacterClass.Shaman, eRace.Kobold, eRace.Troll),
                    new(eCharacterClass.Runemaster, eRace.Dwarf, eRace.Kobold, eRace.Norseman),
                    new(eCharacterClass.Bonedancer, eRace.Kobold, eRace.Troll, eRace.Valkyn),
                    new(eCharacterClass.Berserker, eRace.Dwarf, eRace.Norseman, eRace.Troll, eRace.Valkyn),
                    new(eCharacterClass.Savage, eRace.Dwarf, eRace.Kobold, eRace.Norseman, eRace.Troll, eRace.Valkyn),
                ],
                [eRealm.Hibernia] =
                [
                    new(eCharacterClass.Eldritch, eRace.Elf, eRace.Lurikeen),
                    new(eCharacterClass.Enchanter, eRace.Elf, eRace.Lurikeen),
                    new(eCharacterClass.Mentalist, eRace.Celt, eRace.Elf, eRace.Lurikeen),
                    new(eCharacterClass.Blademaster, eRace.Celt, eRace.Elf, eRace.Firbolg),
                    new(eCharacterClass.Hero, eRace.Celt, eRace.Firbolg, eRace.Lurikeen, eRace.Sylvan),
                    new(eCharacterClass.Champion, eRace.Celt, eRace.Elf, eRace.Lurikeen),
                    new(eCharacterClass.Warden, eRace.Celt, eRace.Firbolg, eRace.Sylvan),
                    new(eCharacterClass.Druid, eRace.Celt, eRace.Firbolg, eRace.Sylvan),
                    new(eCharacterClass.Bard, eRace.Celt, eRace.Firbolg),
                    new(eCharacterClass.Nightshade, eRace.Elf, eRace.Lurikeen),
                    new(eCharacterClass.Ranger, eRace.Celt, eRace.Elf, eRace.Lurikeen),
                    new(eCharacterClass.Animist, eRace.Celt, eRace.Firbolg, eRace.Sylvan),
                    new(eCharacterClass.Valewalker, eRace.Celt, eRace.Firbolg, eRace.Sylvan),
                    // Experimental Sluaghbinder: the isolated class has the
                    // same two legal races as its player character creator.
                    new(eCharacterClass.Sluaghbinder, eRace.Celt, eRace.Firbolg),
                ],
            };

        private static readonly IReadOnlyDictionary<(eRealm Realm, eGender Gender), (string[] Starts, string[] Middles, string[] Ends)> NameParts =
            new Dictionary<(eRealm, eGender), (string[], string[], string[])>
            {
                [(eRealm.Albion, eGender.Male)] =
                    (["Ald", "Ber", "Ced", "Ed", "Gar", "God", "Har", "Leof", "Os", "Ran", "Ren", "Thed", "Wil"],
                     ["", "a", "e", "en", "er", "i", "o"],
                     ["bert", "ric", "win", "mund", "ward", "frey", "ren", "well", "red", "helm"]),
                [(eRealm.Albion, eGender.Female)] =
                    (["Ada", "Ael", "Ela", "Eve", "Gis", "Isa", "Lina", "Mara", "Ros", "Ser", "Thea", "Ys"],
                     ["", "a", "e", "el", "en", "i", "o"],
                     ["bel", "beth", "lyn", "wen", "ette", "ine", "ora", "elle", "ith", "anne"]),
                [(eRealm.Midgard, eGender.Male)] =
                    (["Arn", "Bjorn", "Dag", "Egil", "Eirik", "Finn", "Gorm", "Hald", "Ivar", "Knut", "Rag", "Sig", "Tor", "Ulf"],
                     ["", "a", "e", "ge", "i", "un"],
                     ["ar", "bjorn", "brand", "grim", "mund", "rik", "sten", "ulf", "vald", "var"]),
                [(eRealm.Midgard, eGender.Female)] =
                    (["Astr", "Bry", "Dag", "Eir", "Fre", "Gud", "Hild", "Ing", "Kara", "Liv", "Rag", "Sig", "Siv", "Yr"],
                     ["", "a", "e", "i", "un"],
                     ["a", "dis", "frid", "gerd", "hild", "run", "rid", "veig", "borg", "ny"]),
                [(eRealm.Hibernia, eGender.Male)] =
                    (["Aed", "Bran", "Cael", "Ciar", "Con", "Dair", "Eog", "Ferg", "Lorc", "Nial", "Ois", "Rian", "Tad"],
                     ["", "a", "e", "el", "in", "o"],
                     ["an", "dan", "gan", "gus", "lan", "nan", "ren", "ric", "ron", "van"]),
                [(eRealm.Hibernia, eGender.Female)] =
                    (["Aine", "Bria", "Cao", "Deir", "Eil", "Fia", "Mae", "Muir", "Nia", "Orla", "Rois", "Sao", "Una"],
                     ["", "a", "e", "el", "in", "ri"],
                     ["a", "bhe", "dra", "la", "lin", "na", "ra", "rin", "se", "wen"]),
            };

        private static readonly IReadOnlyDictionary<eRealm, string[]> NameBridges = new Dictionary<eRealm, string[]>
        {
            [eRealm.Albion] = ["", "al", "en", "is", "or"],
            [eRealm.Midgard] = ["", "ar", "en", "ild", "un"],
            [eRealm.Hibernia] = ["", "ae", "el", "in", "or"],
        };

        public static Identity Generate(eRealm realm, eGender gender, ISet<string> reservedNames = null, Random random = null)
        {
            if (!Choices.TryGetValue(realm, out ClassChoice[] realmChoices))
                throw new ArgumentOutOfRangeException(nameof(realm), "Autonomous bots must belong to Albion, Midgard, or Hibernia.");
            if (gender is not eGender.Male and not eGender.Female)
                throw new ArgumentOutOfRangeException(nameof(gender), "Autonomous bots must have a male or female identity.");

            random ??= Random.Shared;
            realmChoices = ActiveChoices(realmChoices);
            ClassChoice classChoice = realmChoices[random.Next(realmChoices.Length)];
            eRace race = classChoice.Races[random.Next(classChoice.Races.Length)];
            string name = GenerateUniqueName(realm, gender, reservedNames, random);
            return new Identity(name, realm, gender, classChoice.CharacterClass, race);
        }

        public static Identity GenerateForClass(
            eRealm realm,
            eGender gender,
            eCharacterClass characterClass,
            ISet<string> reservedNames = null,
            Random random = null)
        {
            if (!Choices.TryGetValue(realm, out ClassChoice[] realmChoices))
                throw new ArgumentOutOfRangeException(nameof(realm));
            ClassChoice choice = ActiveChoices(realmChoices).FirstOrDefault(entry => entry.CharacterClass == characterClass);
            if (choice == null)
                throw new ArgumentException($"{characterClass} is not a Classic + SI {GlobalConstants.RealmToName(realm)} class.", nameof(characterClass));
            if (gender is not eGender.Male and not eGender.Female)
                throw new ArgumentOutOfRangeException(nameof(gender));

            random ??= Random.Shared;
            eRace race = choice.Races[random.Next(choice.Races.Length)];
            return new Identity(
                GenerateUniqueName(realm, gender, reservedNames, random),
                realm,
                gender,
                characterClass,
                race);
        }

        public static IReadOnlyCollection<eCharacterClass> GetEraClasses(eRealm realm) =>
            Choices.TryGetValue(realm, out ClassChoice[] choices)
                ? ActiveChoices(choices).Select(choice => choice.CharacterClass).ToArray()
                : Array.Empty<eCharacterClass>();

        /// <summary>False only for the Sluaghbinder in the 0.33 "no custom class" edition.</summary>
        public static bool IsClassAvailable(eCharacterClass characterClass) =>
            characterClass != eCharacterClass.Sluaghbinder || ServerProperties.Properties.ENABLE_SLUAGHBINDER;

        // With the custom class enabled (0.33b, the default) the original array is used unchanged,
        // so bot rolls are identical to the Claude version build.
        private static ClassChoice[] ActiveChoices(ClassChoice[] choices) =>
            ServerProperties.Properties.ENABLE_SLUAGHBINDER
                ? choices
                : choices.Where(choice => IsClassAvailable(choice.CharacterClass)).ToArray();

        public static IReadOnlyCollection<eRace> GetEligibleRaces(eCharacterClass characterClass) =>
            Choices.Values.SelectMany(value => value)
                .FirstOrDefault(choice => choice.CharacterClass == characterClass)?.Races ?? Array.Empty<eRace>();

        private static string GenerateUniqueName(eRealm realm, eGender gender, ISet<string> reservedNames, Random random)
        {
            var parts = NameParts[(realm, gender)];
            string[] bridges = NameBridges[realm];
            for (int attempt = 0; attempt < 1024; attempt++)
            {
                string candidate = parts.Starts[random.Next(parts.Starts.Length)] +
                                   parts.Middles[random.Next(parts.Middles.Length)] +
                                   bridges[random.Next(bridges.Length)] +
                                   parts.Ends[random.Next(parts.Ends.Length)];
                candidate = Normalize(candidate);
                if (reservedNames == null || reservedNames.Add(candidate))
                    return candidate;
            }

            // High population rosters must not fail merely because random rolls
            // repeatedly hit occupied names. Exhaust the themed pool deterministically.
            foreach (string start in parts.Starts)
            foreach (string middle in parts.Middles)
            foreach (string bridge in bridges)
            foreach (string end in parts.Ends)
            {
                string candidate = Normalize(start + middle + bridge + end);
                if (reservedNames == null || reservedNames.Add(candidate))
                    return candidate;
            }

            throw new InvalidOperationException($"Unable to produce another unique {GlobalConstants.RealmToName(realm)} {gender} bot name.");
        }

        private static string Normalize(string name)
        {
            string collapsed = name.Replace("aa", "a", StringComparison.OrdinalIgnoreCase)
                .Replace("ee", "e", StringComparison.OrdinalIgnoreCase)
                .Replace("ii", "i", StringComparison.OrdinalIgnoreCase);
            return char.ToUpperInvariant(collapsed[0]) + collapsed[1..].ToLowerInvariant();
        }
    }
}
