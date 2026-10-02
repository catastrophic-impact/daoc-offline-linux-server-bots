using System.Text.Json.Serialization;
using OpenTK;

namespace CEM.World
{
    /// <summary>
    /// Ladder instance discovered during zone export (geometry only; links placed in second pass).
    /// </summary>
    public sealed class LadderDefinition
    {
        public required string Name { get; init; }

        /// <summary>Geometric bottom anchor (game space).</summary>
        public Vector3 Bottom { get; init; }

        /// <summary>Geometric top anchor (game space).</summary>
        public Vector3 Top { get; init; }

        public float MinZ { get; init; }
        public float MaxZ { get; init; }

        /// <summary>Horizontal center of the climb mesh.</summary>
        public Vector2 CenterXY { get; init; }

        /// <summary>Unit horizontal tangent (along the wall / ladder width).</summary>
        public Vector2 Tangent { get; init; }

        /// <summary>
        /// Unit horizontal thin-axis normal (one of two sides). Free side is resolved at placement time.
        /// </summary>
        public Vector2 ThinAxis { get; init; }

        /// <summary>Optional intermediate Z heights from multi-part climb meshes.</summary>
        public float[] SeedHeights { get; init; } = [];
    }

    /// <summary>
    /// JSON-friendly DTO (OpenTK vectors do not serialize cleanly by default).
    /// </summary>
    public sealed class LadderDefinitionDto
    {
        public required string Name { get; init; }
        public float BottomX { get; init; }
        public float BottomY { get; init; }
        public float BottomZ { get; init; }
        public float TopX { get; init; }
        public float TopY { get; init; }
        public float TopZ { get; init; }
        public float MinZ { get; init; }
        public float MaxZ { get; init; }
        public float CenterX { get; init; }
        public float CenterY { get; init; }
        public float TangentX { get; init; }
        public float TangentY { get; init; }
        public float ThinAxisX { get; init; }
        public float ThinAxisY { get; init; }
        public float[] SeedHeights { get; init; } = [];

        public static LadderDefinitionDto From(LadderDefinition def)
        {
            return new()
            {
                Name = def.Name,
                BottomX = def.Bottom.X,
                BottomY = def.Bottom.Y,
                BottomZ = def.Bottom.Z,
                TopX = def.Top.X,
                TopY = def.Top.Y,
                TopZ = def.Top.Z,
                MinZ = def.MinZ,
                MaxZ = def.MaxZ,
                CenterX = def.CenterXY.X,
                CenterY = def.CenterXY.Y,
                TangentX = def.Tangent.X,
                TangentY = def.Tangent.Y,
                ThinAxisX = def.ThinAxis.X,
                ThinAxisY = def.ThinAxis.Y,
                SeedHeights = def.SeedHeights,
            };
        }

        public LadderDefinition ToDefinition()
        {
            return new()
            {
                Name = Name,
                Bottom = new(BottomX, BottomY, BottomZ),
                Top = new(TopX, TopY, TopZ),
                MinZ = MinZ,
                MaxZ = MaxZ,
                CenterXY = new(CenterX, CenterY),
                Tangent = new(TangentX, TangentY),
                ThinAxis = new(ThinAxisX, ThinAxisY),
                SeedHeights = SeedHeights ?? [],
            };
        }
    }

    public sealed class LadderDefinitionFile
    {
        public int ZoneId { get; init; }
        public List<LadderDefinitionDto> Ladders { get; init; } = [];

        [JsonIgnore]
        public IEnumerable<LadderDefinition> Definitions => Ladders.Select(l => l.ToDefinition());
    }
}
