using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DOL.GS;

/// <summary>
/// Read-only view of the local DAoC character quickbars. This never writes an
/// INI file and is used only by the human player's opt-in /bot pilot.
/// </summary>
public sealed class AutonomousPlayerQuickbarProfile
{
    private readonly HashSet<int> _skillIds;
    private readonly HashSet<string> _skillNames;

    public string SourcePath { get; }
    public DateTime LastWriteUtc { get; }
    public int ActionCount => _skillIds.Count + _skillNames.Count;

    private AutonomousPlayerQuickbarProfile(
        string sourcePath,
        DateTime lastWriteUtc,
        HashSet<int> skillIds,
        HashSet<string> skillNames)
    {
        SourcePath = sourcePath;
        LastWriteUtc = lastWriteUtc;
        _skillIds = skillIds;
        _skillNames = skillNames;
    }

    public bool Allows(Skill skill) => skill != null &&
        (_skillIds.Contains(skill.ID) || _skillNames.Contains(skill.Name ?? string.Empty));

    public static bool TryLoad(GamePlayer player, out AutonomousPlayerQuickbarProfile profile, out string error)
    {
        profile = null;
        error = string.Empty;
        if (player == null || string.IsNullOrWhiteSpace(player.Name))
        {
            error = "No active character was available for quickbar lookup.";
            return false;
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Electronic Arts",
            "Dark Age of Camelot");
        if (!Directory.Exists(root))
        {
            error = "The local Dark Age of Camelot settings folder does not exist yet.";
            return false;
        }

        string expectedPrefix = player.Name + "-";
        FileInfo source;
        try
        {
            source = Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            error = $"The quickbar settings could not be read: {exception.Message}";
            return false;
        }

        if (source == null)
        {
            error = $"No saved quickbar file was found for {player.Name}. Log in manually once and place the desired actions on a quickbar first.";
            return false;
        }

        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inQuickbarSection = false;
        try
        {
            foreach (string rawLine in File.ReadLines(source.FullName))
            {
                string line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inQuickbarSection = line.StartsWith("[Quickbar", StringComparison.OrdinalIgnoreCase) &&
                                        !line.Equals("[QuickBinds]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inQuickbarSection || !line.StartsWith("Hotkey_", StringComparison.OrdinalIgnoreCase))
                    continue;

                int equals = line.IndexOf('=');
                if (equals < 0 || equals == line.Length - 1)
                    continue;
                string[] fields = line[(equals + 1)..].Split(',');
                if (fields.Length < 3)
                    continue;

                if (int.TryParse(fields[^1].Trim(), out int id) && id > 0)
                    ids.Add(id);
                string name = string.Join(',', fields.Skip(2).Take(fields.Length - 3)).Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch (Exception exception)
        {
            error = $"The quickbar file could not be parsed: {exception.Message}";
            return false;
        }

        if (ids.Count == 0 && names.Count == 0)
        {
            error = $"{source.Name} has no usable actions in its Quickbar sections.";
            return false;
        }

        profile = new(source.FullName, source.LastWriteTimeUtc, ids, names);
        return true;
    }
}
