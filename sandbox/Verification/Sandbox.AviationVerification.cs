using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Rendering;

public partial class Sandbox
{
    private bool _verifyAviation;
    private async void VerifyAviation(FrameExchange.FrameLease frame)
    {
        try
        {
            _verificationDone = true;
            VerifyAirWorkVisuals();
            VerifyAirTransportInput();
            VerifyTiltrotorCombatRoles();
            if (_manifest.Settings.Presentation.ModelOverrides.GetValueOrDefault("airfield") == "iso-airfield")
                VerifyIsometricAirfield();
            if (_manifest.Settings.Presentation.ModelOverrides.GetValueOrDefault("fighter") == "iso-fighter")
                VerifyIsometricFighter(frame);
            if (_manifest.Settings.Presentation.ModelOverrides.GetValueOrDefault("tiltrotor") == "iso-tiltrotor")
                VerifyIsometricTiltrotor(frame);
            if (_manifest.Settings.Presentation.ModelOverrides.GetValueOrDefault("heavy-air-transport") == "iso-heavy-air-transport")
                VerifyIsometricHeavyTransport(frame);
            var poses = frame.Poses.ToArray();
            var report = _session!.Completion.GetAwaiter().GetResult();
            if (frame.Tick != report.Tick || !poses.Any(p => _catalog!.Units[p.UnitType].Airbase is not null) ||
                !poses.Any(p => _catalog!.Units[p.UnitType].Flight is not null) || _sprites!.SpriteCount < poses.Length)
                throw new InvalidOperationException("Incomplete aviation presentation.");
            var engineer = poses.Single(p => p.Id.Value == 13);
            if (engineer.Order.Kind != UnitActionKind.Capture || engineer.Order.Phase != OrderPhase.Engaging ||
                !engineer.Flight.Airborne || poses.Single(p => p.Id.Value == 16).Upgrade.Completed != "owned" ||
                poses.Where(p => p.Id.Value is 18 or 19).Any(p => p.Carrier != default || p.Current.Elevation != 0))
                throw new InvalidOperationException("Air construction, upgrade, capture or mixed-size transport failed in the shared scenario.");
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _selected = engineer.Id;
                _camera!.Size = 42; CenterCamera(new(32, 38));
                RefreshSelection(); UpdateCommandPanel(frame); UpdateSelection(1); UpdateTerminal(frame, 1);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                string path = Path.GetFullPath(_capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(path) != Error.Ok) throw new IOException("Aviation capture failed.");
            }
            GD.Print("OPENRTS_AVIATION:PASS");
            GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report)); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private void VerifyIsometricFighter(FrameExchange.FrameLease frame)
    {
        var catalog = _catalog!;
        int type = catalog.Units.ToList().FindIndex(unit => unit.Id == "fighter");
        var unit = catalog.Units[type];
        var north = FacingDirection.North;
        var east = FacingDirection.FromQuarterTurns(1);
        var ground = new WorldPosition(32 * 1024, 32 * 1024);
        var air = ground with { Elevation = unit.Flight!.CruiseAltitude };
        var parked = new EntityPose(new(90003), type, ground, ground,
            new(1, east, east, north, north, new(unit.IdleAnimation, 0, 0), AnimationPlayback.None, 0));
        var flying = parked with
        {
            Previous = air, Current = air,
            Flight = new(FlightPhase.Flying, 0, 0, default, -1, true),
            Presentation = parked.Presentation with { States = unit.StateMask("airborne"), Body = new(unit.MoveAnimation, 0, 0) }
        };

        foreach (var (pose, atlas) in new[] { (parked, "fighter.png"), (flying, "fighter-airborne.png") })
        {
            _sprites!.Apply([pose], 1, 0, frame.TickRate);
            var visible = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>()
                .Where(node => node.Multimesh.VisibleInstanceCount > 0)
                .Select(node => (ShaderMaterial)node.Multimesh.Mesh.SurfaceGetMaterial(0)).ToArray();
            if (visible.Length != (pose.Flight.Airborne ? 2 : 1) || visible.Any(material =>
                    material.GetShaderParameter("sprite_texture").AsGodotObject() is not Texture2D texture ||
                    !texture.ResourcePath.EndsWith("/isometric/" + atlas, StringComparison.Ordinal) ||
                    !Mathf.IsEqualApprox(material.GetShaderParameter("uv_rect").AsVector4().Y, .5f)))
                throw new InvalidOperationException("Fighter gear or heading did not match the rendering pose.");
        }

        var effects = OpenRTS.Content.EffectCatalog.Load(_manifest.Effects).Weapons;
        var effect = _combatEffects!.LaunchVisual(type, 0, new(1, flying.Id, air, east, north),
            effects[unit.Weapons[0].Weapon.Id], Vector3.Right);
        var socket = new Vector3(32 + .51f, air.Elevation / 1024f + .173f, 32);
        if (effect is not { } launch ||
            _camera!.UnprojectPosition(launch.Center).DistanceTo(_camera.UnprojectPosition(socket)) > 0.05f)
            throw new InvalidOperationException("Fighter launch effect did not follow its body heading and altitude.");

        _sprites!.Apply(frame.Poses, 1, frame.Tick, frame.TickRate, frame.Weapons, contacts: frame.Contacts);
    }

    private void VerifyIsometricTiltrotor(FrameExchange.FrameLease frame)
    {
        var types = _catalog!.Units.ToList();
        int aircraft = types.FindIndex(unit => unit.Id == "tiltrotor");
        int rifleman = types.FindIndex(unit => unit.Id == "rifleman");
        const int size = 48;
        byte grass = _catalog.Locomotion!.TerrainIndex("grass");
        using var world = new SimulationWorld(types, size * 1024, size * 1024,
            navigationGrid: new(size, size, terrain: Enumerable.Repeat(grass, size * size)));
        var start = new WorldPosition(16384, 16384);
        var target = start with { X = start.X + 8192 };
        var carrier = world.AddInitialUnit(aircraft, start, 1);
        EntityId[] squad =
        [
            world.AddInitialUnit(rifleman, start with { X = start.X - 800 }, 1),
            world.AddInitialUnit(rifleman, start with { X = start.X + 800 }, 1),
            world.AddInitialUnit(rifleman, start with { Y = start.Y - 800 }, 1),
            world.AddInitialUnit(rifleman, start with { Y = start.Y + 800 }, 1)
        ];
        var poses = new EntityPose[world.Count];
        world.Step([new(1, 0, 1, CommandKind.Enter, TargetEntity: carrier, Recipients: new(squad))]);
        world.CopyPoses(poses);
        if (poses.Count(pose => pose.Carrier == carrier) != 4)
            throw new InvalidOperationException("Four infantry must board the parked tiltrotor.");
        var parked = poses.Single(pose => pose.Id == carrier);
        AircraftAtlasFrame(parked, "tiltrotor.png", 0, frame.TickRate);

        world.Step([new(2, 0, 1, CommandKind.Move, carrier, target)]);
        for (int i = 0; i < 100; i++) world.Step();
        world.CopyPoses(poses);
        var hovering = poses.Single(pose => pose.Id == carrier);
        if (!hovering.Flight.Airborne || poses.Count(pose => pose.Carrier == carrier) != 4 ||
            AircraftAtlasFrame(hovering, "tiltrotor-airborne.png", 0, frame.TickRate).X ==
            AircraftAtlasFrame(hovering, "tiltrotor-airborne.png", (int)Math.Ceiling(frame.TickRate / 12d), frame.TickRate).X)
            throw new InvalidOperationException("The loaded tiltrotor must retain its squad and animate the rotors in flight.");

        world.Step([new(world.Tick + 1, 0, 1, CommandKind.UnloadAt, carrier, target)]);
        for (int i = 0; i < 100; i++) world.Step();
        world.CopyPoses(poses);
        if (poses.Any(pose => pose.Carrier == carrier || pose.Current.Elevation != 0))
            throw new InvalidOperationException("The tiltrotor must land before unloading its squad.");
        AircraftAtlasFrame(poses.Single(pose => pose.Id == carrier), "tiltrotor.png", 0, frame.TickRate);
        _sprites!.Apply(frame.Poses, 1, frame.Tick, frame.TickRate, frame.Weapons, contacts: frame.Contacts);
        GD.Print("OPENRTS_TILTROTOR:PASS four passengers, landing doors and rotor phases");
    }

    private Vector4 AircraftAtlasFrame(EntityPose pose, string atlas, int tick, int rate)
    {
        _sprites!.Apply([pose], 0, tick, rate);
        var visible = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>()
            .Where(node => node.Multimesh.VisibleInstanceCount > 0)
            .Select(node => (ShaderMaterial)node.Multimesh.Mesh.SurfaceGetMaterial(0)).ToArray();
        if (visible.Length != (pose.Flight.Airborne ? 2 : 1) || visible.Any(material =>
                material.GetShaderParameter("sprite_texture").AsGodotObject() is not Texture2D texture ||
                !texture.ResourcePath.EndsWith("/isometric/" + atlas, StringComparison.Ordinal)))
            throw new InvalidOperationException($"The aircraft pose did not select {atlas}.");
        return visible[0].GetShaderParameter("uv_rect").AsVector4();
    }

    private void VerifyIsometricHeavyTransport(FrameExchange.FrameLease frame)
    {
        var units = _catalog!.Units.ToList();
        int type = units.FindIndex(unit => unit.Id == "heavy-air-transport");
        var unit = units[type];
        var ground = new WorldPosition(32 * 1024, 32 * 1024);
        var north = FacingDirection.North;
        var east = FacingDirection.FromQuarterTurns(1);
        foreach (var (airborne, atlas) in new[]
        {
            (false, "heavy-air-transport.png"),
            (true, "heavy-air-transport-airborne.png")
        })
        {
            var at = ground with { Elevation = airborne ? unit.Flight!.CruiseAltitude : 0 };
            uint states = airborne ? unit.StateMask("airborne") : 0;
            var pose = new EntityPose(new(90004), type, at, at,
                new(1, east, east, east, east, new(unit.IdleAnimation, 0, 0), AnimationPlayback.None, states))
            { Flight = new(airborne ? FlightPhase.Hovering : FlightPhase.Landed, 0, 0, default, -1, airborne) };
            var art = AircraftAtlasFrame(pose, atlas, 0, frame.TickRate);
            var turned = pose with { Presentation = pose.Presentation with { BodyFacing = north, PreviousBodyFacing = north } };
            if (art == AircraftAtlasFrame(turned, atlas, 0, frame.TickRate) || (airborne && art.X == AircraftAtlasFrame(pose, atlas,
                    (int)Math.Ceiling(frame.TickRate / 12d), frame.TickRate).X))
                throw new InvalidOperationException($"{atlas} did not follow the heading or animate while hovering.");
        }
        _sprites!.Apply(frame.Poses, 1, frame.Tick, frame.TickRate, frame.Weapons, contacts: frame.Contacts);
        GD.Print("OPENRTS_HEAVY_TRANSPORT:PASS ground ramp, heading and hovering rotor phases");
    }

    private void VerifyTiltrotorCombatRoles()
    {
        var types = _catalog!.Units.ToList();
        int Type(string id) => types.FindIndex(unit => unit.Id == id);
        int padType = Type("helipad"), plantType = Type("power-plant");
        const int size = 56;
        byte grass = _catalog.Locomotion!.TerrainIndex("grass");
        foreach (var (id, capacity, weaponKind) in new[]
                 { ("tiltrotor", 0, ProjectileKind.Hitscan), ("missile-tiltrotor", 8, ProjectileKind.Missile) })
        {
            int type = Type(id);
            var definition = types[type];
            if (definition.Flight!.Ammo != capacity || definition.Weapons.Single().Weapon.Projectile != weaponKind ||
                (capacity == 0 ? definition.Transport?.Slots != 4 : definition.Transport is not null) ||
                !types[padType].Products.Any(product => product.Unit == id))
                throw new InvalidOperationException($"{id} has the wrong combat role or cannot be built at a helipad.");

            using var world = new SimulationWorld(types, size * 1024, size * 1024,
                navigationGrid: new(size, size, terrain: Enumerable.Repeat(grass, size * size)),
                players: [new(1, "Blue"), new(2, "Red")]);
            var pad = world.AddInitialUnit(padType, new(24 * 1024, 35 * 1024), 1);
            world.AddInitialUnit(plantType, new(16 * 1024 + 512, 35 * 1024), 1);
            var target = world.AddInitialUnit(plantType, new(24 * 1024 + 512, 24 * 1024), 2);
            var aircraft = world.AddInitialUnit(type, new(24 * 1024 + 512, 28 * 1024, definition.Flight.CruiseAltitude), 1);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, aircraft, TargetEntity: target)]);

            int shots = 0;
            bool returnedEmpty = false, rearmed = false;
            for (int tick = 0; tick < 600; tick++)
            {
                foreach (var evt in world.Events)
                    if (evt.Kind == WorldEventKind.WeaponFired && evt.Entity == aircraft) shots++;
                world.TryGetFlight(aircraft, out var flight);
                if (capacity == 0 && (!flight.Airborne || flight.Airbase != default))
                    throw new InvalidOperationException("Infantry support must stay airborne without seeking an ammo refill.");
                returnedEmpty |= flight is { Phase: FlightPhase.Servicing, Ammo: 0 } && flight.Airbase == pad;
                rearmed |= returnedEmpty && flight.Ammo == capacity;
                if (shots > 8 && (capacity == 0 || rearmed)) break;
                world.Step();
            }
            if (shots <= 8 || capacity > 0 && !rearmed)
                throw new InvalidOperationException($"{id} did not sustain its attack: shots={shots}, serviced={returnedEmpty}, rearmed={rearmed}.");
        }
        GD.Print("OPENRTS_TILTROTOR_COMBAT:PASS sustained ground fire and missile rearm with attack resumed");
    }

    private void VerifyIsometricAirfield()
    {
        var units = _catalog!.Units.ToList();
        int fieldType = units.FindIndex(t => t.Id == "airfield");
        int fighterType = units.FindIndex(t => t.Id == "fighter");
        var airbase = units[fieldType].Airbase!;
        const int size = 64;
        byte grass = _catalog.Locomotion!.TerrainIndex("grass");
        using var world = new SimulationWorld(units, size * 1024, size * 1024,
            navigationGrid: new(size, size, terrain: Enumerable.Repeat(grass, size * size)));
        var origin = new WorldPosition(32 * 1024, 32 * 1024);
        var field = world.AddInitialUnit(fieldType, origin, 1);
        WorldPosition At(WorldPosition offset) => new(origin.X + offset.X, origin.Y + offset.Y);

        // These offsets are the centers of the painted stands and runway in the Blender model.
        WorldPosition[] stands = [new(-2624, -2432), new(-576, -2432), new(1600, -1152), new(1600, 1024)];
        var runwayStart = At(new(-1664, -1536));
        var runwayEnd = At(new(-1664, 4608));
        if (!airbase.Slots.SequenceEqual(stands) || At(airbase.RunwayStart!.Value) != runwayStart ||
            At(airbase.RunwayEnd!.Value) != runwayEnd)
            throw new InvalidOperationException("Airfield parking or runway is out of sync with the model.");
        var fighters = stands.Select(at => world.AddInitialUnit(fighterType, At(at), 1)).ToArray();
        var poses = new EntityPose[world.Count];
        world.Step([new(1, 0, 1, CommandKind.MoveSelected, Recipients: new(fighters), Position: new(49152, 49152))]);
        var departed = new bool[fighters.Length];
        var observedRun = new bool[fighters.Length];
        for (int tick = 0; tick < 360 && departed.Any(value => !value); tick++)
        {
            world.Step();
            world.CopyPoses(poses);
            if (poses.Count(p => p.Flight.Phase is FlightPhase.Taxiing or FlightPhase.RunwayTakeoff) > 1)
                throw new InvalidOperationException("Two fighters reserved the same runway.");
            for (int i = 0; i < fighters.Length; i++)
            {
                var fighter = poses.Single(p => p.Id == fighters[i]);
                if (fighter.Flight.Phase == FlightPhase.RunwayTakeoff)
                {
                    if ((!observedRun[i] && fighter.Current != runwayStart) ||
                        fighter.Current.X != runwayStart.X || fighter.Flight.Airbase != field)
                        throw new InvalidOperationException($"Fighter from stand {i + 1} missed the runway.");
                    observedRun[i] = true;
                }
                if (observedRun[i] && !departed[i] && fighter.Flight.Phase == FlightPhase.Flying)
                {
                    if (fighter.Current.X != runwayEnd.X || fighter.Current.Y != runwayEnd.Y || fighter.Current.Elevation <= 0)
                        throw new InvalidOperationException($"Fighter from stand {i + 1} did not depart through the open end.");
                    departed[i] = true;
                }
            }
        }
        if (departed.Any(value => !value))
            throw new InvalidOperationException("A fighter failed to depart from its airfield stand.");

        world.Step(fighters.Select((fighter, i) =>
            new WorldCommand(world.Tick + 1, 0, i + 1, CommandKind.ReturnToBase, fighter)).ToArray());
        var returned = new bool[fighters.Length];
        var observedLanding = new bool[fighters.Length];
        var landingY = new int[fighters.Length];
        for (int tick = 0; tick < 900 && returned.Any(value => !value); tick++)
        {
            world.Step();
            world.CopyPoses(poses);
            if (poses.Count(p => p.Flight.Phase is FlightPhase.RunwayLanding or FlightPhase.Taxiing) > 1)
                throw new InvalidOperationException("Two fighters used the same runway on return.");
            for (int i = 0; i < fighters.Length; i++)
            {
                var fighter = poses.Single(p => p.Id == fighters[i]);
                if (fighter.Flight.Phase == FlightPhase.RunwayLanding)
                {
                    if ((!observedLanding[i] && fighter.Current.Y <= runwayEnd.Y) ||
                        (observedLanding[i] && fighter.Current.Y >= landingY[i]) ||
                        fighter.Current.X != runwayStart.X || fighter.Flight.Airbase != field)
                        throw new InvalidOperationException($"Fighter from stand {i + 1} did not approach from the open end.");
                    observedLanding[i] = true;
                    landingY[i] = fighter.Current.Y;
                }
                returned[i] |= observedLanding[i] && fighter.Current == At(stands[i]) &&
                    fighter.Flight.Phase is FlightPhase.Landed or FlightPhase.Servicing;
            }
        }
        if (returned.Any(value => !value))
            throw new InvalidOperationException("A fighter failed to return to its airfield stand. " +
                string.Join(", ", poses.Where(p => p.Id != field).Select(p => $"{p.Id}: {p.Flight.Phase} at {p.Current}")));
        GD.Print("OPENRTS_ISOMETRIC_AIRFIELD:PASS four departures and four open-end landings");
    }

    private void VerifyAirWorkVisuals()
    {
        var units = _catalog!.Units;
        int worker = units.ToList().FindIndex(t => t.Id == "engineering-tiltrotor");
        int target = units.ToList().FindIndex(t => t.Id == "power-plant");
        var at = new WorldPosition(16384, 16384, 3584);
        var building = new EntityPose(new(90002), target, at with { Elevation = 0 }, at with { Elevation = 0 });
        foreach (var kind in new[] { UnitActionKind.Build, UnitActionKind.Capture, UnitActionKind.UpgradeSupport })
        {
            var roaming = new WorkFlightVisual(units.Select(p => p.Flight?.WorkRoaming ?? false).ToArray());
            var fixedHover = new WorkFlightVisual(new bool[units.Count]);
            var actor = new EntityPose(new(90001), worker, at, at,
                Order: new(kind, OrderPhase.Engaging, at, building.Id, 0, 0))
                { Flight = new(FlightPhase.Hovering, 0, 0, default, -1, true) };
            var moving = actor with { Current = at with { X = at.X + 1024 } };
            var effects = _combatEffects!;
            var source0 = effects.WorkSource(moving, at with { Y = at.Y + 4096 }, 0);
            var source1 = effects.WorkSource(moving, at with { Y = at.Y + 4096 }, 1);
            if (source0 == source1 || source1.Elevation != moving.Current.Elevation)
                throw new InvalidOperationException("The work beam did not follow the moving aircraft at flight height.");
            EntityPose[] poses = [actor, building];
            roaming.Update(poses, 10); fixedHover.Update(poses, 10);
            float heading = roaming.Heading(actor, 80, 0);
            if (heading != roaming.Heading(actor, 85, 0) || heading == roaming.Heading(actor, 136, 0) ||
                fixedHover.Heading(actor, 80, 0.7f) != 0.7f || MathF.Abs(roaming.Heading(actor, 10, 0)) > 0.0001f)
                throw new InvalidOperationException($"Roof heading failed: hold={heading}/{roaming.Heading(actor, 85, 0)}, next={roaming.Heading(actor, 136, 0)}, disabled={fixedHover.Heading(actor, 80, 0.7f)}, start={roaming.Heading(actor, 10, 0)}.");
            var repair = actor with { Order = actor.Order with { Kind = UnitActionKind.Repair } };
            roaming.Update([repair, building], 150);
            if (MathF.Abs(Mathf.Wrap(roaming.Heading(repair, 162, 0), -MathF.PI, MathF.PI)) > 0.0001f)
                throw new InvalidOperationException("Repair kept roaming after its transition.");
        }
    }

    private void VerifyAirTransportInput()
    {
        var savedSelection = _selection.Ids.ToArray();
        var savedPointer = _pointer;
        float savedSize = _camera!.Size;
        var savedPosition = _camera.Position;
        foreach (var (carrierId, passengerId) in new[] { (15u, 18u), (5u, 7u) })
        {
            int index = Array.FindIndex(_displayPoses, 0, _displayCount, p => p.Id.Value == carrierId);
            var original = _displayPoses[index];
            var at = original.Current with { Elevation = 3584 };
            var flying = original with { Previous = at, Current = at, Flight = original.Flight with { Phase = FlightPhase.Hovering, Airborne = true } };
            _displayPoses[index] = flying;
            _selection.Change([new(passengerId)], OpenRTS.Godot.Controls.SelectionChange.Replace);
            RefreshSelection();
            foreach (float zoom in new[] { 24f, 48f })
            {
                _camera.Size = zoom;
                CenterCamera(new(at.X / 1024f, at.Y / 1024f));
                var center = _sprites!.DisplayPosition(flying, 1);
                float extent = carrierId == 15 ? 1.6f : 0.9f;
                _pointer = _camera.UnprojectPosition(center + new Vector3(extent, 0, 0));
                var point = CommandPoint(_pointer);
                if (point is null || Pick(point.Value)?.Id != flying.Id || !CanBoardSelected(flying))
                    throw new InvalidOperationException("Aircraft model bounds or airborne boarding selection failed at this zoom.");
                UpdateRtsCursor();
                if (_rtsOverlay!.Cursor != OpenRTS.Godot.Controls.RtsCursor.Enter)
                    throw new InvalidOperationException("Hovering transport did not offer its Enter cursor.");
            }
            _displayPoses[index] = original;
        }
        _selection.Change(savedSelection, OpenRTS.Godot.Controls.SelectionChange.Replace);
        _pointer = savedPointer; _camera.Size = savedSize; _camera.Position = savedPosition;
        RefreshSelection();
    }
}
