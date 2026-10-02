using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_AutonomousBotChat
{
    [TestCase("hey whats up", eAutonomousChatIntent.Greeting)]
    [TestCase("you guys suck", eAutonomousChatIntent.Banter)]
    [TestCase("you guys are shit and dumb", eAutonomousChatIntent.Abuse)]
    [TestCase("anyone want a dungeon group", eAutonomousChatIntent.Dungeon)]
    [TestCase("where can I sell this gear", eAutonomousChatIntent.Trading)]
    [TestCase("who won the presidential election", eAutonomousChatIntent.IgnoreOutOfWorld)]
    [TestCase("what are bitcoin stocks doing", eAutonomousChatIntent.IgnoreOutOfWorld)]
    public void IntentModel_SeparatesDaocChatterFromRealWorldTopics(string text, eAutonomousChatIntent expected)
    {
        Assert.That(AutonomousChatIntentModel.Predict(text), Is.EqualTo(expected));
    }

    [Test]
    public void ConversationCoordinator_NeverCreatesAResponsePileOn()
    {
        for (int eligible = 0; eligible < 200; eligible++)
            Assert.That(AutonomousBotChatCoordinator.RollResponseCount(eligible, new Random(eligible)), Is.InRange(0, Math.Min(2, eligible)));
    }

    [Test]
    public void ConversationCoordinator_SuppressesBackToBackDuplicateAnswers()
    {
        HashSet<string> scheduled = new(StringComparer.OrdinalIgnoreCase);
        Assert.That(AutonomousBotChatCoordinator.TryAddDistinctResponse(scheduled, "  The frontier is active. ", out string first), Is.True);
        Assert.That(first, Is.EqualTo("The frontier is active."));
        Assert.That(AutonomousBotChatCoordinator.TryAddDistinctResponse(scheduled, "the frontier is active.", out _), Is.False);
        Assert.That(scheduled, Has.Count.EqualTo(1));
    }

    [TestCase("who won the presidential election")]
    [TestCase("you are all shit and dumb")]
    public void ModeratedTopics_GetOneShortNonEscalatingReply(string text)
    {
        string response = AutonomousBotChatCoordinator.GenerateFactionReply(text, new Random(3));
        Assert.That(response, Is.Not.Empty);
        Assert.That(response.Length, Is.LessThan(80));
    }

    [TestCase("How much do u think jeweled frog eye is worth?")]
    [TestCase("Does anybody want to make me an offer on iron sword?")]
    [TestCase("Why did Mythic make Sorcerer so OP?")]
    [TestCase("Why is Bonedancer so broken in PvP?")]
    [TestCase("Anybody else just DC?")]
    public void PlayfulTopics_GetShortConversationReplies(string text)
    {
        string response = AutonomousBotChatCoordinator.GenerateFactionReply(text, new Random(9));
        Assert.That(response, Is.Not.Empty);
        Assert.That(response.Length, Is.LessThan(100));
    }

    [TestCase("yo ciaradella can you stop complaining noob", "Ciaradella", true)]
    [TestCase("YO CIARADELLA CAN YOU STOP", "ciaradella", true)]
    [TestCase("Ciaradella?", "Ciaradella", true)]
    [TestCase("yo NotCiaradella", "Ciaradella", false)]
    public void DirectMentions_AreCaseInsensitiveWholeNames(string message, string name, bool expected)
    {
        Assert.That(AutonomousBotChatCoordinator.ContainsMention(message, name), Is.EqualTo(expected));
    }
    [Test]
    public void Generate_UsesLiveMonsterOrNpcContext()
    {
        var context = new AutonomousBotChat.Context("Aldric", "Paladin", "Camelot Hills", "bandit", "Trainer Jocelin", "iron sword", "weaponcrafting", 12, 1, eRealm.Albion);
        string combined = string.Join(" ", Enumerable.Range(0, 80).Select(index => AutonomousBotChat.Generate(context, new Random(index))));
        Assert.That(combined, Does.Contain("bandit"));
        Assert.That(combined, Does.Contain("Trainer Jocelin"));
    }

    [Test]
    public void ShouldSpeak_IsRareAndHonorsCooldown()
    {
        DateTime now = DateTime.UtcNow;
        Assert.That(AutonomousBotChat.ShouldSpeak(now, now.AddMinutes(-1), false, new Random(1)), Is.False);
        Assert.That(AutonomousBotChat.ShouldSpeak(now, now.AddHours(-1), true, new Random(1)), Is.False);
    }

    [Test]
    public void EmittedFactionChat_IsPositiveAndHasNoObsoleteGoReference()
    {
        FieldInfo openersField = typeof(AutonomousBotChatCoordinator).GetField("FactionOpeners", BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo repliesField = typeof(AutonomousBotChatCoordinator).GetField("FactionReplies", BindingFlags.NonPublic | BindingFlags.Static);
        var emitted = new List<string>();
        emitted.AddRange((string[])openersField.GetValue(null));
        emitted.AddRange((string[])repliesField.GetValue(null));

        string[] prompts =
        [
            "How much is this sword worth?", "Why is this class broken?", "Anybody else lagging?",
            "hello realm", "you are all shit and dumb", "where is the dungeon?", "any groups forming?",
        ];
        foreach (string prompt in prompts)
            for (int seed = 0; seed < 32; seed++)
                emitted.Add(AutonomousBotChatCoordinator.GenerateFactionReply(prompt, new Random(seed)));

        string[] forbidden = ["noob", "idiot", "moron", "stfu", "game sucks", "paid a sub", "skill issue", "/go"];
        foreach (string message in emitted)
            foreach (string phrase in forbidden)
                Assert.That(message, Does.Not.Contain(phrase).IgnoreCase, $"Unsafe faction chat: {message}");
    }
}
