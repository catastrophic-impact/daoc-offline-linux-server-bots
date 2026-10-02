using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DOL.Database;
using DOL.Database.Handlers;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_AtomicExchangePersistence
    {
        [Test]
        public void TwoPersistedTables_CommitTogetherAndCleanSnapshots()
        {
            string path = TemporaryPath();
            try
            {
                SqliteObjectDatabase database = Open(path);
                var (permission, proceeds) = Seed(database);
                permission.Command = "after-commit";
                proceeds.Value = "after-commit";

                Assert.That(database.SaveObjectsAtomically([permission, proceeds]), Is.True);
                Assert.That(permission.Dirty, Is.False);
                Assert.That(proceeds.Dirty, Is.False);

                SqliteObjectDatabase fresh = Open(path);
                Assert.That(fresh.SelectAllObjects<DbSinglePermission>().Single().Command, Is.EqualTo("after-commit"));
                Assert.That(fresh.SelectAllObjects<DbCoreCharacterXCustomParam>().Single().Value, Is.EqualTo("after-commit"));
            }
            finally
            {
                DeleteTemporaryFile(path);
            }
        }

        [Test]
        public void MissingSecondRow_RollsBackFirstUpdateAndLeavesSnapshotsDirty()
        {
            string path = TemporaryPath();
            try
            {
                SqliteObjectDatabase database = Open(path);
                var (permission, proceeds) = Seed(database);
                permission.Command = "must-not-commit";
                proceeds.Value = "missing-row-update";

                // Delete a fresh copy so the original persisted snapshot remains a valid
                // update candidate; the atomic write must discover the zero-row update.
                SqliteObjectDatabase deleter = Open(path);
                DbCoreCharacterXCustomParam deleted = deleter.SelectAllObjects<DbCoreCharacterXCustomParam>().Single();
                Assert.That(deleter.DeleteObject(deleted), Is.True);

                Assert.That(database.SaveObjectsAtomically([permission, proceeds]), Is.False);
                Assert.That(permission.Dirty, Is.True);
                Assert.That(proceeds.Dirty, Is.True);

                SqliteObjectDatabase fresh = Open(path);
                Assert.That(fresh.SelectAllObjects<DbSinglePermission>().Single().Command, Is.EqualTo("before"));
                Assert.That(fresh.SelectAllObjects<DbCoreCharacterXCustomParam>(), Is.Empty);
            }
            finally
            {
                DeleteTemporaryFile(path);
            }
        }

        [Test]
        public void ConcurrentWriters_OnOneSqliteDatabase_AreSerializedWithoutLostRows()
        {
            string path = TemporaryPath();
            try
            {
                SqliteObjectDatabase database = Open(path);
                DbSinglePermission[] rows = Enumerable.Range(0, 64)
                    .Select(index => new DbSinglePermission
                    {
                        PlayerID = $"writer-{index}",
                        Command = "before",
                    })
                    .ToArray();
                var results = new ConcurrentBag<bool>();

                Parallel.ForEach(rows, row => results.Add(database.AddObject(row)));
                Assert.That(results, Has.Count.EqualTo(rows.Length));
                Assert.That(results.All(result => result), Is.True,
                    "In-process SQLite writers must queue instead of failing with SQLITE_BUSY.");

                results.Clear();
                Parallel.ForEach(rows, row =>
                {
                    row.Command = "after";
                    results.Add(database.SaveObject(row));
                });
                Assert.That(results.All(result => result), Is.True);

                SqliteObjectDatabase fresh = Open(path);
                DbSinglePermission[] saved = fresh.SelectAllObjects<DbSinglePermission>().ToArray();
                Assert.That(saved, Has.Length.EqualTo(rows.Length));
                Assert.That(saved.All(row => row.Command == "after"), Is.True);
            }
            finally
            {
                DeleteTemporaryFile(path);
            }
        }

        [Test]
        public void WalReadersAndWriters_CanOpenConnectionsConcurrently()
        {
            string path = TemporaryPath();
            try
            {
                var database = new SqliteObjectDatabase($"Data Source={path};Version=3;Pooling=False;Journal Mode=WAL;Synchronous=Normal;Foreign Keys=True;Default Timeout=2");
                database.RegisterDataObject(typeof(DbSinglePermission));
                Parallel.For(0, 240, index =>
                {
                    Assert.That(database.AddObject(new DbSinglePermission
                    {
                        PlayerID = $"wal-{index}", Command = "saved"
                    }), Is.True);
                    Assert.That(database.SelectAllObjects<DbSinglePermission>(), Is.Not.Empty);
                });
                Assert.That(database.SelectAllObjects<DbSinglePermission>().Count, Is.EqualTo(240));
            }
            finally
            {
                DeleteTemporaryFile(path);
                DeleteTemporaryFile(path + "-wal");
                DeleteTemporaryFile(path + "-shm");
            }
        }

        [Test]
        public void PooledWalReadersAndWriters_CanOpenConnectionsConcurrently()
        {
            // The runtime uses pooled connections; reads open without the writer
            // gate once WAL is confirmed, so hammer mixed reads/writes on the pool.
            string path = TemporaryPath();
            try
            {
                var database = new SqliteObjectDatabase($"Data Source={path};Version=3;Pooling=True;Journal Mode=WAL;Synchronous=Normal;Foreign Keys=True;Default Timeout=2");
                database.RegisterDataObject(typeof(DbSinglePermission));
                Parallel.For(0, 480, index =>
                {
                    Assert.That(database.AddObject(new DbSinglePermission
                    {
                        PlayerID = $"pooled-{index}", Command = "saved"
                    }), Is.True);
                    for (int read = 0; read < 3; read++)
                        Assert.That(database.SelectAllObjects<DbSinglePermission>(), Is.Not.Empty);
                });
                Assert.That(database.SelectAllObjects<DbSinglePermission>().Count, Is.EqualTo(480));
            }
            finally
            {
                System.Data.SQLite.SQLiteConnection.ClearAllPools();
                DeleteTemporaryFile(path);
                DeleteTemporaryFile(path + "-wal");
                DeleteTemporaryFile(path + "-shm");
            }
        }

        [Test]
        public void WalletAndProceedsWithSameNumericIdBothCommit_AndOnlyNewSalesCanBeClaimed()
        {
            string path = TemporaryPath();
            try
            {
                SqliteObjectDatabase database = Open(path);
                database.RegisterDataObject(typeof(DbAccountXMoney));
                var wallet = new DbAccountXMoney { AccountId = "claim-test", Realm = 1, Gold = 100 };
                var proceeds = new DbCoreCharacterXCustomParam("claim-character", "RealmExchange.Proceeds.1", "1970000|0|0");
                Assert.That(database.AddObject(wallet), Is.True);
                Assert.That(database.AddObject(proceeds), Is.True);
                Assert.That(wallet.ObjectId, Is.EqualTo(proceeds.ObjectId), "Reproduce colliding auto-increment IDs in different tables.");
                wallet.Gold += 197;
                proceeds.Value = "0|0|0";
                Assert.That(database.SaveObjectsAtomically([wallet, proceeds, wallet]), Is.True);

                SqliteObjectDatabase fresh = Open(path);
                fresh.RegisterDataObject(typeof(DbAccountXMoney));
                Assert.That(fresh.SelectAllObjects<DbAccountXMoney>().Single().Gold, Is.EqualTo(297));
                Assert.That(fresh.SelectAllObjects<DbCoreCharacterXCustomParam>().Single().Value, Is.EqualTo("0|0|0"),
                    "The next claim must read zero, even after a restart or fresh connection.");
                proceeds = fresh.SelectAllObjects<DbCoreCharacterXCustomParam>().Single();
                proceeds.Value = "50000|0|0";
                Assert.That(fresh.SaveObjectsAtomically([proceeds]), Is.True);
                wallet = fresh.SelectAllObjects<DbAccountXMoney>().Single();
                wallet.Gold += 5;
                proceeds.Value = "0|0|0";
                Assert.That(fresh.SaveObjectsAtomically([wallet, proceeds]), Is.True);
                Assert.That(database.SelectAllObjects<DbAccountXMoney>().Single().Gold, Is.EqualTo(302));
                Assert.That(database.SelectAllObjects<DbCoreCharacterXCustomParam>().Single().Value, Is.EqualTo("0|0|0"));
            }
            finally { DeleteTemporaryFile(path); }
        }

        [Test]
        public void MissingProceedsWithCollidingWalletIdRollsBackThePayment()
        {
            string path = TemporaryPath();
            try
            {
                SqliteObjectDatabase database = Open(path);
                database.RegisterDataObject(typeof(DbAccountXMoney));
                var wallet = new DbAccountXMoney { AccountId = "rollback-test", Realm = 1, Gold = 100 };
                var proceeds = new DbCoreCharacterXCustomParam("rollback-character", "RealmExchange.Proceeds.1", "1970000|0|0");
                Assert.That(database.AddObject(wallet), Is.True);
                Assert.That(database.AddObject(proceeds), Is.True);
                Assert.That(wallet.ObjectId, Is.EqualTo(proceeds.ObjectId));
                SqliteObjectDatabase deleter = Open(path);
                Assert.That(deleter.DeleteObject(deleter.SelectAllObjects<DbCoreCharacterXCustomParam>().Single()), Is.True);
                wallet.Gold = 297;
                proceeds.Value = "0|0|0";
                Assert.That(database.SaveObjectsAtomically([wallet, proceeds]), Is.False);
                Assert.That(database.SelectAllObjects<DbAccountXMoney>().Single().Gold, Is.EqualTo(100));
                Assert.That(wallet.Dirty, Is.True);
                Assert.That(proceeds.Dirty, Is.True);
            }
            finally { DeleteTemporaryFile(path); }
        }

        [Test]
        public void BatchInsert_ConstraintDoesNotPoisonFollowingRowsOrLoseSuccessFlags()
        {
            string path = TemporaryPath();
            try
            {
                var database = Open(path);
                DbSinglePermission existing = new() { PlayerID = "existing", Command = "seed" };
                Assert.That(database.AddObject(existing), Is.True);
                DbSinglePermission first = new() { PlayerID = "first", Command = "valid" };
                DbSinglePermission duplicate = new() { ObjectId = existing.ObjectId, PlayerID = "duplicate", Command = "invalid" };
                DbSinglePermission last = new() { PlayerID = "last", Command = "valid" };
                Assert.That(database.AddObject(new[] { first, duplicate, last }), Is.False);
                Assert.That(first.IsPersisted, Is.True);
                Assert.That(duplicate.IsPersisted, Is.False);
                Assert.That(last.IsPersisted, Is.True);
                Assert.That(database.SelectAllObjects<DbSinglePermission>().Count(), Is.EqualTo(3));
            }
            finally { DeleteTemporaryFile(path); }
        }

        private sealed class ConnectionGateProbe : SqliteObjectDatabase
        {
            public ConnectionGateProbe() : base("Data Source=:memory:;Version=3;Pooling=False;") { }
            public object WriterGate => WriteSerializationLock;
            public void OpenReader()
            {
                using var connection = CreateConnection(ConnectionString);
                OpenConnection(connection);
            }
        }

        [Test]
        public void ReaderConnectionSetup_WaitsForInProcessWriter()
        {
            var database = new ConnectionGateProbe();
            using var attempting = new System.Threading.ManualResetEventSlim();
            Task reader;
            bool openedDuringWrite;
            lock (database.WriterGate)
            {
                reader = Task.Run(() =>
                {
                    attempting.Set();
                    database.OpenReader();
                });
                Assert.That(attempting.Wait(TimeSpan.FromSeconds(5)), Is.True);
                openedDuringWrite = reader.Wait(TimeSpan.FromMilliseconds(200));
            }
            Assert.That(reader.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(openedDuringWrite, Is.False,
                "Connection PRAGMAs must not overlap an in-process write transaction.");
        }

        private static (DbSinglePermission Permission, DbCoreCharacterXCustomParam Proceeds) Seed(SqliteObjectDatabase database)
        {
            DbSinglePermission permission = new() { PlayerID = "atomic-player", Command = "before" };
            DbCoreCharacterXCustomParam proceeds = new("atomic-character", "RealmExchange.Proceeds.3", "before");
            Assert.That(database.AddObject([permission, proceeds]), Is.True);
            Assert.That(permission.IsPersisted, Is.True);
            Assert.That(proceeds.IsPersisted, Is.True);
            return (permission, proceeds);
        }

        private static SqliteObjectDatabase Open(string path)
        {
            SqliteObjectDatabase database = new($"Data Source={path};Version=3;Pooling=False;");
            database.RegisterDataObject(typeof(DbSinglePermission));
            database.RegisterDataObject(typeof(DbCoreCharacterXCustomParam));
            return database;
        }

        private static string TemporaryPath() => Path.Combine(Path.GetTempPath(),
            "daoc-atomic-exchange-" + Guid.NewGuid().ToString("N") + ".sqlite3");

        private static void DeleteTemporaryFile(string path)
        {
            // The test creates only this GUID-named SQLite file, never a server database.
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
