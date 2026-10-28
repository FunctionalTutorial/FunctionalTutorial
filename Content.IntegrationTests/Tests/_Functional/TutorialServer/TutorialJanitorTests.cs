#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server._Functional.TutorialServer;
using Content.Server.Decals;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules.Components;
using Content.Shared._Functional.TutorialServer;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Decals;
using Content.Shared.Inventory;
using Content.Shared.Disposal.Tube;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Roles;
using Content.Shared.Tag;
using NUnit.Framework;
using Content.Shared.Light.Components;
using Robust.Shared.GameObjects;
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

            Assert.That(maps.TryLoadTutorialMap(role, out var mapUid, out var gridUid, out _), Is.True);

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
            Assert.That(exits, Is.EqualTo(1));
            Assert.That(stockCryo, Is.EqualTo(0));

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
