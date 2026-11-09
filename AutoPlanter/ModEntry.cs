using StardewModdingAPI;

namespace AutoPlanter;

/// <summary>SMAPI entry point. Gameplay systems are added in subsequent implementation steps.</summary>
internal sealed class ModEntry : Mod
{
    public override void Entry(IModHelper helper)
    {
        Monitor.Log("AutoPlanter loaded. Horse-and-plow gameplay systems are not implemented yet.", LogLevel.Info);
    }
}
