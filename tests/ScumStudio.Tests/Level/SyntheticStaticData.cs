using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// A small <c>The_Island_LevelStaticData</c> laid out as the game's (the five arrays of tagged structs), for the spawn place
/// editor without game files: vehicle point 0 stores no rotation (audit F107), points 1 and 2 are whole (groups Default and
/// City), two zombie points (Exterior, Interior), one tagged point (tag <c>Encounter.Bunker</c>), two threat zones of
/// <c>MTZ_Village</c> (the first with low-population settings, a sub-zone and a drone approach point around it) and one
/// hunting area of <c>BD_Village</c>. The name table also knows the group <c>Boat</c> and the tag <c>Encounter.Police</c>,
/// which nothing uses.
/// </summary>
internal static class SyntheticStaticData
{
    public const string Village = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Village";
    public const string Biome = "/Game/ConZ_Files/Hunting/BiomeData/BD_Village";
    public const string PointType = "EEncounterCharacterSpawnPointType";

    /// <param name="idOnVehicles">Every vehicle point also stores a <c>SpawnPointId</c> (a member this version does not know).</param>
    public static CookedPackage Build(bool idOnVehicles = false)
    {
        var pkg = new SyntheticLevelPackage();
        var cls = pkg.ScriptClass("/Script/ConZ", "LevelStaticData");
        var zone = pkg.ObjectImport(Village, "/Script/ConZ", "EncounterZoneData", "MTZ_Village");
        var biome = pkg.ObjectImport(Biome, "/Script/ConZ", "BiomeData", "BD_Village");
        pkg.Name("VehicleLevelSpawnGroup.Boat");
        pkg.Name("Encounter.Police");
        var export = pkg.Export("The_Island_LevelStaticData", cls, 0);

        var w = new ByteWriter();
        var t = new Tags(pkg, w);
        t.StructArray(SpawnPlaces.ArrayOf(SpawnPlaceKind.Vehicle), "VehicleSpawnPoint",
            i => Vehicle(i, new FVector(1000, 2000, 300), null, "Default", idOnVehicles ? 7 : null),
            i => Vehicle(i, new FVector(-5000, 800, 120), 45f, "Default", idOnVehicles ? 8 : null),
            i => Vehicle(i, new FVector(7000, -300, 90), 180f, "City", idOnVehicles ? 9 : null));
        t.StructArray(SpawnPlaces.ArrayOf(SpawnPlaceKind.Character), "EncounterCharacterSpawnPoint",
            i => Character(i, new FVector(10, 20, 30), 0f, "Exterior"),
            i => Character(i, new FVector(40, 50, 60), 90f, "Interior"));
        t.StructArray(SpawnPlaces.ArrayOf(SpawnPlaceKind.TaggedCharacter), "EncounterTaggedCharacterSpawnPoint", i =>
        {
            Character(i, new FVector(70, 80, 90), 10f, "Exterior");
            i.TagContainer("Tags", "Encounter.Bunker");
        });
        t.StructArray(SpawnPlaces.ArrayOf(SpawnPlaceKind.Zone), "StaticZoneDescription",
            i => Zone(i, zone, new FVector(20000, 30000, 500), 4000f, 2500f, around: true),
            i => Zone(i, zone, new FVector(-40000, 10000, 200), 6000f, 6000f, around: false));
        t.StructArray(SpawnPlaces.ArrayOf(SpawnPlaceKind.Animal), "HuntingBiome", i =>
        {
            i.Transform("Transform", new FVector(-1000, -2000, 0), 0f);
            i.Float("Radius", 15000f);
            i.Struct("PopulationParameters", "BiomeParameters", p =>
            {
                p.Byte("Priority", 2);
                p.Object("BiomeData", biome);
                p.Bool("bEnablePopulationSpawning", true);
                p.Float("VisitorDeactivationDistance", 5000f);
            });
        });
        t.None();
        w.I32(0);
        pkg.SetPayload(export, w.ToArray());
        var bytes = pkg.Build();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SpawnPlaces.StaticDataPath);
    }

    /// <summary>The package <paramref name="bytes"/> read back.</summary>
    public static CookedPackage Parse(PackageBytes bytes) => CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SpawnPlaces.StaticDataPath);

    /// <summary>The bytes of every element of <paramref name="kind"/>'s array, in order.</summary>
    public static List<byte[]> Items(CookedPackage package, SpawnPlaceKind kind)
    {
        var payload = package.GetExportData(0).ToArray();
        var array = (ScumStudio.Formats.Properties.ArrayValue)package.ReadProperties(0).Find(SpawnPlaces.ArrayOf(kind))!.Value;
        return array.Items.Select(i => payload.AsSpan(i.Offset, i.Size).ToArray()).ToList();
    }

    /// <summary>One element of <paramref name="kind"/>'s array as read.</summary>
    public static ScumStudio.Formats.Properties.StructValue Item(CookedPackage package, SpawnPlaceKind kind, int index) =>
        (ScumStudio.Formats.Properties.StructValue)((ScumStudio.Formats.Properties.ArrayValue)package.ReadProperties(0).Find(SpawnPlaces.ArrayOf(kind))!.Value).Items[index];

    public static FQuat Turn(float yaw) => new TransformValue(FVector.Zero, new FRotator(0f, yaw, 0f), FVector.One).ToTransform().Rotation;

    private static void Vehicle(Tags i, FVector at, float? yaw, string group, int? id)
    {
        i.Transform("SpawnTransform", at, yaw);
        i.Struct("VehicleLevelSpawnGroup", "GameplayTag", g => g.Name("TagName", "VehicleLevelSpawnGroup." + group));
        if (id is { } value)
        {
            i.Int("SpawnPointId", value);
        }
    }

    private static void Character(Tags i, FVector at, float yaw, string type)
    {
        i.Transform("Transform", at, yaw);
        i.Enum("Type", PointType, PointType + "::" + type);
    }

    private static void Zone(Tags i, int settings, FVector at, float semiX, float semiY, bool around)
    {
        i.Transform("_transform", at, 0f);
        if (around)
        {
            i.StructArray("_flyingSpawnedCharacterApproachLocations", "Transform", a => a.Vector("Translation", new FVector(at.X + 9000, at.Y, at.Z + 3000)));
        }

        i.Object("_zoneData", settings);
        if (around)
        {
            i.Object("_lpcZoneData", settings);
            i.StructArray("_subZoneData", "EncounterSubZoneData", z =>
            {
                z.Box("SubZoneBox", new FVector(at.X - 500, at.Y - 500, at.Z), new FVector(at.X + 500, at.Y + 500, at.Z + 400));
                z.Object("EncounterZoneData", settings);
            });
        }

        i.Float("_semiXAxisSize", semiX);
        i.Float("_semiYAxisSize", semiY);
        i.Float("_zoneActivationRadiusMultiplier", 1.5f);
        i.Float("_LPCZoneActivationRadiusMultiplier", 2f);
        foreach (var (box, k) in new[] { ("_boundingBox", 1f), ("_zoneActivationBoundingBox", 1.5f), ("_LPCZoneActivationBoundingBox", 2f) })
        {
            i.Box(box, new FVector(at.X - (semiX * k), at.Y - (semiY * k), at.Z), new FVector(at.X + (semiX * k), at.Y + (semiY * k), at.Z));
        }
    }

    /// <summary>FPropertyTag-framed values (UE 4.27), the kinds the static data uses.</summary>
    internal sealed class Tags(SyntheticLevelPackage package, ByteWriter writer)
    {
        public void None() => writer.FName(package.Name("None"));

        public void Float(string name, float value)
        {
            Header(name, "FloatProperty", 4);
            writer.U8(0);
            writer.F32(value);
        }

        public void Int(string name, int value)
        {
            Header(name, "IntProperty", 4);
            writer.U8(0);
            writer.I32(value);
        }

        public void Byte(string name, byte value)
        {
            Header(name, "ByteProperty", 1);
            writer.FName(package.Name("None"));
            writer.U8(0);
            writer.U8(value);
        }

        /// <summary>A <c>BoolProperty</c>: its value is in the tag, the value itself is empty.</summary>
        public void Bool(string name, bool value)
        {
            Header(name, "BoolProperty", 0);
            writer.U8(value ? (byte)1 : (byte)0);
            writer.U8(0);
        }

        /// <summary>A natively serialized <c>GameplayTagContainer</c>: the count, then each tag's FName.</summary>
        public void TagContainer(string name, params string[] tags) => Native(name, "GameplayTagContainer", b =>
        {
            b.I32(tags.Length);
            foreach (var tag in tags)
            {
                b.FName(package.Name(tag));
            }
        });

        public void Name(string name, string value)
        {
            Header(name, "NameProperty", 8);
            writer.U8(0);
            writer.FName(package.Name(value));
        }

        public void Enum(string name, string enumType, string value)
        {
            Header(name, "EnumProperty", 8);
            writer.FName(package.Name(enumType));
            writer.U8(0);
            writer.FName(package.Name(value));
        }

        public void Object(string name, int packageIndex)
        {
            Header(name, "ObjectProperty", 4);
            writer.U8(0);
            writer.I32(packageIndex);
        }

        public void Vector(string name, FVector v) => Native(name, "Vector", b => { b.F32(v.X); b.F32(v.Y); b.F32(v.Z); });

        public void Box(string name, FVector min, FVector max) => Native(name, "Box", b =>
        {
            b.F32(min.X);
            b.F32(min.Y);
            b.F32(min.Z);
            b.F32(max.X);
            b.F32(max.Y);
            b.F32(max.Z);
            b.U8(1);
        });

        /// <summary>A tagged <c>Transform</c>: no <c>Rotation</c> member when <paramref name="yaw"/> is null.</summary>
        public void Transform(string name, FVector at, float? yaw) => Struct(name, "Transform", t =>
        {
            if (yaw is { } y)
            {
                var q = Turn(y);
                t.Native("Rotation", "Quat", b => { b.F32(q.X); b.F32(q.Y); b.F32(q.Z); b.F32(q.W); });
            }

            t.Vector("Translation", at);
            t.Vector("Scale3D", FVector.One);
        });

        public void Struct(string name, string structName, Action<Tags> inner) => Native(name, structName, b =>
        {
            var t = new Tags(package, b);
            inner(t);
            t.None();
        });

        public void StructArray(string name, string structName, params Action<Tags>[] items)
        {
            var body = new ByteWriter();
            foreach (var item in items)
            {
                var t = new Tags(package, body);
                item(t);
                t.None();
            }

            var value = new ByteWriter();
            value.I32(items.Length);
            value.FName(package.Name(name));
            value.FName(package.Name("StructProperty"));
            value.I32(body.Length);
            value.I32(0);
            value.FName(package.Name(structName));
            value.Guid(default);
            value.U8(0);
            value.Raw(body.WrittenSpan);
            Header(name, "ArrayProperty", value.Length);
            writer.FName(package.Name("StructProperty"));
            writer.U8(0);
            writer.Raw(value.WrittenSpan);
        }

        private void Native(string name, string structName, Action<ByteWriter> value)
        {
            var body = new ByteWriter();
            value(body);
            Header(name, "StructProperty", body.Length);
            writer.FName(package.Name(structName));
            writer.Guid(default);
            writer.U8(0);
            writer.Raw(body.WrittenSpan);
        }

        private void Header(string name, string type, int size)
        {
            writer.FName(package.Name(name));
            writer.FName(package.Name(type));
            writer.I32(size);
            writer.I32(0);
        }
    }
}
