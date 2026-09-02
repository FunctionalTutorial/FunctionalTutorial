using System.Collections.Generic;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Shared._Functional.TutorialServer;

/// <summary>
/// Wizden's station tileset dropped most floors from 4 visual variants to 1.
/// Maps saved before that still store variant 1–3, and the client draws
/// <c>noTile.png</c> (purple/black checkers) whenever
/// <c>tile.Variant &gt;= definition.Variants</c>. Clamp on grid startup so
/// ported tutorial maps render.
/// </summary>
public sealed partial class TutorialTileVariantClampSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GridStartupEvent>(OnGridStartup);
    }

    private void OnGridStartup(GridStartupEvent ev)
    {
        if (!TryComp(ev.EntityUid, out MapGridComponent? grid))
            return;

        ClampVariants(ev.EntityUid, grid);
    }

    /// <summary>
    /// Rewrites any tile whose stored variant is past the current definition.
    /// </summary>
    public void ClampVariants(EntityUid gridUid, MapGridComponent grid)
    {
        List<(Vector2i GridIndices, Tile Tile)>? updates = null;

        foreach (var tileRef in _map.GetAllTiles(gridUid, grid))
        {
            var tile = tileRef.Tile;
            if (!_tiles.TryGetDefinition(tile.TypeId, out var def) || def.Variants == 0)
                continue;

            if (tile.Variant < def.Variants)
                continue;

            var clamped = (byte)(tile.Variant % def.Variants);
            updates ??= [];
            updates.Add((tileRef.GridIndices, new Tile(tile.TypeId, tile.Flags, clamped, tile.RotationMirroring)));
        }

        if (updates != null)
            _map.SetTiles(gridUid, grid, updates);
    }
}
