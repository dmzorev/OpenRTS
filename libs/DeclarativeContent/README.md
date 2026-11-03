# DeclarativeContent

Engine-independent C# library for YAML definitions, inheritance, strict parameter binding and trait factories. Targets .NET 8; works in newer .NET applications. No Godot or Hyperfleets references. MIT; depends on YamlDotNet 18.1.0 (MIT).

This is an original implementation inspired by the data/trait pattern. It does not include OpenRA/OpenRTS source code and is not a MiniYaml compatibility layer.

## Pipeline

`ContentSource → DefinitionSet → TraitRegistry<TContext> → TraitPlan<TContext> → TraitSet`

The caller supplies named text sources; there is no engine filesystem, global game state, assembly scanning or arbitrary class loading in this library. YAML names resolve only through explicit registration. Parameter types use public settable properties, `[Required]`, `[Range]`, `[MinLength]` and optional `IValidateContent`. Unknown fields, invalid values and incomplete concrete definitions fail before spawning.

```csharp
using DeclarativeContent;
using System.ComponentModel.DataAnnotations;

var definitions = DefinitionSet.Load(new[] { new ContentSource("units.yaml", yamlText) });
var registry = new TraitRegistry<WorldContext>()
    .Register<HealthOptions>("Health", (world, key, options) => new Health(options.HP), required: true)
    .Register<WeaponOptions>("Armament", (world, key, options) => world.CreateWeapon(options.Weapon),
        multiple: true, requires: new[] { "Health" });
registry.ValidateTemplates(definitions);
var plan = registry.Compile("scout", definitions.All["scout"]);
var entityTraits = plan.Create(worldContext);
var health = entityTraits.Single<Health>();
```

`WorldContext`, `Health`, `HealthOptions` and `WeaponOptions` belong to the host project. Factories return host-defined runtime objects. Factories run in dependency order; each call binds fresh options, so one entity's state cannot mutate another entity's option instance. The host controls scheduling and decides which interfaces its systems execute; the library has no tick loop.

## Definition syntax

```yaml
^unit:
  Health:
    HP: 100
  Armament@primary:
    Weapon: light-gun

scout:
  Inherits: ^unit
  Health:
    HP: 60
  Armament@secondary:
    Weapon: flare-launcher

unarmed-scout:
  Inherits: scout
  -Armament@primary:
  -Armament@secondary:
```

Rules of version 0.1:

- YAML indentation uses spaces; tabs are rejected. Standard quoted strings, comments, block strings, maps and sequences use YamlDotNet.
- `^` marks abstract definitions; abstract definitions can be incomplete. `Concrete` excludes them.
- `Inherits` and `Inherits@LABEL` name parents in the same definition set. Parents may appear later or in another file. Each file contains one document.
- Parents merge in declaration order; later parents win. Local fields apply last regardless of the placement of `Inherits`. Diamond inheritance follows that same rule, including reapplication of common ancestor values through a later parent.
- Maps merge recursively. Scalars and arrays replace. Empty map `{}` preserves inherited map values. To remove a block or nested field use `-ExactKey:` with an empty value. Removing a nonexistent key is an error. Remove then re-add a map to replace it entirely.
- `Trait@left` and `Trait@right` are separate keys but resolve to the same registered trait type. Multiple instances must be enabled in the registry. Dependency names refer to trait types, not instance labels.
- IDs and keys are case sensitive. Duplicate IDs across files and duplicate YAML keys are errors; there is no implicit file override order. Use derived IDs for variants.
- Anchors, aliases, tags, YAML merge keys, nested `Inherits` and inheritance cycles are rejected. This keeps inheritance explicit and prevents type names in YAML from loading code.
- Errors include source file, line, column and logical path. Values preserve their source locations through merging.
- `ValidateTemplates` checks known fields and supplied values on abstract definitions. `Compile` validates concrete definitions, required fields, trait multiplicity and dependency order. Cross-definition references and game-specific constraints belong to the host.

`Options.Bind<T>` also works outside a trait registry. Supported parameter shapes: string, bool, int, finite double, named enum, array, string-keyed dictionary and nested parameter classes. Empty optional marker traits may use `{}`. No implicit numeric enum values or culture-dependent decimal commas.

`ContentSchema.Generate(registry.OptionTypes)` generates JSON Schema for editor completion of partial definitions. It intentionally does not enforce required fields before inheritance; the loader is authoritative. Reference IDs and game-specific constraints are validated by the host.

## Reuse and packaging

```sh
dotnet pack DeclarativeContent.csproj -c Release -o packages
```

The folder can later be moved to `net-libs/DeclarativeContent` without source changes; consumers can use `ProjectReference` or a locally produced NuGet package. Package ID is provisional until public publication. This repository builds a local package only; no public registry/repository is configured.

Current limits: reflection-based binding requires preserving registered option types in trimmed/AOT builds; no hot reload, package discovery, property expressions, editor round-trip writing, entity lifecycle or dependency injection container. The public node record permits host-constructed nodes; the YAML loader returns read-only maps/lists. Treat definition trees and compiled plans as configuration, and store mutable gameplay state in factory results.

Tests live in `tests/DeclarativeContent.Tests` in the host repository and reference only this library. Copy that test project with the library when extracting it; adjust its relative ProjectReference.
