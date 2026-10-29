#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server._Functional.TutorialServer;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.GameTicking;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.GameTicking.Rules.Components;
using Content.Shared._Functional.TutorialServer;
using Content.Shared.Atmos;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Decals;
using Content.Shared.Doors.Components;
using Content.Shared.Inventory;
using Content.Shared.Disposal.Components;
using Content.Shared.Disposal.Tube;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Containers;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Roles;
using Content.Shared.Tag;
using NUnit.Framework;
using Content.Shared.Light.Components;
using Content.Shared.Slippery;
using Content.Shared.Wall;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Functional.TutorialServer;

/// <summary>
/// The janitor course is a mapped holopad walk, not a stamped closet. These checks load that map
/// and the sensors the twelve rooms complete on. They do not play the course.
/// </summary>
[TestFixture]
[TestOf(typeof(TutorialGoalSensorSystem))]
public sealed class TutorialJanitorTests : GameTest
{
    private static readonly ProtoId<TutorialRolePrototype> RoleId = "TutorialJanitor";
    private static readonly EntProtoId MentorId = "TutorialHoloMentorJanitor";

    [Test]
    public async Task TutorialJanitor_CurriculumResolvesEveryLocaleString()
    {
        var server = Pair.Server;
        var protos = server.ProtoMan;

        await server.WaitAssertion(() =>
        {
            Assert.That(protos.TryIndex<TutorialRolePrototype>(RoleId, out var role), Is.True);
            TutorialCurriculumAssertions.EveryLocaleStringResolves(role!);

            Assert.That(TutorialCurriculumAssertions.LocMan.TryGetString("tutorial-janitor-control-wear-galoshes", out var galoshes), Is.True);
            Assert.That(galoshes, Does.Contain("put the galoshes in the feet slot"));
            Assert.That(TutorialCurriculumAssertions.LocMan.GetString("tutorial-janitor-control-clear-one"), Is.EqualTo("Click a fuel puddle with a mop."));
            Assert.That(TutorialCurriculumAssertions.LocMan.GetString("tutorial-janitor-nanci-clear-rest-1"), Is.EqualTo("Mop the rest of this hall."));
            Assert.That(TutorialCurriculumAssertions.LocMan.GetString("tutorial-janitor-control-empty-cart"), Does.Contain("spill liquid"));
            Assert.That(TutorialCurriculumAssertions.LocMan.GetString("tutorial-janitor-control-refill-cart"), Does.Contain("bucket"));

            Assert.That(role!.Goals.SelectMany(g => g.SubGoals).Any(s => s.Id == "arm-bag"), Is.False);
            var fill = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "fill-bag");
            Assert.That(fill.Complete, Is.EqualTo(TutorialStepComplete.LooseTrashCleared));
            Assert.That(fill.Tag, Is.EqualTo("Trash"));
            Assert.That(fill.MinCount, Is.EqualTo(15));
            Assert.That(TutorialCurriculumAssertions.LocMan.GetString(fill.ControlHint!),
                Is.EqualTo("Hold the trash bag and click a tile in the pile."));

            var flush = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "flush");
            Assert.That(flush.ControlHint, Is.EqualTo(TutorialGoalSensorSystem.JanitorFlushBagHint));
            Assert.That(flush.Complete, Is.EqualTo(TutorialStepComplete.DisposalEngaged));
            Assert.That(TutorialCurriculumAssertions.LocMan.GetString("tutorial-janitor-nanci-flush-belt"), Does.Contain("belt"));

            foreach (var hint in new[]
                     {
                         TutorialGoalSensorSystem.JanitorFlushBagHint,
                         TutorialGoalSensorSystem.JanitorFlushUnitHint,
                         TutorialGoalSensorSystem.JanitorFlushHandsHint,
                     })
            {
                Assert.That(TutorialCurriculumAssertions.LocMan.GetString(hint), Does.Contain("[keybind=\"AltActivateItemInWorld\"]"),
                    hint);
            }
        });
    }

    [Test]
    public async Task TutorialJanitor_ControlHintsUseKeybindMarkupNotLiteralKeys()
    {
        var server = Pair.Server;
        var protos = server.ProtoMan;

        await server.WaitAssertion(() =>
        {
            Assert.That(protos.TryIndex<TutorialRolePrototype>(RoleId, out var role), Is.True);
            TutorialCurriculumAssertions.ControlHintsUseKeybindMarkup(role!);
        });
    }

    [Test]
    public async Task TutorialJanitor_MentorHasAnInCharacterLineForEverySubGoal()
    {
        var server = Pair.Server;
        var protos = server.ProtoMan;

        await server.WaitAssertion(() =>
        {
            Assert.That(protos.TryIndex<TutorialRolePrototype>(RoleId, out var role), Is.True);
            Assert.That(protos.TryIndex<EntityPrototype>(MentorId, out var mentor), Is.True);
            TutorialCurriculumAssertions.CoachSpeaksForEverySubGoal(role!, mentor!);
        });
    }

    [Test]
    public async Task TutorialJanitor_EverySensorReferenceResolves()
    {
        var server = Pair.Server;
        var protos = server.ProtoMan;
        var compFactory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.That(protos.TryIndex<TutorialRolePrototype>(RoleId, out var role), Is.True);
            TutorialCurriculumAssertions.EverySensorReferenceResolves(role!, protos, compFactory);
        });
    }

    [Test]
    public async Task TutorialJanitor_MapWiresGatesDrainDisposalAndMarkers()
    {
        var server = Pair.Server;
        var maps = server.System<TutorialMapSystem>();
        var protos = server.ProtoMan;
        var entMan = server.EntMan;
        var tags = server.System<TagSystem>();

        await server.WaitAssertion(() =>
        {
            var role = protos.Index<TutorialRolePrototype>(RoleId);
            Assert.That(role.LiveTutorial, Is.True);
            Assert.That(role.MentorMode, Is.EqualTo(TutorialMentorMode.Holopad));
            Assert.That(role.Job, Is.EqualTo(new ProtoId<JobPrototype>("Passenger")));
            Assert.That(role.ChamberEntryPads, Is.False);

            foreach (var goal in role.Goals)
            {
                if (goal.EnterRoom is not { } room)
                    continue;

                Assert.That(protos.HasIndex<EntityPrototype>($"TutorialHolopad{room}"), Is.True,
                    $"enterRoom {room} has no holopad prototype");
            }

            Assert.That(maps.TryLoadTutorialMap(role, out var mapUid, out var gridUid, out var spawn), Is.True);

            var gridsOnMap = 0;
            var gridQuery = entMan.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out _, out _, out var gridXform))
            {
                if (gridXform.MapUid == mapUid)
                    gridsOnMap++;
            }

            Assert.That(gridsOnMap, Is.EqualTo(1), "a stray space grid was stealing the spawn and the air fill");
            Assert.That(spawn.EntityId, Is.EqualTo(gridUid));

            var mapSystem = server.System<SharedMapSystem>();
            var grid = entMan.GetComponent<MapGridComponent>(gridUid);
            var spawnTile = mapSystem.GetTileRef(gridUid, grid, new Vector2i((int)spawn.Position.X, (int)spawn.Position.Y));
            Assert.That(spawnTile.Tile.IsEmpty, Is.False, "spawn tile is space");

            var gates = new HashSet<int>();
            var gateQuery = entMan.EntityQueryEnumerator<TutorialGateDoorComponent, TransformComponent>();
            while (gateQuery.MoveNext(out _, out var gate, out var xform))
            {
                if (xform.MapUid == mapUid)
                    gates.Add(gate.UnlockAtGoalIndex);
            }

            for (var room = 1; room <= 11; room++)
                Assert.That(gates, Does.Contain(room), $"map is missing the gate into room {room}");

            var pads = new HashSet<int>();
            var padQuery = entMan.EntityQueryEnumerator<TutorialHoloPointComponent, TransformComponent>();
            while (padQuery.MoveNext(out _, out var pad, out var xform))
            {
                if (xform.MapUid == mapUid)
                    pads.Add(pad.Room);
            }

            for (var room = 0; room <= 11; room++)
                Assert.That(pads, Does.Contain(room), $"map is missing a holopad for room {room}");

            var foundDrain = false;
            var drainQuery = entMan.EntityQueryEnumerator<DrainComponent, TransformComponent>();
            while (drainQuery.MoveNext(out var uid, out var drain, out var xform))
            {
                if (xform.MapUid != mapUid || !tags.HasTag(uid, "TutorialJanitorCloggedDrain"))
                    continue;

                foundDrain = true;
                Assert.That(drain.UnitsDestroyedPerSecond, Is.EqualTo(0f));
                Assert.That(drain.AutoDrain, Is.False);
            }

            Assert.That(foundDrain, Is.True, "clogged drain was not placed");

            var disposals = new List<(EntityUid Parent, Vector2 Position)>();
            var disposalQuery = entMan.EntityQueryEnumerator<TransformComponent>();
            while (disposalQuery.MoveNext(out var uid, out var xform))
            {
                if (xform.MapUid != mapUid || !tags.HasTag(uid, "TutorialJanitorDisposal"))
                    continue;

                disposals.Add((xform.ParentUid, xform.Coordinates.Position));
            }

            var foundEntry = false;
            var entryQuery = entMan.EntityQueryEnumerator<DisposalEntryComponent, DisposalTubeComponent, TransformComponent>();
            while (entryQuery.MoveNext(out _, out _, out _, out var entryXform))
            {
                var there = entryXform.Coordinates.Position;
                foreach (var disposal in disposals)
                {
                    if (entryXform.ParentUid == disposal.Parent && Vector2.Distance(there, disposal.Position) < 0.2f)
                        foundEntry = true;
                }
            }

            Assert.That(foundEntry, Is.True, "disposal has no trunk on its tile, so flush cannot succeed");

            var markers = new Dictionary<string, int>();
            var markerQuery = entMan.EntityQueryEnumerator<TutorialStepMarkerComponent, TransformComponent>();
            while (markerQuery.MoveNext(out _, out var marker, out var xform))
            {
                if (xform.MapUid != mapUid || string.IsNullOrEmpty(marker.MarkerId))
                    continue;

                markers[marker.MarkerId] = markers.GetValueOrDefault(marker.MarkerId) + 1;
            }

            Assert.That(markers.GetValueOrDefault("janitor-spill-one"), Is.EqualTo(1));
            Assert.That(markers.GetValueOrDefault("janitor-spill"), Is.GreaterThan(0));
            Assert.That(markers.GetValueOrDefault("janitor-backup"), Is.EqualTo(3));
            Assert.That(markers.GetValueOrDefault("janitor-soap"), Is.EqualTo(1));
            Assert.That(markers.GetValueOrDefault("janitor-cleanade"), Is.GreaterThan(0));
            Assert.That(markers.GetValueOrDefault("janitor-potassium"), Is.EqualTo(1));

            var trash = 0;
            var randomTrash = 0;
            var exits = 0;
            var stockCryo = 0;
            var metaQuery = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (metaQuery.MoveNext(out var uid, out var meta, out var xform))
            {
                if (xform.MapUid != mapUid)
                    continue;

                var id = meta.EntityPrototype?.ID;
                if (id == "RandomSpawner100")
                    randomTrash++;
                if (id == "TutorialJanitorExit")
                    exits++;
                if (id == "CryogenicSleepUnit")
                    stockCryo++;
                if (tags.HasTag(uid, "Trash"))
                    trash++;
            }

            Assert.That(randomTrash, Is.EqualTo(0));
            Assert.That(trash, Is.GreaterThanOrEqualTo(15));
            Assert.That(server.System<TutorialGoalSensorSystem>().CountLooseTagged(mapUid, "Trash"), Is.GreaterThanOrEqualTo(15));
            Assert.That(exits, Is.EqualTo(1));
            Assert.That(stockCryo, Is.EqualTo(0));

            var doorTiles = new HashSet<(int X, int Y)>();
            var doorQuery = entMan.EntityQueryEnumerator<DoorComponent, TransformComponent>();
            while (doorQuery.MoveNext(out _, out _, out var doorXform))
            {
                if (doorXform.MapUid != mapUid)
                    continue;

                var doorPos = doorXform.Coordinates.Position;
                doorTiles.Add(((int)MathF.Floor(doorPos.X), (int)MathF.Floor(doorPos.Y)));
            }

            var wallTiles = new HashSet<(int X, int Y)>();
            var wallQuery = entMan.EntityQueryEnumerator<WallComponent, TransformComponent>();
            while (wallQuery.MoveNext(out _, out _, out var wallXform))
            {
                if (wallXform.MapUid != mapUid)
                    continue;

                var wallPos = wallXform.Coordinates.Position;
                wallTiles.Add(((int)MathF.Floor(wallPos.X), (int)MathF.Floor(wallPos.Y)));
            }

            EntityUid? roomZeroPad = null;
            var wallLights = 0;
            var lightsOnDoors = 0;
            var trashRoomLights = new List<(Vector2 Pos, (int X, int Y) Dir, (int X, int Y) Mount)>();
            var lightQuery = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (lightQuery.MoveNext(out var uid, out var meta, out var xform))
            {
                if (xform.MapUid != mapUid)
                    continue;

                var id = meta.EntityPrototype?.ID;
                if (id == "TutorialHolopad0")
                    roomZeroPad = uid;
                if (id != "AlwaysPoweredWallLight")
                    continue;

                wallLights++;
                var lightPos = xform.Coordinates.Position;
                var tile = ((int)MathF.Floor(lightPos.X), (int)MathF.Floor(lightPos.Y));
                // The tube sprite is offset one tile along local +Y, onto the wall it is mounted to.
                var facing = xform.LocalRotation.RotateVec(Vector2.UnitY);
                var dir = ((int)MathF.Round(facing.X), (int)MathF.Round(facing.Y));
                var mount = ((int)MathF.Floor(lightPos.X + facing.X), (int)MathF.Floor(lightPos.Y + facing.Y));
                if (doorTiles.Contains(tile) || doorTiles.Contains(mount))
                    lightsOnDoors++;

                // Trash chamber interior: x 32–37, y 7–10. Doors sit in the middle of the east and west walls.
                if (tile.Item1 is >= 32 and <= 37 && tile.Item2 is >= 7 and <= 10)
                    trashRoomLights.Add((lightPos, dir, mount));
            }

            Assert.That(roomZeroPad, Is.Not.Null, "first room has no holopad");
            Assert.That(Vector2.Distance(spawn.Position, entMan.GetComponent<TransformComponent>(roomZeroPad!.Value).Coordinates.Position), Is.LessThan(4f),
                "spawn is not in the closet room with the first holopad");

            var mixture = server.System<AtmosphereSystem>().GetContainingMixture(roomZeroPad.Value);
            Assert.That(mixture, Is.Not.Null);
            Assert.That(mixture!.GetMoles(Gas.Oxygen), Is.GreaterThan(1f));
            Assert.That(mixture.GetMoles(Gas.Nitrogen), Is.GreaterThan(1f));
            Assert.That(wallLights, Is.GreaterThan(40));
            Assert.That(lightsOnDoors, Is.EqualTo(0), "a wall light is on a doorway or mounted onto a door tile");
            Assert.That(trashRoomLights, Has.Count.GreaterThanOrEqualTo(4), "the trash room has no wall lights");
            for (var i = 0; i < trashRoomLights.Count; i++)
            {
                var light = trashRoomLights[i];
                Assert.That(wallTiles, Does.Contain(light.Mount),
                    $"trash room light at {light.Pos} is not mounted on a wall");
                Assert.That(doorTiles, Does.Not.Contain(light.Mount),
                    $"trash room light at {light.Pos} is mounted on a door");

                for (var j = i + 1; j < trashRoomLights.Count; j++)
                {
                    var other = trashRoomLights[j];
                    if (light.Dir != other.Dir)
                        continue;

                    Assert.That(Vector2.Distance(light.Pos, other.Pos), Is.GreaterThanOrEqualTo(3f),
                        "trash room lights on the same wall are closer than every fourth tile");
                }
            }

            var buckets = new List<Vector2>();
            var bucketQuery = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (bucketQuery.MoveNext(out _, out var bucketMeta, out var bucketXform))
            {
                if (bucketXform.MapUid != mapUid || bucketMeta.EntityPrototype?.ID != "Bucket")
                    continue;

                buckets.Add(bucketXform.Coordinates.Position);
            }

            Assert.That(buckets, Has.Count.EqualTo(1), "the trolley room should have one bucket waiting by the water tank");
            Assert.That(buckets[0].X, Is.InRange(10f, 14f));
            Assert.That(buckets[0].Y, Is.InRange(7f, 10f));

            var assistantProto = protos.Index<EntityPrototype>("TutorialJanitorAssistant");
            Assert.That(assistantProto.TryComp<TutorialSlipLoopComponent>(out var loopProto, TutorialCurriculumAssertions.CompFactory), Is.True);
            Assert.That(loopProto!.MinWait, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(loopProto.MaxWait, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(loopProto.EndTag, Is.EqualTo("TutorialJanitorSlipMark"));
            Assert.That(protos.Index<EntityPrototype>("TutorialJanitorSlipCue")
                    .TryComp<TutorialCueComponent>(out _, TutorialCurriculumAssertions.CompFactory),
                Is.False,
                "a one-shot approach cue would cancel the loop after the first crossing");

            // Map init can run before the slip mark is visible to a grid query. The next tick arms it.
            server.System<TutorialSlipLoopSystem>().Update(0f);

            var foundAssistant = false;
            var assistantQuery = entMan.EntityQueryEnumerator<TutorialSlipLoopComponent, HTNComponent, TransformComponent>();
            while (assistantQuery.MoveNext(out _, out var loop, out var htn, out var assistantXform))
            {
                if (assistantXform.MapUid != mapUid)
                    continue;

                foundAssistant = true;
                Assert.That(loop.Armed, Is.True, "the assistant never started the soap loop");
                Assert.That(loop.HeadingFar, Is.True);
                Assert.That(htn.Blackboard.TryGetValue<EntityCoordinates>(NPCBlackboard.FollowTarget, out var target, entMan), Is.True);
                Assert.That(target.Position, Is.EqualTo(loop.Far!.Value.Position));
            }

            Assert.That(foundAssistant, Is.True, "the janitor map has no looping assistant");

            maps.UnloadTutorialMap(mapUid);
            _ = gridUid;
        });
    }

    [Test]
    public async Task TutorialJanitor_SensorsMatchTrolleyDrainLightsAndDecals()
    {
        var map = await Pair.CreateTestMap();
        var server = Pair.Server;
        var entMan = server.EntMan;

        await server.WaitPost(() =>
        {
            entMan.SpawnEntity("TutorialJanitorTrolley", map.GridCoords);
            entMan.SpawnEntity("TutorialJanitorCloggedDrain", map.GridCoords);
            entMan.SpawnEntity("LightReplacerFilled", map.GridCoords);
        });
        await Pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            var sensors = server.System<TutorialGoalSensorSystem>();
            var solutions = server.System<SharedSolutionContainerSystem>();

            var refill = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "refill-cart");
            var empty = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "empty-cart");
            var plunge = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "plunge");

            Assert.That(sensors.TryMatchTaggedSolution(map.MapUid, refill), Is.True);
            Assert.That(sensors.TryMatchTaggedSolution(map.MapUid, empty), Is.False);
            Assert.That(sensors.TryMatchTaggedSolution(map.MapUid, plunge), Is.False);

            var insert = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "insert-mop");
            EntityUid? trolley = null;
            var trolleyQuery = entMan.EntityQueryEnumerator<ItemSlotsComponent, MetaDataComponent, TransformComponent>();
            while (trolleyQuery.MoveNext(out var uid, out _, out var meta, out var xform))
            {
                if (xform.MapUid == map.MapUid && meta.EntityPrototype?.ID == "TutorialJanitorTrolley")
                    trolley = uid;
            }

            Assert.That(trolley, Is.Not.Null);
            var mop = entMan.SpawnEntity("TutorialJanitorMop", map.GridCoords);
            var itemSlots = server.System<ItemSlotsSystem>();
            Assert.That(sensors.IsContainerHasEntityCount(map.MapUid, insert), Is.False);
            Assert.That(itemSlots.TryInsert((trolley!.Value, null), "mop_slot", mop, null), Is.True);
            Assert.That(sensors.IsContainerHasEntityCount(map.MapUid, insert), Is.True);
            Assert.That(itemSlots.TryEject((trolley.Value, null), "mop_slot", null, out _), Is.True);
            Assert.That(sensors.IsContainerHasEntityCount(map.MapUid, insert), Is.False);
            Assert.That(sensors.IsLightReplacerLoaded(map.MapUid, "LightReplacer"), Is.True);

            EntityUid? drainUid = null;
            var drainQuery = entMan.EntityQueryEnumerator<DrainComponent, TransformComponent>();
            while (drainQuery.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.MapUid == map.MapUid)
                    drainUid = uid;
            }

            Assert.That(drainUid, Is.Not.Null);
            Assert.That(solutions.TryGetSolution(drainUid!.Value, "drainBuffer", out var soln, out var solution), Is.True);
            Assert.That(solution!.Volume, Is.EqualTo(FixedPoint2.New(960)));
            solutions.RemoveAllSolution(soln!.Value);
            Assert.That(sensors.TryMatchTaggedSolution(map.MapUid, plunge), Is.True);

            EntityUid? replacer = null;
            var replacerQuery = entMan.EntityQueryEnumerator<LightReplacerComponent, TransformComponent>();
            while (replacerQuery.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.MapUid == map.MapUid)
                    replacer = uid;
            }

            Assert.That(replacer, Is.Not.Null);
            entMan.DeleteEntity(replacer!.Value);
        });

        await Pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var sensors = server.System<TutorialGoalSensorSystem>();
            entMan.SpawnEntity("LightReplacer", map.GridCoords);
            Assert.That(sensors.IsLightReplacerLoaded(map.MapUid, "LightReplacer"), Is.False);

            var decals = server.System<DecalSystem>();
            var added = new Decal(Vector2.Zero, "Blasto", null, Angle.Zero, 0, true);
            Assert.That(decals.TryAddDecal(added, map.GridCoords, out var index), Is.True);
            Assert.That(sensors.CountCleanableDecals(map.MapUid), Is.EqualTo(1));
            Assert.That(sensors.AreCleanableDecalsCleared(map.MapUid), Is.False);
            Assert.That(sensors.AreCleanableDecalsReduced(map.MapUid, map.MapUid), Is.False);

            Assert.That(decals.RemoveDecal(map.Grid.Owner, index), Is.True);
            Assert.That(sensors.CountCleanableDecals(map.MapUid), Is.EqualTo(0));
            Assert.That(sensors.AreCleanableDecalsCleared(map.MapUid), Is.True);
            Assert.That(sensors.AreCleanableDecalsReduced(map.MapUid, map.MapUid), Is.True);
        });
    }

    [Test]
    public async Task TutorialJanitor_LooseLitterClearsToTheSpareAndFlushFollowsIt()
    {
        var map = await Pair.CreateTestMap();
        var server = Pair.Server;
        var entMan = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var sensors = server.System<TutorialGoalSensorSystem>();
            var storage = server.System<SharedStorageSystem>();
            var containers = server.System<SharedContainerSystem>();
            var beat = new TutorialSubGoalData
            {
                Id = "fill-bag",
                Text = "tutorial-janitor-sg-fill-bag",
                Complete = TutorialStepComplete.LooseTrashCleared,
                Tag = "Trash",
                MinCount = 15,
            };

            var full = new List<EntityUid>();
            for (var i = 0; i < 19; i++)
                full.Add(entMan.SpawnEntity("FoodPacketBoritosTrash", map.GridCoords));

            var fullPlayer = entMan.SpawnEntity("Plunger", map.GridCoords);
            Assert.That(sensors.IsLooseTrashCleared(fullPlayer, map.MapUid, beat), Is.False);
            for (var i = 0; i < 14; i++)
                entMan.DeleteEntity(full[i]);
            Assert.That(sensors.CountLooseTagged(map.MapUid, "Trash"), Is.EqualTo(5));
            Assert.That(sensors.IsLooseTrashCleared(fullPlayer, map.MapUid, beat), Is.False);
            entMan.DeleteEntity(full[14]);
            Assert.That(sensors.IsLooseTrashCleared(fullPlayer, map.MapUid, beat), Is.True);

            for (var i = 15; i < full.Count; i++)
                entMan.DeleteEntity(full[i]);
            Assert.That(sensors.CountLooseTagged(map.MapUid, "Trash"), Is.EqualTo(0));

            var thinned = new List<EntityUid>();
            for (var i = 0; i < 6; i++)
                thinned.Add(entMan.SpawnEntity("FoodPacketBoritosTrash", map.GridCoords));

            var thinnedPlayer = entMan.SpawnEntity("Plunger", map.GridCoords);
            Assert.That(sensors.IsLooseTrashCleared(thinnedPlayer, map.MapUid, beat), Is.False);
            foreach (var packet in thinned)
                entMan.DeleteEntity(packet);
            Assert.That(sensors.IsLooseTrashCleared(thinnedPlayer, map.MapUid, beat), Is.True);

            var emptyPlayer = entMan.SpawnEntity("Plunger", map.GridCoords);
            Assert.That(sensors.IsLooseTrashCleared(emptyPlayer, map.MapUid, beat), Is.True);

            var bag = entMan.SpawnEntity("TutorialJanitorTrashBag", map.GridCoords);
            for (var i = 0; i < 10; i++)
            {
                var packet = entMan.SpawnEntity("FoodPacketBoritosTrash", map.GridCoords);
                Assert.That(storage.Insert(bag, packet, out _), Is.True);
            }

            var left = new List<EntityUid>();
            for (var i = 0; i < 4; i++)
                left.Add(entMan.SpawnEntity("FoodPacketBoritosTrash", map.GridCoords));

            var partialPlayer = entMan.SpawnEntity("Plunger", map.GridCoords);
            Assert.That(sensors.CountLooseTagged(map.MapUid, "Trash"), Is.EqualTo(4));
            Assert.That(sensors.IsLooseTrashCleared(partialPlayer, map.MapUid, beat), Is.False);
            foreach (var packet in left)
                entMan.DeleteEntity(packet);
            Assert.That(sensors.IsLooseTrashCleared(partialPlayer, map.MapUid, beat), Is.True);
            Assert.That(sensors.CountContentsOfTagged(map.MapUid, TutorialGoalSensorSystem.JanitorTrashBagTag), Is.EqualTo(10));
            Assert.That(sensors.ResolveJanitorFlushHint(partialPlayer, map.MapUid),
                Is.EqualTo(TutorialGoalSensorSystem.JanitorFlushBagHint));

            foreach (var packet in entMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities.ToArray())
                entMan.DeleteEntity(packet);

            var bin = entMan.SpawnEntity("TutorialJanitorDisposal", map.GridCoords);
            var binXform = entMan.GetComponent<TransformComponent>(bin);
            if (!binXform.Anchored)
                server.System<SharedTransformSystem>().AnchorEntity(bin, binXform);
            var unit = entMan.GetComponent<DisposalUnitComponent>(bin);
            unit.AutomaticEngage = false;
            var dumped = entMan.SpawnEntity("FoodPacketBoritosTrash", map.GridCoords);
            Assert.That(unit.Container, Is.Not.Null);
            Assert.That(containers.Insert(dumped, unit.Container!), Is.True);
            Assert.That(sensors.ResolveJanitorFlushHint(partialPlayer, map.MapUid),
                Is.EqualTo(TutorialGoalSensorSystem.JanitorFlushUnitHint));

            entMan.DeleteEntity(dumped);
            var loose = entMan.SpawnEntity("FoodPacketBoritosTrash", map.GridCoords);
            Assert.That(sensors.ResolveJanitorFlushHint(partialPlayer, map.MapUid),
                Is.EqualTo(TutorialGoalSensorSystem.JanitorFlushHandsHint));

            entMan.DeleteEntity(loose);
            var pack = entMan.SpawnEntity("ClothingBackpack", map.GridCoords);
            var pocketed = entMan.SpawnEntity("FoodPacketBoritosTrash", map.GridCoords);
            Assert.That(storage.Insert(pack, pocketed, out _), Is.True);
            Assert.That(sensors.ResolveJanitorFlushHint(pack, map.MapUid),
                Is.EqualTo(TutorialGoalSensorSystem.JanitorFlushHandsHint));
        });
    }

    [Test]
    public async Task TutorialJanitor_SprayPotassiumRespawnsAndSkipsASecondBottle()
    {
        var map = await Pair.CreateTestMap();
        var server = Pair.Server;
        var entMan = server.EntMan;

        await server.WaitPost(() =>
        {
            entMan.SpawnEntity("TutorialJanitorPotassiumMark", map.GridCoords);
            entMan.SpawnEntity("TutorialJanitorPotassiumSpill", map.GridCoords);
            entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.SpawnEntity("MobHuman", map.GridCoords);
        });
        await Pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            var potassium = role.Goals.First(goal => goal.Id == "potassium");
            Assert.That(potassium.SubGoals[^2].Id, Is.EqualTo("lecture"));
            Assert.That(potassium.SubGoals[^1].Id, Is.EqualTo("spray-potassium"));

            var spray = potassium.SubGoals[^1];
            var sensors = server.System<TutorialGoalSensorSystem>();
            var solutions = server.System<SharedSolutionContainerSystem>();
            var hands = server.System<SharedHandsSystem>();

            var humans = new List<EntityUid>();
            var bodies = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (bodies.MoveNext(out var uid, out var meta, out var xform))
            {
                if (xform.MapUid == map.MapUid && meta.EntityPrototype?.ID == "MobHuman")
                    humans.Add(uid);
            }

            Assert.That(humans, Has.Count.EqualTo(2));
            var mop = entMan.SpawnEntity("TutorialJanitorMop", map.GridCoords);
            Assert.That(hands.TryPickup(humans[0], mop), Is.True,
                "the potassium beat is reached while the mop is still in hand");

            var puddle = FindProto(entMan, map.MapUid, "TutorialJanitorPotassiumSpill");
            Assert.That(puddle, Is.Not.Null);
            Assert.That(solutions.TryGetSolution(puddle!.Value, "puddle", out var soln, out _), Is.True);
            solutions.RemoveAllSolution(soln!.Value);

            sensors.EnsureTaggedRespawn(humans[0], map.MapUid, spray);
            Assert.That(CountProto(entMan, map.MapUid, "SprayBottleSpaceCleaner"), Is.EqualTo(1));
            Assert.That(Potassium(solutions, FindProto(entMan, map.MapUid, "TutorialJanitorPotassiumSpill")), Is.EqualTo(FixedPoint2.New(5)));
            Assert.That(sensors.IsTaggedReagentCleared(map.MapUid, spray), Is.False);

            sensors.EnsureTaggedRespawn(humans[0], map.MapUid, spray);
            Assert.That(CountProto(entMan, map.MapUid, "SprayBottleSpaceCleaner"), Is.EqualTo(1));

            var held = entMan.SpawnEntity("SprayBottleSpaceCleaner", map.GridCoords);
            Assert.That(hands.TryPickup(humans[1], held), Is.True);
            sensors.EnsureTaggedRespawn(humans[1], map.MapUid, spray);
            Assert.That(CountProto(entMan, map.MapUid, "SprayBottleSpaceCleaner"), Is.EqualTo(2));

            var cleaned = FindProto(entMan, map.MapUid, "TutorialJanitorPotassiumSpill");
            Assert.That(solutions.TryGetSolution(cleaned!.Value, "puddle", out var water, out _), Is.True);
            solutions.RemoveAllSolution(water!.Value);
            solutions.TryAddReagent(water.Value, "Water", FixedPoint2.New(5));
            Assert.That(sensors.IsTaggedReagentCleared(map.MapUid, spray), Is.True);

            entMan.DeleteEntity(cleaned.Value);
            Assert.That(sensors.IsTaggedReagentCleared(map.MapUid, spray), Is.False);
            sensors.EnsureTaggedRespawn(humans[0], map.MapUid, spray);
            Assert.That(Potassium(solutions, FindProto(entMan, map.MapUid, "TutorialJanitorPotassiumSpill")), Is.EqualTo(FixedPoint2.New(5)));
            Assert.That(CountProto(entMan, map.MapUid, "SprayBottleSpaceCleaner"), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task TutorialJanitor_PotassiumIsDoneWhenGoneAndTheMopChangesTheLine()
    {
        var map = await Pair.CreateTestMap();
        var server = Pair.Server;
        var entMan = server.EntMan;

        await server.WaitPost(() => entMan.SpawnEntity("MobHuman", map.GridCoords));
        await Pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            var mop = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "mop-potassium");
            var spray = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "spray-potassium");
            Assert.That(mop.Complete, Is.EqualTo(TutorialStepComplete.TaggedReagentCleared));
            Assert.That(mop.ClearedWhenAbsent, Is.True);
            Assert.That(spray.ClearedWhenAbsent, Is.False);

            var puddle = entMan.SpawnEntity("TutorialJanitorPotassiumSpill", map.GridCoords);
            var sensors = server.System<TutorialGoalSensorSystem>();
            var solutions = server.System<SharedSolutionContainerSystem>();
            Assert.That(sensors.IsTaggedReagentCleared(map.MapUid, mop), Is.False);

            Assert.That(solutions.TryGetSolution(puddle, "puddle", out var soln, out _), Is.True);
            solutions.RemoveAllSolution(soln!.Value);
            solutions.TryAddReagent(soln.Value, "Water", FixedPoint2.New(5));
            Assert.That(sensors.IsTaggedReagentCleared(map.MapUid, mop), Is.True,
                "a spill with no potassium left is mopped, whether or not it exploded");

            entMan.DeleteEntity(puddle);
            Assert.That(sensors.IsTaggedReagentCleared(map.MapUid, mop), Is.True,
                "the explosion deleting the spill still finishes the beat");
            Assert.That(sensors.IsTaggedReagentCleared(map.MapUid, spray), Is.False,
                "the spray beat still needs a spill to clean");

            EntityUid? human = null;
            var bodies = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (bodies.MoveNext(out var uid, out var meta, out var xform))
            {
                if (xform.MapUid == map.MapUid && meta.EntityPrototype?.ID == "MobHuman")
                    human = uid;
            }

            Assert.That(human, Is.Not.Null);
            var mentor = server.ProtoMan.Index<EntityPrototype>(MentorId);
            Assert.That(mentor.TryComp<TutorialTrainerComponent>(out var trainer, TutorialCurriculumAssertions.CompFactory), Is.True);
            var trainerSys = server.System<TutorialTrainerSystem>();

            var quiet = trainerSys.SpokenLocIds(human!.Value, trainer!, "mop-potassium");
            Assert.That(quiet, Does.Contain("tutorial-janitor-nanci-mop-potassium-mock"));
            Assert.That(quiet, Does.Not.Contain("tutorial-janitor-nanci-mop-potassium-2"));
            var quietLecture = trainerSys.SpokenLocIds(human.Value, trainer, "lecture");
            Assert.That(quietLecture, Does.Contain("tutorial-janitor-nanci-lecture-other"));
            Assert.That(quietLecture, Does.Not.Contain("tutorial-janitor-nanci-lecture-3"));

            var choice = entMan.EnsureComponent<TutorialToolChoiceComponent>(human.Value);
            choice.Expected.Add("mop-potassium");
            var boom = trainerSys.SpokenLocIds(human.Value, trainer, "mop-potassium");
            Assert.That(boom, Does.Contain("tutorial-janitor-nanci-mop-potassium-2"));
            Assert.That(boom, Does.Contain("tutorial-janitor-nanci-mop-potassium-3"));
            Assert.That(boom, Does.Not.Contain("tutorial-janitor-nanci-mop-potassium-mock"));
            var boomLecture = trainerSys.SpokenLocIds(human.Value, trainer, "lecture");
            Assert.That(boomLecture, Does.Contain("tutorial-janitor-nanci-lecture-3"));
            Assert.That(boomLecture, Does.Not.Contain("tutorial-janitor-nanci-lecture-other"));

            var mentorEnt = entMan.SpawnEntity(MentorId, map.GridCoords);
            var mentorComp = entMan.GetComponent<TutorialMentorComponent>(mentorEnt);
            mentorComp.PlayerUid = human.Value;
            var coach = entMan.GetComponent<TutorialTrainerComponent>(mentorEnt);
            coach.LastSpokenSubGoal = "wear-galoshes";
            coach.PendingLines.Clear();
            coach.PendingAfterLines.Clear();
            coach.ReactingFor = null;
            coach.ReactionChosen = false;
            coach.PendingAfterLines.Enqueue(new TutorialPendingLine("tutorial-janitor-nanci-wear-galoshes-3", false, true));
            coach.PendingAfterLines.Enqueue(new TutorialPendingLine("tutorial-janitor-nanci-wear-galoshes-4", false, true));

            Assert.That(trainerSys.TryStartReaction(human.Value, "wear-galoshes"), Is.True);
            coach.PendingAfterLines.Clear();
            coach.ReactingFor = null;
            Assert.That(trainerSys.TryStartReaction(human.Value, "wear-galoshes"), Is.False,
                "a finished reaction must not be queued again, or the galoshes airlock never opens");
            Assert.That(coach.PendingAfterLines, Is.Empty);
        });
    }

    private static EntityUid? FindProto(IEntityManager entMan, EntityUid mapUid, string prototype)
    {
        var query = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var meta, out var xform))
        {
            if (xform.MapUid == mapUid && meta.EntityPrototype?.ID == prototype)
                return uid;
        }

        return null;
    }

    private static int CountProto(IEntityManager entMan, EntityUid mapUid, string prototype)
    {
        var count = 0;
        var query = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out _, out var meta, out var xform))
        {
            if (xform.MapUid == mapUid && meta.EntityPrototype?.ID == prototype)
                count++;
        }

        return count;
    }

    private static FixedPoint2 Potassium(SharedSolutionContainerSystem solutions, EntityUid? puddle)
    {
        if (puddle == null || !solutions.TryGetSolution(puddle.Value, "puddle", out _, out var solution) || solution == null)
            return FixedPoint2.Zero;

        return solution.GetTotalPrototypeQuantity("Potassium");
    }

    [Test]
    public async Task TutorialJanitor_FootlessSpeciesSkipsFootwearAndHearsTheMock()
    {
        var map = await Pair.CreateTestMap();
        var server = Pair.Server;

        await server.WaitPost(() =>
        {
            server.EntMan.SpawnEntity("MobHuman", map.GridCoords);
            server.EntMan.SpawnEntity("MobDiona", map.GridCoords);
        });
        await Pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var tutorial = server.System<TutorialServerRuleSystem>();
            var trainerSys = server.System<TutorialTrainerSystem>();
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            var mentor = server.ProtoMan.Index<EntityPrototype>(MentorId);
            Assert.That(mentor.TryComp<TutorialTrainerComponent>(out var trainer, TutorialCurriculumAssertions.CompFactory), Is.True);

            EntityUid? human = null;
            EntityUid? diona = null;
            var bodies = server.EntMan.EntityQueryEnumerator<InventoryComponent, MetaDataComponent, TransformComponent>();
            while (bodies.MoveNext(out var uid, out _, out var meta, out var xform))
            {
                if (xform.MapUid != map.MapUid)
                    continue;

                if (meta.EntityPrototype?.ID == "MobHuman")
                    human = uid;
                else if (meta.EntityPrototype?.ID == "MobDiona")
                    diona = uid;
            }

            Assert.That(human, Is.Not.Null);
            Assert.That(diona, Is.Not.Null);
            Assert.That(tutorial.HasInventorySlot(human!.Value, "shoes"), Is.True);
            Assert.That(tutorial.HasInventorySlot(diona!.Value, "shoes"), Is.False);

            foreach (var sub in role.Goals.SelectMany(g => g.SubGoals))
            {
                var footwear = sub.Slot == "shoes" ||
                               (sub.Complete == TutorialStepComplete.WearItem &&
                                sub.Entity is { } proto &&
                                proto.Id.StartsWith("ClothingShoes", StringComparison.Ordinal));

                Assert.That(tutorial.SkipsForMissingFeet(diona.Value, sub), Is.EqualTo(footwear), sub.Id);
                Assert.That(tutorial.SkipsForMissingFeet(human.Value, sub), Is.False, sub.Id);

                if (!footwear)
                    continue;

                var mock = trainer!.Lines.Where(l => l.SubGoalId == sub.Id && l.WithoutSlot == "shoes").ToList();
                Assert.That(mock, Is.Not.Empty, $"{sub.Id} has no line for a species with no feet");
            }

            var dionaGaloshes = trainerSys.SpokenLocIds(diona.Value, trainer!, "wear-galoshes");
            Assert.That(dionaGaloshes, Is.EqualTo(new[]
            {
                "tutorial-janitor-nanci-wear-galoshes-nofeet-1",
                "tutorial-janitor-nanci-wear-galoshes-nofeet-2",
                "tutorial-janitor-nanci-wear-galoshes-nofeet-3",
            }));

            var humanGaloshes = trainerSys.SpokenLocIds(human.Value, trainer, "wear-galoshes");
            Assert.That(humanGaloshes, Does.Contain("tutorial-janitor-nanci-wear-galoshes-2"));
            Assert.That(humanGaloshes, Does.Not.Contain("tutorial-janitor-nanci-wear-galoshes-nofeet-1"));

            var dionaSlip = trainerSys.SpokenLocIds(diona.Value, trainer, "watch-slip");
            Assert.That(dionaSlip, Does.Contain("tutorial-janitor-nanci-watch-slip-1"));
            Assert.That(dionaSlip, Does.Contain("tutorial-janitor-nanci-watch-slip-nofeet"));
            Assert.That(dionaSlip, Does.Not.Contain("tutorial-janitor-nanci-watch-slip-2"));

            Assert.That(mentor.TryComp<TutorialQuipComponent>(out var quips, TutorialCurriculumAssertions.CompFactory), Is.True);
            var slip = quips!.Quips.Single(q => q.Trigger == TutorialQuipTrigger.PlayerSlipped);
            Assert.That(TutorialCurriculumAssertions.LocMan.TryGetString(slip.Line, out var slipText), Is.True);
            Assert.That(slipText, Does.Contain("galoshes"));
        });
    }

    [Test]
    public async Task TutorialJanitor_WaterLeftByTheMopCountsAsCleared()
    {
        var map = await Pair.CreateTestMap();
        var server = Pair.Server;
        var entMan = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var fuel = entMan.SpawnEntity("TutorialJanitorSpillOne", map.GridCoords.Offset(new Vector2(1, 0)));
            var rest = entMan.SpawnEntity("TutorialJanitorSpill", map.GridCoords.Offset(new Vector2(2, 0)));

            var solutions = server.System<SharedSolutionContainerSystem>();
            var sensors = server.System<TutorialGoalSensorSystem>();
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            var clearOne = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "clear-one");
            var clearRest = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "clear-rest");
            var drain = role.Goals.First(g => g.Id == "drain");

            Assert.That(drain.SubGoals.Select(s => s.Id), Is.EqualTo(new[] { "plunge" }));

            Assert.That(solutions.TryGetSolution(fuel, "puddle", out var fuelSoln, out _), Is.True);
            solutions.TryAddReagent(fuelSoln!.Value, "WeldingFuel", FixedPoint2.New(30));
            Assert.That(solutions.TryGetSolution(rest, "puddle", out var restSoln, out _), Is.True);
            solutions.TryAddReagent(restSoln!.Value, "WeldingFuel", FixedPoint2.New(30));

            Assert.That(sensors.IsMarkedPuddleFreeOf(map.MapUid, clearOne.Marker, "WeldingFuel"), Is.False);
            Assert.That(sensors.IsMarkedPuddleFreeOf(map.MapUid, clearRest.Marker, "WeldingFuel"), Is.False);

            solutions.RemoveAllSolution(fuelSoln.Value);
            solutions.TryAddReagent(fuelSoln.Value, "Water", FixedPoint2.New(30));
            Assert.That(sensors.IsMarkedPuddleFreeOf(map.MapUid, clearOne.Marker, "WeldingFuel"), Is.True,
                "a fuel tile that is only water should advance without waiting to dry");

            solutions.RemoveAllSolution(restSoln.Value);
            solutions.TryAddReagent(restSoln.Value, "Water", FixedPoint2.New(30));
            Assert.That(sensors.IsMarkedPuddleFreeOf(map.MapUid, clearRest.Marker, "WeldingFuel"), Is.True);
        });
    }

    [Test]
    public async Task TutorialJanitor_CleanadeWaterCountsAsCleared()
    {
        var map = await Pair.CreateTestMap();
        var server = Pair.Server;
        var entMan = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var egg = entMan.SpawnEntity("TutorialJanitorEgg", map.GridCoords.Offset(new Vector2(1, 0)));
            var flour = entMan.SpawnEntity("TutorialJanitorFlour", map.GridCoords.Offset(new Vector2(2, 0)));
            var vomit = entMan.SpawnEntity("TutorialJanitorVomit", map.GridCoords.Offset(new Vector2(3, 0)));

            var solutions = server.System<SharedSolutionContainerSystem>();
            var sensors = server.System<TutorialGoalSensorSystem>();
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            var mopCorners = role.Goals.SelectMany(g => g.SubGoals).First(s => s.Id == "mop-corners");

            Assert.That(mopCorners.AllowedReagent?.Id, Is.EqualTo("Water"));
            Assert.That(sensors.IsMarkedPuddleOnly(map.MapUid, mopCorners.Marker, "Water"), Is.False,
                "egg, flour, and vomit still have to be cleaned");

            foreach (var puddle in new[] { egg, flour, vomit })
            {
                Assert.That(solutions.TryGetSolution(puddle, "puddle", out var soln, out _), Is.True);
                solutions.RemoveAllSolution(soln!.Value);
                solutions.TryAddReagent(soln.Value, "Water", FixedPoint2.New(10));
            }

            Assert.That(sensors.IsMarkedPuddleOnly(map.MapUid, mopCorners.Marker, "Water"), Is.True,
                "a mess the foam or mop turned into water should advance without waiting to dry");

            Assert.That(solutions.TryGetSolution(flour, "puddle", out var flourSoln, out _), Is.True);
            solutions.TryAddReagent(flourSoln!.Value, "Flour", FixedPoint2.New(5));
            Assert.That(sensors.IsMarkedPuddleOnly(map.MapUid, mopCorners.Marker, "Water"), Is.False,
                "a corner the foam missed still blocks the beat");
        });
    }
}

