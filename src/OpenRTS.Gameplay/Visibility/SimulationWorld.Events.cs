using OpenRTS.Core.Simulation;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private void EmitEvent(WorldEvent value)
    {
        if (_fog is not null)
        {
            ulong audience = 0, entities = 0, targets = 0;
            bool spatial = value.Kind is WorldEventKind.Spawned or WorldEventKind.Destroyed or WorldEventKind.Removed or
                WorldEventKind.WeaponFired or WorldEventKind.ProjectileImpact or WorldEventKind.ProjectileExpired or
                WorldEventKind.Damaged or WorldEventKind.Repaired or WorldEventKind.AnimationPlayed or WorldEventKind.OwnerChanged or
                WorldEventKind.ActorTransformed or WorldEventKind.TransformationCompleted;
            uint owner = TryGetGameplayOwner(value.Entity, out uint currentOwner) ? currentOwner : value.Owner;
            for (int i = 0; i < _fog.Players.Count; i++)
            {
                uint player = _fog.Players[i]; ulong bit = 1ul << i;
                bool entity = IsVisible(player, value.Entity) || value.Kind == WorldEventKind.OwnerChanged && value.PreviousOwner == player, target = IsVisible(player, value.TargetEntity);
                if (entity) entities |= bit;
                if (target) targets |= bit;
                bool location = _fog.At(player, value.Position) == CellVisibility.Visible;
                bool effect = value.Kind is WorldEventKind.ProjectileImpact or WorldEventKind.ProjectileExpired;
                if (spatial ? (effect ? location : value.Entity.Value != 0 ? entity : location) : owner == player || value.Owner == player) audience |= bit;
            }
            value = value with { Visibility = new(_fog.Players, audience, entities, targets) };
        }
        _events.Add(value);
    }
}
