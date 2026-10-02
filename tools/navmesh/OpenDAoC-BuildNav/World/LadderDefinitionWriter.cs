using System.Text.Json;
using CEM.Utils;

namespace CEM.World
{
    internal static class LadderDefinitionWriter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public static void Write(string path, int zoneId, IReadOnlyList<LadderDefinition> ladders)
        {
            LadderDefinitionFile file = new()
            {
                ZoneId = zoneId,
                Ladders = ladders.Select(LadderDefinitionDto.From).ToList(),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
            Log.Normal($"Wrote {ladders.Count} ladder definition(s) to {path}");
        }

        public static LadderDefinitionFile? TryRead(string path)
        {
            if (!File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LadderDefinitionFile>(json, JsonOptions);
        }
    }
}
