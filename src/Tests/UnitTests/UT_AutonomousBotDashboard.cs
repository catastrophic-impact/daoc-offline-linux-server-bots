using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_AutonomousBotDashboard
    {
        [Test]
        public void TenThousandBotSnapshotStaysCompactAndRoundTrips()
        {
            string groupMetadata = new string('g', 400);
            AutonomousBotDashboard.BotStatus[] bots = Enumerable.Range(1, 10_000)
                .Select(id => new AutonomousBotDashboard.BotStatus(id, 50, "Hadrian's Wall",
                    "Traveling in group formation", "Active frontier RvR", "Enemy target",
                    "Dun Caer", "Moving with the warband; 1,250 units remain", true,
                    groupMetadata, "RvR", "rvr-assignment", "2026-09-07T00:00:00Z",
                    "Roaming", "2026-09-07T01:00:00Z"))
                .ToArray();
            var snapshot = new AutonomousBotDashboard.Snapshot(DateTime.UtcNow, true, "test-request", bots);

            var stopwatch = Stopwatch.StartNew();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            stopwatch.Stop();
            AutonomousBotDashboard.Snapshot decoded =
                JsonSerializer.Deserialize<AutonomousBotDashboard.Snapshot>(json);

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded.RequestId, Is.EqualTo("test-request"));
            Assert.That(decoded.Bots, Has.Length.EqualTo(10_000));
            Assert.That(json.Length, Is.LessThan(12 * 1024 * 1024));
            TestContext.Progress.WriteLine($"10,000-bot dashboard snapshot: {json.Length / 1024d / 1024d:0.00} MB serialized in {stopwatch.ElapsedMilliseconds} ms");
        }

        [Test]
        [NonParallelizable]
        public void RequestProducesOneMatchingRollingSnapshot()
        {
            string requestId = Guid.NewGuid().ToString("N");
            string responsePath = AutonomousBotDashboard.FilePath;
            string requestPath = AutonomousBotDashboard.RequestPath;
            string temporaryPath = responsePath + ".tmp";
            try
            {
                File.Delete(responsePath);
                File.Delete(requestPath);
                File.Delete(temporaryPath);
                File.WriteAllText(requestPath, requestId);

                typeof(AutonomousBotDashboard).GetMethod("PollForRequest",
                    BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

                AutonomousBotDashboard.Snapshot response = JsonSerializer.Deserialize<AutonomousBotDashboard.Snapshot>(
                    File.ReadAllBytes(responsePath));
                Assert.That(response.RequestId, Is.EqualTo(requestId));
                Assert.That(response.Running, Is.True);
                Assert.That(Directory.GetFiles(AppContext.BaseDirectory, "bot-world*.json"), Has.Length.EqualTo(1));
                Assert.That(File.Exists(temporaryPath), Is.False);
            }
            finally
            {
                File.Delete(responsePath);
                File.Delete(requestPath);
                File.Delete(temporaryPath);
            }
        }
    }
}
