using System.Linq;
using DOL.GS;
using DOL.GS.Commands;
using NUnit.Framework;

namespace DOL.Tests.UnitTests
{
    [TestFixture]
    public class UT_StablesCommand
    {
        [TestCase(eRealm.Albion, 1, 51)]
        [TestCase(eRealm.Midgard, 100, 151)]
        [TestCase(eRealm.Hibernia, 200, 181)]
        public void EachRealmShowsItsOwnClassicAndIslesNetwork(eRealm realm, int classic, int isles)
        {
            Assert.That(StablesCommandHandler.TryGetRegions(realm, out ushort actualClassic, out ushort actualIsles), Is.True);
            Assert.That((actualClassic, actualIsles), Is.EqualTo(((ushort)classic, (ushort)isles)));
        }

        [Test]
        public void PagingPreservesEveryConnectionInOrder()
        {
            string[] routes = Enumerable.Range(1, 60)
                .Select(index => $"Stable {index} -> destination {index}").ToArray();

            var pages = StablesCommandHandler.PaginateLines(routes, 150);

            Assert.That(pages.Count, Is.GreaterThan(1));
            Assert.That(pages.SelectMany(page => page), Is.EqualTo(routes));
            Assert.That(pages.All(page => page.Length <= 20 &&
                page.Sum(line => line.Length + 2) <= 150), Is.True);
        }

        [TestCase("CamelotNoth", "Camelot North")]
        [TestCase("Cotswold", "Cotswold Village")]
        [TestCase("IarnDwarf", "Iarn Dwarf Camp")]
        [TestCase("ParthenonFarm", "East Lough Derg")]
        public void SourceNamesExplainLegacyTicketIdentifiers(string token, string expected)
        {
            Assert.That(StablesCommandHandler.HumanizeSource(token), Is.EqualTo(expected));
        }
    }
}