/// <summary>
/// Needs a real ticker. The dummy one accepts the role selection and then never spawns a body.
/// </summary>
[TestFixture]
[TestOf(typeof(TutorialServerRuleSystem))]
public sealed class TutorialJanitorFeetSessionTests : GameTest
{
    private static readonly ProtoId<TutorialRolePrototype> RoleId = "TutorialJanitor";
    private static readonly EntProtoId TutorialRule = "TutorialServer";

    public override PoolSettings PoolSettings => new()
    {
        Dirty = true,
        InLobby = true,
        DummyTicker = false,
        Connected = true,
    };

    [Test]
    public async Task TutorialJanitor_MissingShoesSlotAdvancesPastGaloshes()
    {
        var pair = Pair;
        var server = pair.Server;
        var ticker = server.System<GameTicker>();
        var tutorial = server.System<TutorialServerRuleSystem>();

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset("TutorialServer");
            ticker.StartGameRule(TutorialRule, out _);
            ticker.StartRound();
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
            tutorial.TrySelectRole(pair.Player!, RoleId, confirmedStub: false));

        var ready = false;
        for (var i = 0; i < 100 && !ready; i++)
        {
            await pair.RunTicksSync(10);
            await server.WaitPost(() =>
            {
                ready = pair.Player?.AttachedEntity is { } body &&
                        server.EntMan.HasComponent<TutorialParticipantComponent>(body);
            });
        }

