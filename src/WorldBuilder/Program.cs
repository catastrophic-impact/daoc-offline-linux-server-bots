using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Database.Attributes;
using DataTableAttribute = DOL.Database.Attributes.DataTable;
using DOL.Database.Connection;
using DOL.Logging;

namespace Daoc.WorldBuilder;

/// <summary>
///   DaocWorldBuilder --source &lt;opendaoc-db-core dir&gt; --out &lt;world.sqlite&gt; [--patches &lt;dir&gt;]
///
/// 1. Creates every table from the server's own DataObjects (exactly the schema the server expects).
/// 2. Loads the rows of every *.sql dump file in --source.
/// 3. Runs the *.sql files in --patches in name order (our versioned world changes), each in a transaction.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string? source = Option(args, "--source"), output = Option(args, "--out"), patches = Option(args, "--patches");
        if (source == null || output == null)
        {
            Console.Error.WriteLine("usage: DaocWorldBuilder --source <opendaoc-db-core dir> --out <world.sqlite> [--patches <dir>]");
            return 2;
        }
        if (File.Exists(output))
        {
            Console.Error.WriteLine($"{output} already exists; refusing to overwrite a world.");
            return 3;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            LoggerManager.Initialize(Path.Combine(AppContext.BaseDirectory, "logconfig.xml"));
            string connection = $"Data Source={output};Version=3;Pooling=False;Journal Mode=Off;Synchronous=Off";

            int tables = CreateSchema(connection);
            Console.WriteLine($"Schema: {tables} tables from the server's DataObjects ({clock.Elapsed.TotalSeconds:F0}s)");

            using var db = new SQLiteConnection(connection);
            db.Open();
            ImportDump(db, source);
            Console.WriteLine($"Import done ({clock.Elapsed.TotalSeconds:F0}s)");

            if (patches != null)
                ApplyPatches(db, patches);

            using (var vacuum = db.CreateCommand())
            {
                vacuum.CommandText = "ANALYZE; VACUUM;";
                vacuum.ExecuteNonQuery();
            }
            Console.WriteLine($"World written to {output} ({new FileInfo(output).Length / 1048576} MB, {clock.Elapsed.TotalSeconds:F0}s)");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"World build failed: {exception.Message}");
            try { File.Delete(output); } catch (IOException) { }
            return 1;
        }
    }

    /// <summary>Registers every [DataTable] class, as GameServer.InitDB does, which creates the tables.</summary>
    private static int CreateSchema(string connection)
    {
        IObjectDatabase database = ObjectDatabase.GetObjectDatabase(EConnectionType.DATABASE_SQLITE, connection);
        // The assemblies holding DataObjects: CoreDatabase and GameServer (which includes the bots).
        Assembly[] assemblies =
        [
            typeof(DbAccount).Assembly,
            typeof(DOL.GS.GameServer).Assembly,
        ];
        int count = 0;
        foreach (Type type in assemblies.SelectMany(a => a.GetTypes()))
        {
            if (!type.IsClass || type.IsAbstract || !type.GetCustomAttributes<DataTableAttribute>(false).Any())
                continue;
            database.RegisterDataObject(type);
            count++;
        }
        return count;
    }

    private static void ImportDump(SQLiteConnection db, string source)
    {
        // Table and column names in the dump are lower-case; SQLite matches them case-insensitively,
        // but we look them up to skip anything the server does not use.
        Dictionary<string, HashSet<string>> schema = ReadSchema(db);
        var skippedTables = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedColumns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.GetFiles(source, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            long rows = 0;
            using var transaction = db.BeginTransaction();
            foreach (DumpInsert insert in new MySqlDumpReader(File.ReadAllText(file)).ReadInserts())
            {
                if (!schema.TryGetValue(insert.Table, out HashSet<string>? columns))
                {
                    skippedTables.Add(insert.Table);
                    continue;
                }
                int[] keep = Enumerable.Range(0, insert.Columns.Count).Where(i => columns.Contains(insert.Columns[i])).ToArray();
                foreach (int i in Enumerable.Range(0, insert.Columns.Count).Except(keep))
                    skippedColumns.Add($"{insert.Table}.{insert.Columns[i]}");

                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"INSERT OR REPLACE INTO `{insert.Table}` ({string.Join(", ", keep.Select(i => $"`{insert.Columns[i]}`"))}) " +
                    $"VALUES ({string.Join(", ", keep.Select(i => $"@p{i}"))})";
                SQLiteParameter[] parameters = keep.Select(i => new SQLiteParameter($"@p{i}")).ToArray();
                command.Parameters.AddRange(parameters);
                foreach (object?[] row in insert.Rows)
                {
                    if (row.Length != insert.Columns.Count)
                        throw new InvalidDataException($"{Path.GetFileName(file)}: a {insert.Table} row has {row.Length} values for {insert.Columns.Count} columns");
                    for (int p = 0; p < keep.Length; p++)
                        parameters[p].Value = row[keep[p]] ?? DBNull.Value;
                    command.ExecuteNonQuery();
                    rows++;
                }
            }
            transaction.Commit();
            if (rows > 0)
                Console.WriteLine($"  {Path.GetFileName(file),-40} {rows,9:N0} rows");
        }

        if (skippedTables.Count > 0)
            Console.WriteLine($"Skipped tables the server does not define: {string.Join(", ", skippedTables)}");
        if (skippedColumns.Count > 0)
            Console.WriteLine($"Skipped columns the server does not define: {string.Join(", ", skippedColumns)}");
    }

    private static Dictionary<string, HashSet<string>> ReadSchema(SQLiteConnection db)
    {
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var tables = db.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var names = new List<string>();
        using (IDataReader reader = tables.ExecuteReader())
            while (reader.Read())
                names.Add(reader.GetString(0));
        foreach (string table in names)
        {
            using var info = db.CreateCommand();
            info.CommandText = $"PRAGMA table_info(`{table}`)";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using IDataReader reader = info.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
            schema[table] = columns;
        }
        return schema;
    }

    private static void ApplyPatches(SQLiteConnection db, string directory)
    {
        foreach (string file in Directory.GetFiles(directory, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = File.ReadAllText(file);
            int changed = command.ExecuteNonQuery();
            transaction.Commit();
            Console.WriteLine($"Patch {Path.GetFileName(file)}: {changed:N0} rows changed");
        }
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
