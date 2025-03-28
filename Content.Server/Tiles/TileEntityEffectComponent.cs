using Content.Shared.EntityEffects;

namespace Content.Server.Tiles;

/// <summary>
/// Applies effects upon stepping onto a tile.
/// </summary>
[RegisterComponent, Access(typeof(TileEntityEffectSystem))]
public sealed partial class TileEntityEffectComponent : Component
{
    /// <summary>
    /// List of effects that should be applied.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public List<EntityEffect> Effects = default!;
}
