using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private readonly Dictionary<string, Button> _upgradeButtons = new();

    private static string UpgradeLabel(string id) => id switch
    {
        "owned" => "Оборудовать шахту",
        "rockets" => "Ракетная установка",
        "cannon" => "Пушка",
        "firing-ports" => "Бойницы для пехоты",
        "expanded" => "Гарнизон на 8 мест",
        "mixed" => "Пулемёт и ракеты",
        "heavy" => "Тяжёлое производство",
        _ => id
    };

    private bool UpgradeAvailable(EntityPose target, UpgradeDefinition upgrade, uint owner) =>
        owner != 0 && !target.Construction.Active && target.Upgrade is { Completed: null, Project: null } &&
        (upgrade.ClaimNeutral ? target.Presentation.Owner == 0 &&
            (_catalog!.Units[target.UnitType].Supply?.Role != SupplyRole.Mine || target.Supply.Amount > 0) : target.Presentation.Owner == owner);

    private bool CanSupportUpgrade(EntityPose actor, EntityPose target)
    {
        var type = _catalog!.Units[actor.UnitType].WithRotation(actor.QuarterTurns);
        if (actor.Id == target.Id || type.ConstructionRange <= 0 || actor.Construction.Active ||
            actor.Upgrade.Active || actor.TerrainStranded) return false;
        return type.Movable || ObjectGeometry.DistanceSquared(type, actor.Current, _catalog.Units[target.UnitType].WithRotation(target.QuarterTurns),
            target.Current, out _, out _) <= (long)type.ConstructionRange * type.ConstructionRange;
    }

    private (EntityId Actor, UpgradeDefinition Upgrade)? ContextUpgrade(EntityPose target)
    {
        var upgrade = _catalog!.Units[target.UnitType].DefaultUpgrade;
        if (upgrade is not { Support: true } || !UpgradeAvailable(target, upgrade, CommandOwner)) return null;
        foreach (var id in ActiveRecipients())
            if (CanSupportUpgrade(_selectionPoses[id], target)) return (id, upgrade);
        return null;
    }

    private bool TryContextUpgrade(EntityPose target)
    {
        if (ContextUpgrade(target) is not { } command) return false;
        SubmitUpgrade(command.Actor, target.Id, command.Upgrade.Id);
        return true;
    }

    private void SubmitUpgrade(EntityId actor, EntityId target, string variant) =>
        _session!.Submit(CommandKind.StartUpgrade, entity: actor, targetEntity: target, action: variant);

    private void ActivateUpgrade(UpgradeDefinition variant)
    {
        foreach (var target in ActiveRecipients().Select(id => _selectionPoses[id]))
        {
            uint owner = target.Presentation.Owner;
            if (!UpgradeAvailable(target, variant, owner)) continue;
            if (!variant.Support)
            {
                SubmitUpgrade(target.Id, target.Id, variant.Id);
                return;
            }
            var supporter = _displayPoses.Take(_displayCount).Where(p => p.Presentation.Owner == owner &&
                    p.Order.Phase == OpenRTS.Core.Orders.OrderPhase.Idle && CanSupportUpgrade(p, target))
                .OrderBy(p => ObjectGeometry.DistanceSquared(_catalog!.Units[p.UnitType].WithRotation(p.QuarterTurns), p.Current,
                    _catalog.Units[target.UnitType].WithRotation(target.QuarterTurns), target.Current, out _, out _)).ThenBy(p => p.Id.Value).FirstOrDefault();
            if (supporter.Id != default)
            {
                SubmitUpgrade(supporter.Id, target.Id, variant.Id);
                return;
            }
        }
        _notice = "Для апгрейда нужен свободный инженер или кран.";
    }

    private void AddUpgradeButtons()
    {
        foreach (var upgrade in _catalog!.Units[_activeType].Upgrades)
        {
            if (upgrade.ClaimNeutral) continue;
            var button = IconButton(UpgradeLabel(upgrade.Id), null, 120);
            button.Pressed += () => ActivateUpgrade(upgrade);
            button.TooltipText = $"{UpgradeLabel(upgrade.Id)} · {upgrade.Cost:N0} кр.\n" +
                (upgrade.Support ? "Требует свободного инженера или крана." : "Выполняется самостоятельно.");
            _customRow!.AddChild(button);
            _upgradeButtons.Add(upgrade.Id, button);
        }
        var cancel = IconButton("Отменить апгрейд", null, 120);
        cancel.Visible = false;
        cancel.Pressed += () =>
        {
            if (Selected() is { } pose && pose.Upgrade.Project is not null && Controls(pose.Upgrade.Payer))
                CancelUpgrade(pose.Id, pose.Upgrade.Payer);
        };
        _customRow!.AddChild(cancel);
        _upgradeButtons.Add("", cancel);
    }

    private void UpdateUpgradeButtons(UnitDefinition type, EntityId[] ids)
    {
        foreach (var upgrade in type.Upgrades)
        {
            if (!_upgradeButtons.TryGetValue(upgrade.Id, out var button)) continue;
            var poses = ids.Select(id => _selectionPoses[id]);
            button.Disabled = !poses.Any(p => UpgradeAvailable(p, upgrade, p.Presentation.Owner));
            var building = poses.FirstOrDefault(p => p.Upgrade.Project == upgrade.Id);
            button.Text = building.Id == default ? UpgradeLabel(upgrade.Id) :
                $"{UpgradeLabel(upgrade.Id)} {100 * building.Upgrade.Work / building.Upgrade.Required}%";
        }
        if (_upgradeButtons.TryGetValue("", out var cancel))
            cancel.Visible = Selected() is { } pose && pose.Upgrade.Project is not null && Controls(pose.Upgrade.Payer);
    }

    private void CancelUpgrade(EntityId target, uint owner) =>
        _session!.Submit(CommandKind.CancelUpgrade, owner: owner, targetEntity: target);

    private ConstructionProjectPose? UpgradePreview()
    {
        if (_activeType < 0 || _activeRecipients.Length == 0 || _catalog!.Units[_activeType].ConstructionRange <= 0 ||
            _sitePlacement is not null || _placing || _armedAction is not null || _armedCustom is not null ||
            CommandPoint(_pointer) is not { } point || Pick(point) is not { } target ||
            ContextUpgrade(target) is not { } command) return null;
        return UpgradeGhost(target, command.Upgrade.Id, CommandOwner);
    }

    private static ConstructionProjectPose UpgradeGhost(EntityPose target, string variant, uint owner) =>
        new(target.Id, owner, target.UnitType, target.Current, false, variant, target.Presentation.BodyFacing)
        { QuarterTurns = target.QuarterTurns };
}
