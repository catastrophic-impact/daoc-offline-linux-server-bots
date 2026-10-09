using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_BotCrossRealmGear
    {
        private static DbItemTemplate Template(string id) => new() { Id_nb = id };

        [TestCase("woolen_woven_pointed_steeple_hat3")]   // Midgard hat on the Hibernia model 1279 (no Valkyn mesh)
        [TestCase("SYLVAN_WOVEN_POINTED_STEEPLE_HAT3")]   // ids match case-insensitively
        [TestCase("cailiocht_helm3")]                     // Midgard helm on the Hibernia studded cap
        [TestCase("2_FDK_Shield")]                        // Midgard shield on an Albion kite shield
        public void TemplatesOnAnotherRealmsModelAreSkippedByBots(string id) =>
            Assert.That(BotCrossRealmGear.IsExcluded(Template(id)), Is.True);

        [TestCase("woolen_padded_pointed_steeple_hat")]   // Midgard hat on the Midgard model 1280
        [TestCase("woolen_woven_pointed_steeple_hat")]    // Hibernia hat on the Hibernia model
        [TestCase("")]
        public void MatchingRealmTemplatesStayAvailable(string id) =>
            Assert.That(BotCrossRealmGear.IsExcluded(Template(id)), Is.False);

        [Test]
        public void NullTemplateIsNotExcluded() => Assert.That(BotCrossRealmGear.IsExcluded(null), Is.False);

        [Test]
        public void GeneratedListHasTheAuditedTemplates() => Assert.That(BotCrossRealmGear.Count, Is.EqualTo(256));
    }
}
