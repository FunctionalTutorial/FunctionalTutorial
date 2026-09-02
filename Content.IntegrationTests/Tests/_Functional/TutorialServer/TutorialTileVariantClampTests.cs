#nullable enable
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Content.IntegrationTests.Fixtures;
using Content.Shared._Functional.TutorialServer;
using Content.Shared.Maps;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Functional.TutorialServer;

[TestFixture]
[TestOf(typeof(TutorialTileVariantClampSystem))]
public sealed class TutorialTileVariantClampTests : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Dirty = true,
        Connected = false,
    };

    private static readonly ResPath TutorialMapsRoot = new("/Maps/_Functional/TutorialServer/");

    private static readonly Regex TilemapEntry = new(@"^[ \t]+(\d+):[ \t]*(\S+)\s*$", RegexOptions.Multiline);

    private static readonly Regex ChunkTiles = new(
        @"^[ \t]+tiles: ([A-Za-z0-9+/=]+)\r?\n[ \t]+version: (\d+)",
        RegexOptions.Multiline);

    /// <summary>
    /// Old maps store FloorSteel variant 3; Wizden FloorSteel only has variant 0.
    /// </summary>
    [Test]
    public async Task ClampVariants_WrapsOutOfRangeVariant()
    {
        var server = Pair.Server;
        var entMan = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var mapSys = entMan.System<SharedMapSystem>();
            var tiles = IoCManager.Resolve<ITileDefinitionManager>();
            var clamp = entMan.System<TutorialTileVariantClampSystem>();

            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            var steel = (ContentTileDefinition) tiles["FloorSteel"];
            Assert.That(steel.Variants, Is.EqualTo(1));

            mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(steel.TileId, variant: 3));
            clamp.ClampVariants(grid.Owner, grid.Comp);

            var after = mapSys.GetTileRef(grid.Owner, grid.Comp, Vector2i.Zero).Tile;
            Assert.That(after.TypeId, Is.EqualTo(steel.TileId));
            Assert.That(after.Variant, Is.EqualTo((byte) 0));
        });
    }

    /// <summary>
    /// Tutorial YAML must store variants the current tileset can draw. Out-of-range
    /// indices render as the purple/black missing-tile sprite.
    /// </summary>
    [Test]
    public async Task TutorialMaps_YamlStoresOnlyExistingTileVariants()
    {
        var server = Pair.Server;

        await server.WaitAssertion(() =>
        {
            var resources = server.ResolveDependency<IResourceManager>();
            var tiles = IoCManager.Resolve<ITileDefinitionManager>();
            var protos = IoCManager.Resolve<IPrototypeManager>();

            var aliases = new Dictionary<string, string>();
            foreach (var alias in protos.EnumeratePrototypes<TileAliasPrototype>())
                aliases[alias.ID] = alias.Target;

            var maps = new List<ResPath>();
            foreach (var path in resources.ContentFindFiles(TutorialMapsRoot))
            {
                if (path.Extension == "yml")
                    maps.Add(path);
            }

            Assert.That(maps, Is.Not.Empty);

            Assert.Multiple(() =>
            {
                foreach (var path in maps)
                {
                    var yaml = resources.ContentFileReadAllText(path);
                    foreach (var problem in FindOutOfRangeVariants(yaml, tiles, aliases))
                    {
                        Assert.Fail($"{path}: {problem}");
                    }
                }
            });
        });
    }

    private static IEnumerable<string> FindOutOfRangeVariants(
        string yaml,
        ITileDefinitionManager tiles,
        Dictionary<string, string> aliases)
    {
        var tilemap = ParseTilemap(yaml);
        if (tilemap.Count == 0)
            yield break;

        foreach (Match match in ChunkTiles.Matches(yaml))
        {
            var version = int.Parse(match.Groups[2].Value);
            if (version is not (6 or 7))
                continue;

            var data = Convert.FromBase64String(match.Groups[1].Value);
            var stride = version >= 7 ? 7 : 6;
            if (data.Length % stride != 0)
            {
                yield return $"chunk blob length {data.Length} is not a multiple of {stride}";
                continue;
            }

            for (var offset = 0; offset < data.Length; offset += stride)
            {
                var yamlId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                var variant = data[offset + 5];
                if (!tilemap.TryGetValue(yamlId, out var name))
                    continue;

                if (aliases.TryGetValue(name, out var aliased))
                    name = aliased;

                if (!tiles.TryGetDefinition(name, out var def) || def.Variants == 0)
                    continue;

                if (variant >= def.Variants)
                    yield return $"{name} variant {variant} exceeds {def.Variants - 1}";
            }
        }
    }

    private static Dictionary<int, string> ParseTilemap(string yaml)
    {
        var tilemap = new Dictionary<int, string>();
        var inMap = false;
        using var reader = new StringReader(yaml);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim() == "tilemap:")
            {
                inMap = true;
                continue;
            }

            if (!inMap)
                continue;

            var match = TilemapEntry.Match(line);
            if (!match.Success)
                break;

            tilemap[int.Parse(match.Groups[1].Value)] = match.Groups[2].Value;
        }

        return tilemap;
    }
}
