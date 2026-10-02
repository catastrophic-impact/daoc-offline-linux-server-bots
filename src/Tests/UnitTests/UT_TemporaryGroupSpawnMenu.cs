using System.Linq;
using System.Text.RegularExpressions;
using DOL.GS;
using DOL.GS.Commands;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_TemporaryGroupSpawnMenu
    {
        [TestCase(eRealm.Albion)]
        [TestCase(eRealm.Midgard)]
        [TestCase(eRealm.Hibernia)]
        public void MenuContainsEveryAndOnlyCurrentRealmClass(eRealm realm)
        {
            string[] expected = TemporaryGroupClassCatalog.ForRealm(realm)
                .Select(entry => entry.CharacterClass.ToString())
                .ToArray();
            string[] links = Regex.Matches(TemporaryGroupSpawnMenu.BuildMenuText(realm), @"\[([^\]]+)\]")
                .Select(match => match.Groups[1].Value)
                .ToArray();

            Assert.That(links, Is.EquivalentTo(expected));
            Assert.That(links, Has.Length.EqualTo(expected.Length));
        }

        [TestCase(eRealm.Albion)]
        [TestCase(eRealm.Midgard)]
        [TestCase(eRealm.Hibernia)]
        public void EveryClickableLabelResolvesToItsClass(eRealm realm)
        {
            foreach ((eCharacterClass expected, _) in TemporaryGroupClassCatalog.ForRealm(realm))
            {
                Assert.That(TemporaryGroupClassCatalog.TryResolve(realm, expected.ToString(), out eCharacterClass actual),
                    Is.True);
                Assert.That(actual, Is.EqualTo(expected));
            }
        }
    }
}
