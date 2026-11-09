using StardewModdingAPI.Events;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.TerrainFeatures;

namespace AutoPlanter;

internal sealed class ModEntry : Mod
{
    private const string BuildingId = "AutoPlanter.HorsePlow";
    private const string CursorKey = "AutoPlanter/WorkCursor";
    private const int WorkRadius = 20;
    private const int PatchSize = 3;
    private long lastWorkTick;
    private Texture2D? horseTexture;
    private Vector2? horsePosition;
    private Vector2? horseTarget;
    private int horseFrame;
    private int animationTicks;

    public override void Entry(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        Monitor.Log("AutoPlanter loaded. Horse-plow building data and planting logic registered.", LogLevel.Info);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo("Data/Buildings"))
            return;

        e.Edit(asset =>
        {
            var buildings = asset.AsDictionary<string, BuildingData>().Data;
            buildings[BuildingId] = new BuildingData
            {
                Name = "Horse Plow",
                Description = "A compact stable for a horse-drawn automatic planter.",
                Texture = "Buildings/Shipping Bin",
                Builder = "Robin",
                BuildCost = 50000,
                BuildDays = 1,
                Size = new Point(3, 3),
                CollisionMap = "XXX\nXOX\nXXX",
                Metadata = new Dictionary<string, string>
                {
                    ["AutoPlanter"] = "HorsePlow"
                }
            };
        });
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        UpdateHorseMovement();
        if (e.Ticks - lastWorkTick < 60)
            return;

        lastWorkTick = e.Ticks;
        Farm farm = Game1.getFarm();

        foreach (Building building in farm.buildings)
        {
            if (!building.buildingType.Value.Equals(BuildingId, StringComparison.Ordinal))
                continue;

            WorkOnePatch(farm, building);
        }
    }

    private void WorkOnePatch(Farm farm, Building building)
    {
        List<Point> patches = GetPatchCenters();
        int cursor = 0;
        if (building.modData.TryGetValue(CursorKey, out string? saved))
            int.TryParse(saved, out cursor);

        for (int checkedCount = 0; checkedCount < patches.Count; checkedCount++)
        {
            Point offset = patches[(cursor + checkedCount) % patches.Count];
            int centerX = building.tileX.Value + building.tilesWide.Value / 2 + offset.X;
            int centerY = building.tileY.Value + building.tilesHigh.Value / 2 + offset.Y;

            horseTarget = new Vector2((centerX + 0.5f) * Game1.tileSize, (centerY + 0.5f) * Game1.tileSize);
            if (horsePosition is null)
                horsePosition = new Vector2((building.tileX.Value + 1.5f) * Game1.tileSize, (building.tileY.Value + 2.5f) * Game1.tileSize);

            if (!HasSeedInInventory())
                return;

            int planted = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                Vector2 tile = new(centerX + dx, centerY + dy);
                if (Vector2.DistanceSquared(tile, new Vector2(
                        building.tileX.Value + building.tilesWide.Value / 2f,
                        building.tileY.Value + building.tilesHigh.Value / 2f)) > WorkRadius * WorkRadius)
                    continue;

                if (!farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature))
                {
                    var newDirt = new HoeDirt(0);
                    farm.terrainFeatures.Add(tile, newDirt);
                    feature = newDirt;
                }

                if (feature is not HoeDirt dirt || dirt.crop != null)
                    continue;

                if (TryPlantOneSeed(dirt, tile, farm))
                    planted++;
            }

            building.modData[CursorKey] = ((cursor + checkedCount + 1) % patches.Count).ToString();
            if (planted > 0)
                return;
        }
    }

    private static bool HasSeedInInventory()
    {
        return Game1.player.Items.Any(item =>
            item is StardewValley.Object obj && obj.Category == StardewValley.Object.SeedsCategory && obj.Stack > 0);
    }

    private static bool TryPlantOneSeed(HoeDirt dirt, Vector2 tile, Farm farm)
    {
        for (int i = 0; i < Game1.player.Items.Count; i++)
        {
            Item? item = Game1.player.Items[i];
            if (item is not StardewValley.Object seed || seed.Category != StardewValley.Object.SeedsCategory || seed.Stack <= 0)
                continue;

            // Stardew 1.6 changed the HoeDirt planting API; resolve its supported overload at runtime.
            var methods = typeof(HoeDirt).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(method => method.Name.Equals("plant", StringComparison.OrdinalIgnoreCase));
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                int intIndex = 0;
                bool supported = true;
                for (int p = 0; p < parameters.Length; p++)
                {
                    Type type = parameters[p].ParameterType;
                    if (type == typeof(int))
                        args[p] = intIndex++ switch { 0 => seed.ParentSheetIndex, 1 => (int)tile.X, 2 => (int)tile.Y, _ => 0 };
                    else if (type == typeof(string))
                        args[p] = seed.QualifiedItemId;
                    else if (type == typeof(Vector2))
                        args[p] = tile;
                    else if (typeof(Farmer).IsAssignableFrom(type))
                        args[p] = Game1.player;
                    else if (type == typeof(bool))
                        args[p] = false;
                    else if (parameters[p].HasDefaultValue)
                        args[p] = parameters[p].DefaultValue;
                    else { supported = false; break; }
                }
                if (!supported)
                    continue;
                try
                {
                    if (method.Invoke(dirt, args) is bool planted && planted)
                    {
                        seed.Stack--;
                        if (seed.Stack <= 0)
                            Game1.player.Items[i] = null;
                        return true;
                    }
                }
                catch (System.Reflection.TargetInvocationException)
                {
                    // Try another overload if the game exposes one.
                }
            }
        }
        return false;
    }

    private void UpdateHorseMovement()
    {
        if (horseTarget is null || horsePosition is null)
            return;
        Vector2 delta = horseTarget.Value - horsePosition.Value;
        float distance = delta.Length();
        float speed = Math.Max(0.1f, Game1.player.getMovementSpeed() * 2f);
        if (distance <= speed)
            horsePosition = horseTarget;
        else
            horsePosition += delta / distance * speed;
        if (++animationTicks >= 8)
        {
            animationTicks = 0;
            horseFrame = (horseFrame + 1) % 4;
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation != Game1.getFarm() || horsePosition is null)
            return;
        try
        {
            horseTexture ??= Helper.ModContent.Load<Texture2D>("assets/horse_plow.png");
            Vector2 screenPosition = Game1.GlobalToLocal(Game1.viewport, horsePosition.Value);
            e.SpriteBatch.Draw(horseTexture, screenPosition, new Rectangle(horseFrame * 64, 0, 64, 48), Color.White, 0f, new Vector2(32, 40), 1.5f, SpriteEffects.None, Math.Clamp((horsePosition.Value.Y + 16f) / 100000f, 0f, 0.99f));
        }
        catch (Exception ex)
        {
            Monitor.Log("Couldn't draw horse-plow sprite: " + ex, LogLevel.Warn);
        }
    }
    private static List<Point> GetPatchCenters()
    {
        var result = new List<Point>();
        for (int y = -WorkRadius; y <= WorkRadius; y += PatchSize)
        for (int x = -WorkRadius; x <= WorkRadius; x += PatchSize)
        {
            if (x * x + y * y <= WorkRadius * WorkRadius)
                result.Add(new Point(x, y));
        }
        return result;
    }
}
