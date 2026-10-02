using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DOL.Events;

namespace DOL.GS;

// Observational only: lifecycle hooks update tiny immutable records in memory.
// A separate timer writes the separate ledger; no SQL or file I/O on AI turns.
public static class RealmEventRecords
{
    private static readonly object Sync=new(), WriteSync=new();
    private static readonly Dictionary<string,RealmEventRecord> Active=new(), Pending=new();
    private static Timer _writer;
    private static bool _reconcile;
    private static DateTime _nextWarningUtc;
    private static string PathName=>Path.Combine(AppContext.BaseDirectory,"realm-event-records.sqlite3");
    public static void Begin(string eventId,string name,string kind,string realm,string details)
    {
        lock(Sync)
        {
            if(Active.ContainsKey(eventId))return;
            if(Pending.Count>=4096){DOL.Logging.LoggerManager.Create(typeof(RealmEventRecords)).Warn("Realm event ledger backlog full; new record skipped, gameplay unchanged.");return;}
            var row=new RealmEventRecord(Guid.NewGuid().ToString("N"),eventId,name,kind,realm,DateTime.UtcNow.ToString("O"),"","Rally","In progress",details,0,0);
            Active[eventId]=row;Pending[row.Id]=row;
        }
    }
    public static void Progress(string eventId,string phase,string details,int assigned,int present)
    {
        lock(Sync)if(Active.TryGetValue(eventId,out var row))
        { row=row with {Phase=phase,Details=details,Assigned=assigned,Present=present};Active[eventId]=row;Pending[row.Id]=row; }
    }
    public static void Finish(string eventId,string outcome,string details,int assigned=-1,int present=-1)
    {
        lock(Sync)if(Active.Remove(eventId,out var row))
        { row=row with {EndedUtc=DateTime.UtcNow.ToString("O"),Phase="Ended",Outcome=outcome,Details=details,Assigned=assigned<0?row.Assigned:assigned,Present=present<0?row.Present:present};Pending[row.Id]=row; }
    }
    [GameServerStartedEvent]
    public static void Start(DOLEvent e,object sender,EventArgs args)
    {
        _writer?.Dispose();lock(Sync){Active.Clear();Pending.Clear();_reconcile=true;}
        _writer=new Timer(_=>Flush(),null,1000,2000);
    }
    [GameServerStoppedEvent]
    public static void Stop(DOLEvent e,object sender,EventArgs args)
    {
        _writer?.Dispose();_writer=null;
        lock(Sync)foreach(string id in Active.Keys.ToArray())Finish(id,"Interrupted","Server stopped before the event reached a final outcome.");
        Flush(true);
    }
    private static void Flush(bool drain = false)
    {
        if(drain)Monitor.Enter(WriteSync);
        else if(!Monitor.TryEnter(WriteSync))return;
        try
        {
            if(_reconcile){RealmEventRecordStore.CloseInterrupted(PathName);_reconcile=false;}
            do
            {
                KeyValuePair<string,RealmEventRecord>[] batch;
                lock(Sync)batch=Pending.Take(128).ToArray();
                if(batch.Length==0)return;
                RealmEventRecordStore.Save(PathName,batch.Select(p=>p.Value));
                lock(Sync)foreach(var item in batch)
                    if(Pending.TryGetValue(item.Key,out var current)&&ReferenceEquals(current,item.Value))Pending.Remove(item.Key);
            } while(drain);
        }
        catch(Exception ex)
        {
            if(DateTime.UtcNow>=_nextWarningUtc)
            { _nextWarningUtc=DateTime.UtcNow.AddMinutes(1);DOL.Logging.LoggerManager.Create(typeof(RealmEventRecords)).Warn("Realm event ledger write failed; queued records retained for retry.",ex); }
        }
        finally{Monitor.Exit(WriteSync);}
    }
}