        Assert.That(ready, Is.True, "the player never entered the janitor tutorial");

        await server.WaitAssertion(() =>
        {
            var mob = pair.Player!.AttachedEntity!.Value;
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            Assert.That(TryFindSubGoal(role, "wear-galoshes", out var goalIndex, out var subIndex), Is.True);
            Assert.That(tutorial.TryGetSession(mob, out var session), Is.True);
            session.GoalIndex = goalIndex;
            session.SubGoalIndex = subIndex;
            session.AwaitingChamberEntryPad = false;

            // Finished coach, empty queues: a species with feet must still wait to put the galoshes on.
            var coach = server.EntMan.GetComponent<TutorialTrainerComponent>(session.MentorUid);
            coach.LastSpokenSubGoal = "wear-galoshes";
            coach.PendingLines.Clear();
            coach.PendingAfterLines.Clear();
            coach.PendingInterjections.Clear();
            coach.ReactingFor = null;
            coach.NextLineAt = null;
        });

        await pair.RunTicksSync(15);

        await server.WaitAssertion(() =>
        {
            var mob = pair.Player!.AttachedEntity!.Value;
            var part = server.EntMan.GetComponent<TutorialParticipantComponent>(mob);
            Assert.That(tutorial.TryGetCurrentSubGoal(mob, part, out var sub), Is.True);
            Assert.That(sub.Id, Is.EqualTo("wear-galoshes"));

            var inventory = server.EntMan.GetComponent<InventoryComponent>(mob);
            server.System<InventorySystem>().SetTemplateId((mob, inventory), "diona");
            Assert.That(tutorial.HasInventorySlot(mob, "shoes"), Is.False);

            Assert.That(tutorial.TryGetSession(mob, out var session), Is.True);
            var coach = server.EntMan.GetComponent<TutorialTrainerComponent>(session.MentorUid);
            coach.LastSpokenSubGoal = null;
            coach.PendingLines.Clear();
            coach.PendingAfterLines.Clear();
            coach.PendingInterjections.Clear();
            coach.ReactingFor = null;
            coach.NextLineAt = null;
            coach.StartDelay = TimeSpan.Zero;
            coach.SessionStartDelay = TimeSpan.Zero;
            coach.MinLineDelay = TimeSpan.Zero;
            coach.MaxLineDelay = TimeSpan.Zero;
            coach.SecondsPerCharacter = 0;
            // The jump did not walk them up to the pad. She still owes the mock before the beat ends.
            coach.PlayerArrived = true;
        });

