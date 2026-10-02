using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace DOL.GS;

// Shared with the launcher. This is a separate ledger, never the game database.
public sealed record RealmEventRecord(string Id, string EventId, string Name, string Kind, string Realm,
    string StartedUtc, string EndedUtc, string Phase, string Outcome, string Details, int Assigned, int Present);

public static class RealmEventRecordStore
{
    private static SQLiteConnection Open(string path, bool readOnly)
    {
        var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource=path, ReadOnly=readOnly,
            FailIfMissing=readOnly, Pooling=false, DefaultTimeout=5 }.ConnectionString);
        connection.Open();
        return connection;
    }
    public static void Save(string path, IEnumerable<RealmEventRecord> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var connection=Open(path,false);
        using (var schema=connection.CreateCommand())
        {
            schema.CommandText="CREATE TABLE IF NOT EXISTS Events(Id TEXT PRIMARY KEY, EventId TEXT NOT NULL, Name TEXT NOT NULL, Kind TEXT NOT NULL, Realm TEXT NOT NULL, StartedUtc TEXT NOT NULL, EndedUtc TEXT NOT NULL, Phase TEXT NOT NULL, Outcome TEXT NOT NULL, Details TEXT NOT NULL, Assigned INTEGER NOT NULL, Present INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS EventTime ON Events(StartedUtc DESC);";
            schema.ExecuteNonQuery();
        }
        using var tx=connection.BeginTransaction();
        foreach(var row in rows)
        {
            using var command=connection.CreateCommand();command.Transaction=tx;
            command.CommandText="INSERT INTO Events VALUES(@id,@event,@name,@kind,@realm,@start,@end,@phase,@outcome,@details,@assigned,@present) ON CONFLICT(Id) DO UPDATE SET EndedUtc=excluded.EndedUtc,Phase=excluded.Phase,Outcome=excluded.Outcome,Details=excluded.Details,Assigned=excluded.Assigned,Present=excluded.Present";
            foreach(var pair in new (string,object)[]{("id",row.Id),("event",row.EventId),("name",row.Name),("kind",row.Kind),("realm",row.Realm),("start",row.StartedUtc),("end",row.EndedUtc),("phase",row.Phase),("outcome",row.Outcome),("details",row.Details),("assigned",row.Assigned),("present",row.Present)}) command.Parameters.AddWithValue("@"+pair.Item1,pair.Item2);
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public static void CloseInterrupted(string path)
    {
        if(!File.Exists(path))return;
        using var connection=Open(path,false);using var command=connection.CreateCommand();
        // The exact end time is unknown after an unclean stop; do not invent it.
        command.CommandText="UPDATE Events SET Outcome='Interrupted',Phase='Stopped',Details=Details || ' | Previous server session ended without a final outcome; end time unknown.' WHERE Outcome='In progress'";
        command.ExecuteNonQuery();
    }
    public static (List<RealmEventRecord> Rows,long Total) Read(string path,string realm,string kind,string outcome,string search,int page,int pageSize=100)
    {
        if(!File.Exists(path))return (new(),0);
        if(page<0 || pageSize<1 || pageSize>500)throw new ArgumentOutOfRangeException(nameof(page));
        using var connection=Open(path,true);
        const string where=" WHERE (@realm='' OR Realm=@realm) AND (@kind='' OR Kind=@kind) AND (@outcome='' OR Outcome=@outcome) AND (@search='' OR instr(lower(Name || ' ' || Details || ' ' || EventId),lower(@search))>0)";
        using var command=connection.CreateCommand();
        command.Parameters.AddWithValue("@realm",realm);command.Parameters.AddWithValue("@kind",kind);command.Parameters.AddWithValue("@outcome",outcome);command.Parameters.AddWithValue("@search",search);
        command.CommandText="SELECT count(*) FROM Events"+where;
        long count=(long)command.ExecuteScalar();
        command.CommandText="SELECT * FROM Events"+where+" ORDER BY COALESCE(NULLIF(StartedUtc,''),EndedUtc) DESC,Id DESC LIMIT @limit OFFSET @offset";
        command.Parameters.AddWithValue("@limit",pageSize);command.Parameters.AddWithValue("@offset",(long)page*pageSize);
        using var reader=command.ExecuteReader();var rows=new List<RealmEventRecord>();
        while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetInt32(10),reader.GetInt32(11)));
        return(rows,count);
    }
}
