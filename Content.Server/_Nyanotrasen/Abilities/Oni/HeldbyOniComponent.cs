namespace Content.Server._Nyanotrasen.Abilities.Oni
{
    [RegisterComponent]
    public sealed partial class HeldByOniComponent : Component
    {
        public EntityUid Holder = default!;
    }
}