        var mocked = false;
        string? current = null;
        for (var i = 0; i < 30 && current != "dip-mop"; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var mob = pair.Player!.AttachedEntity!.Value;
                var part = server.EntMan.GetComponent<TutorialParticipantComponent>(mob);
                Assert.That(tutorial.TryGetCurrentSubGoal(mob, part, out var sub), Is.True);
                current = sub.Id;

                Assert.That(tutorial.TryGetSession(mob, out var session), Is.True);
                if (!server.EntMan.TryGetComponent<TutorialTrainerComponent>(session.MentorUid, out var coach))
                    return;

                foreach (var line in coach.PendingLines)
                {
                    if (line.Text.Contains("nofeet", StringComparison.Ordinal))
                        mocked = true;
                }
            });
        }

        Assert.That(mocked, Is.True, "N.A.N.C.I. advanced past the galoshes without mocking the missing feet");
        Assert.That(current, Is.EqualTo("dip-mop"));
    }

    [Test]
    public async Task TutorialJanitor_SlippingInTheFuelHallIsMockedOnce()
    {
        var pair = Pair;
        var server = pair.Server;
        var ticker = server.System<GameTicker>();
        var tutorial = server.System<TutorialServerRuleSystem>();

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset("TutorialServer");
            ticker.StartGameRule(TutorialRule, out _);
            ticker.StartRound();
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
            tutorial.TrySelectRole(pair.Player!, RoleId, confirmedStub: false));

        var ready = false;
        for (var i = 0; i < 100 && !ready; i++)
        {
            await pair.RunTicksSync(10);
            await server.WaitPost(() =>
            {
                ready = pair.Player?.AttachedEntity is { } body &&
                        server.EntMan.HasComponent<TutorialParticipantComponent>(body);
            });
        }

        Assert.That(ready, Is.True, "the player never entered the janitor tutorial");

        await server.WaitAssertion(() =>
        {
            var mob = pair.Player!.AttachedEntity!.Value;
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            Assert.That(tutorial.TryGetSession(mob, out var session), Is.True);
            Assert.That(tutorial.HasInventorySlot(mob, "shoes"), Is.True);

            var coords = server.EntMan.GetComponent<TransformComponent>(mob).Coordinates;
            var soap = server.EntMan.SpawnEntity("Soap", coords);
            var slipped = new SlipEvent(mob);
            server.EntMan.EventBus.RaiseLocalEvent(soap, ref slipped);

            var coach = server.EntMan.GetComponent<TutorialTrainerComponent>(session.MentorUid);
            Assert.That(coach.PendingInterjections.Any(line => line.Text == "tutorial-janitor-nanci-slip-galoshes"), Is.False,
                "slipping on soap should not be mocked");

            Assert.That(TryFindSubGoal(role, "dip-mop", out var goalIndex, out var subIndex), Is.True);
            session.GoalIndex = goalIndex;
            session.SubGoalIndex = subIndex;

            slipped = new SlipEvent(mob);
            server.EntMan.EventBus.RaiseLocalEvent(soap, ref slipped);
            Assert.That(coach.PendingInterjections.Any(line => line.Text == "tutorial-janitor-nanci-slip-galoshes"), Is.False,
                "the fuel-hall goal starts in the closet, so a slip there is not the wet floor");

            var spill = server.EntMan.SpawnEntity("TutorialJanitorSpillOne", coords);
            slipped = new SlipEvent(mob);
            server.EntMan.EventBus.RaiseLocalEvent(spill, ref slipped);
            Assert.That(coach.PendingInterjections.Count(line => line.Text == "tutorial-janitor-nanci-slip-galoshes"), Is.EqualTo(1));

            slipped = new SlipEvent(mob);
            server.EntMan.EventBus.RaiseLocalEvent(spill, ref slipped);
            Assert.That(coach.PendingInterjections.Count(line => line.Text == "tutorial-janitor-nanci-slip-galoshes"), Is.EqualTo(1),
                "the slip should be mocked only once");
        });
    }

    [Test]
    public async Task TutorialJanitor_EachAssistantSlipCanCompleteTheBeat()
    {
        var pair = Pair;
        var server = pair.Server;
        var ticker = server.System<GameTicker>();
        var tutorial = server.System<TutorialServerRuleSystem>();

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset("TutorialServer");
            ticker.StartGameRule(TutorialRule, out _);
            ticker.StartRound();
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
            tutorial.TrySelectRole(pair.Player!, RoleId, confirmedStub: false));

        var ready = false;
        for (var i = 0; i < 100 && !ready; i++)
        {
            await pair.RunTicksSync(10);
            await server.WaitPost(() =>
            {
                ready = pair.Player?.AttachedEntity is { } body &&
                        server.EntMan.HasComponent<TutorialParticipantComponent>(body);
            });
        }

        Assert.That(ready, Is.True, "the player never entered the janitor tutorial");

        await server.WaitAssertion(() =>
        {
            var mob = pair.Player!.AttachedEntity!.Value;
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            Assert.That(tutorial.TryGetSession(mob, out var session), Is.True);
            session.AwaitingChamberEntryPad = false;

            Assert.That(TryFindSubGoal(role, "step-soap", out var stepGoal, out var stepSub), Is.True);
            session.GoalIndex = stepGoal;
            session.SubGoalIndex = stepSub;

            var coords = server.EntMan.GetComponent<TransformComponent>(mob).Coordinates;
            var dummy = server.EntMan.SpawnEntity("TutorialPracticeMob", coords);
            var soap = server.EntMan.SpawnEntity("Soap", coords);
            var slipped = new SlipEvent(dummy);
            server.EntMan.EventBus.RaiseLocalEvent(soap, ref slipped);

            var part = server.EntMan.GetComponent<TutorialParticipantComponent>(mob);
            Assert.That(tutorial.TryGetCurrentSubGoal(mob, part, out var before), Is.True);
            Assert.That(before.Id, Is.EqualTo("step-soap"), "a slip before the beat should not be spent");

            Assert.That(TryFindSubGoal(role, "watch-slip", out var watchGoal, out var watchSub), Is.True);
            session.GoalIndex = watchGoal;
            session.SubGoalIndex = watchSub;

            slipped = new SlipEvent(dummy);
            server.EntMan.EventBus.RaiseLocalEvent(soap, ref slipped);
            Assert.That(tutorial.TryGetCurrentSubGoal(mob, part, out var first), Is.True);
            Assert.That(first.Id, Is.EqualTo("pickup-soap"));

            session.GoalIndex = watchGoal;
            session.SubGoalIndex = watchSub;
            slipped = new SlipEvent(dummy);
            server.EntMan.EventBus.RaiseLocalEvent(soap, ref slipped);
            Assert.That(tutorial.TryGetCurrentSubGoal(mob, part, out var second), Is.True);
            Assert.That(second.Id, Is.EqualTo("pickup-soap"), "the next slip has to count on its own");
        });
    }

    [Test]
    public async Task TutorialJanitor_JumpskirtOrJumpsuitCompletesTheUniform()
    {
        var pair = Pair;
        var server = pair.Server;
        var ticker = server.System<GameTicker>();
        var tutorial = server.System<TutorialServerRuleSystem>();
        var inventory = server.System<InventorySystem>();

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset("TutorialServer");
            ticker.StartGameRule(TutorialRule, out _);
            ticker.StartRound();
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
            tutorial.TrySelectRole(pair.Player!, RoleId, confirmedStub: false));

        var ready = false;
        for (var i = 0; i < 100 && !ready; i++)
        {
            await pair.RunTicksSync(10);
            await server.WaitPost(() =>
            {
                ready = pair.Player?.AttachedEntity is { } body &&
                        server.EntMan.HasComponent<TutorialParticipantComponent>(body);
            });
        }

        Assert.That(ready, Is.True, "the player never entered the janitor tutorial");

        await server.WaitAssertion(() =>
        {
            var mob = pair.Player!.AttachedEntity!.Value;
            var role = server.ProtoMan.Index<TutorialRolePrototype>(RoleId);
            Assert.That(TryFindSubGoal(role, "wear-suit", out var goalIndex, out var subIndex), Is.True);
            Assert.That(tutorial.TryGetSession(mob, out var session), Is.True);
            session.GoalIndex = goalIndex;
            session.SubGoalIndex = subIndex;
            session.AwaitingChamberEntryPad = false;

            var coords = server.EntMan.GetComponent<TransformComponent>(mob).Coordinates;
            inventory.TryUnequip(mob, "jumpsuit", force: true, silent: true);
            var skirt = server.EntMan.SpawnEntity("ClothingUniformJumpskirtJanitor", coords);
            Assert.That(inventory.TryEquip(mob, skirt, "jumpsuit", force: true, silent: true), Is.True);

            var part = server.EntMan.GetComponent<TutorialParticipantComponent>(mob);
            Assert.That(tutorial.TryGetCurrentSubGoal(mob, part, out var afterSkirt), Is.True);
            Assert.That(afterSkirt.Id, Is.EqualTo("cryo-tip"), "the jumpskirt has to count as the uniform");

            session.GoalIndex = goalIndex;
            session.SubGoalIndex = subIndex;
            session.AwaitingChamberEntryPad = false;
            inventory.TryUnequip(mob, "jumpsuit", force: true, silent: true);
            var suit = server.EntMan.SpawnEntity("ClothingUniformJumpsuitJanitor", coords);
            Assert.That(inventory.TryEquip(mob, suit, "jumpsuit", force: true, silent: true), Is.True);
            Assert.That(tutorial.TryGetCurrentSubGoal(mob, part, out var afterSuit), Is.True);
            Assert.That(afterSuit.Id, Is.EqualTo("cryo-tip"), "the jumpsuit still has to count as the uniform");
        });
    }

    private static bool TryFindSubGoal(TutorialRolePrototype role, string id, out int goalIndex, out int subIndex)
    {
        for (var g = 0; g < role.Goals.Count; g++)
        {
            var goal = role.Goals[g];
            for (var s = 0; s < goal.SubGoals.Count; s++)
            {
                if (goal.SubGoals[s].Id != id)
                    continue;

                goalIndex = g;
                subIndex = s;
                return true;
            }
        }

        goalIndex = 0;
        subIndex = 0;
        return false;
    }
}
