using StardewModdingAPI;
using StardewModdingAPI.Events;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using System.Text.Json;

namespace AutoPlanter;
internal sealed class ModEntry : Mod
{
    const string Id = "AutoPlanter.HorsePlow", SeedsKey = "AutoPlanter/Seeds", ActiveKey = "AutoPlanter/Active", CursorKey = "AutoPlanter/Cursor", MovingKey = "AutoPlanter/Moving";
    const int Radius = 20;
    Texture2D? horseTexture;
    Vector2? horsePosition, horseTarget;
    int frame, anim;
    uint tick;
    Building? selected;
    Chest? seedChest;
    sealed record SeedData(string Id, int Count);

    public override void Entry(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
    }
    void OnAssetRequested(object? s, AssetRequestedEventArgs e)
    {
        string buildingTexture = Helper.ModContent.GetInternalAssetName("assets/horse_plow_building.png").Name;
        if (e.NameWithoutLocale.IsEquivalentTo(buildingTexture))
        {
            e.LoadFrom(() => ResizeBuildingTexture(Helper.ModContent.Load<Texture2D>("assets/horse_plow_building.png")), AssetLoadPriority.Exclusive);
            return;
        }
        if (!e.NameWithoutLocale.IsEquivalentTo("Data/Buildings")) return;
        e.Edit(a => a.AsDictionary<string, BuildingData>().Data[Id] = new BuildingData {
            Name = "Horse Plow", Description = "A horse-drawn automatic planter.", Texture = buildingTexture,
            SourceRect = new Rectangle(0, 0, 48, 64),
            Builder = "Robin", BuildCost = 50000, BuildDays = 1, Size = new Point(3,2),
            CollisionMap = "XXX\nXOX", Metadata = new Dictionary<string,string>{{"AutoPlanter","HorsePlow"}}
        });
    }
    static Texture2D ResizeBuildingTexture(Texture2D source)
    {
        // Match the Junimo Hut's compact 48x64 building sprite dimensions.
        const int width = 48, height = 64;
        Color[] input = new Color[source.Width * source.Height];
        source.GetData(input);
        Color[] output = new Color[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int sx = Math.Min(source.Width - 1, x * source.Width / width);
            int sy = Math.Min(source.Height - 1, y * source.Height / height);
            output[y * width + x] = input[sy * source.Width + sx];
        }
        Texture2D resized = new Texture2D(Game1.graphics.GraphicsDevice, width, height);
        resized.SetData(output);
        return resized;
    }

    void OnButtonPressed(object? s, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || Game1.activeClickableMenu is not null) return;
        Vector2 grabTile = e.Cursor.GrabTile;
        Vector2 cursorTile = e.Cursor.Tile;
        Vector2 facingTile = Game1.player.GetGrabTile();
        Farm farm = Game1.getFarm();
        selected = farm.buildings.FirstOrDefault(b => b.buildingType.Value == Id &&
            (IsInside(b, grabTile) || IsInside(b, cursorTile) || IsInside(b, facingTile)));
        if (selected is null) return;
        Helper.Input.Suppress(e.Button);
        Game1.currentLocation.createQuestionDialogue("Конюшня автопосадки", new[] {
            new Response("load","Загрузить семена"), new Response("cancel","Отмена"), new Response("start","Начать работу")
        }, OnAnswer);
    }
    static bool IsInside(Building b, Vector2 tile) =>
        tile.X >= b.tileX.Value && tile.X < b.tileX.Value + b.tilesWide.Value &&
        tile.Y >= b.tileY.Value && tile.Y < b.tileY.Value + b.tilesHigh.Value;

    void OnAnswer(Farmer who, string answer)
    {
        if (selected is null) return;
        if (answer == "load")
        {
            seedChest = new Chest(true);
            foreach (var d in ReadSeeds(selected))
                try { seedChest.Items.Add(ItemRegistry.Create(d.Id,d.Count)); } catch { }
            Game1.activeClickableMenu = new ItemGrabMenu(
                inventory: seedChest.Items,
                reverseGrab: false,
                showReceivingMenu: true,
                highlightFunction: item => item is StardewValley.Object obj && obj.Category == StardewValley.Object.SeedsCategory,
                behaviorOnItemSelectFunction: (_, _) => { },
                message: "Положи семена в верхние ячейки.",
                behaviorOnItemGrab: (_, _) => { },
                snapToBottom: false,
                canBeExitedWithKey: true,
                playRightClickSound: true,
                allowRightClick: true,
                showOrganizeButton: false,
                source: ItemGrabMenu.source_chest,
                sourceItem: seedChest,
                whichSpecialButton: -1,
                context: seedChest
            );
        }
        else if (answer == "start")
        {
            if (ReadSeeds(selected).Count == 0) { Game1.drawObjectDialogue("Сначала загрузи семена."); return; }
            selected.modData[ActiveKey] = "true";
            // Horse waits inside the hut, then walks out through the front.
            horsePosition = new Vector2(
                (selected.tileX.Value + selected.tilesWide.Value / 2f) * Game1.tileSize,
                (selected.tileY.Value + selected.tilesHigh.Value - 0.25f) * Game1.tileSize
            );
            horseTarget = new Vector2(
                horsePosition.Value.X,
                (selected.tileY.Value + selected.tilesHigh.Value + 0.5f) * Game1.tileSize
            );
            selected.modData[MovingKey] = "exit";
            selected.modData[CursorKey] = "0";
            frame = 0;
            anim = 0;
            Game1.drawObjectDialogue("Лошадь с плугом начинает работу.");
        }
    }
    void OnMenuChanged(object? s, MenuChangedEventArgs e)
    {
        if (seedChest is null || e.NewMenu is ItemGrabMenu) return;
        if (selected is not null)
        {
            var data = seedChest.Items
                .Where(i => i is StardewValley.Object o && o.Category == StardewValley.Object.SeedsCategory && i.Stack > 0)
                .Select(i => new SeedData(i.QualifiedItemId, i.Stack))
                .ToList();
            selected.modData[SeedsKey] = JsonSerializer.Serialize(data);
        }
        seedChest = null;
    }
    static List<SeedData> ReadSeeds(Building b)
    {
        if (!b.modData.TryGetValue(SeedsKey,out string? json)) return new();
        try { return JsonSerializer.Deserialize<List<SeedData>>(json) ?? new(); } catch { return new(); }
    }
    void OnUpdateTicked(object? s, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer) return;

        Farm farm = Game1.getFarm();
        Building? activeBuilding = farm.buildings.FirstOrDefault(b =>
            b.buildingType.Value == Id &&
            b.modData.TryGetValue(ActiveKey, out string? active) && active == "true");

        if (activeBuilding is null)
            return;

        // Initialize/recover the horse at the inside of the hut, then send it to the doorway.
        if (horsePosition is null)
        {
            horsePosition = new Vector2(
                (activeBuilding.tileX.Value + activeBuilding.tilesWide.Value / 2f) * Game1.tileSize,
                (activeBuilding.tileY.Value + activeBuilding.tilesHigh.Value - 0.5f) * Game1.tileSize
            );
            horseTarget = new Vector2(
                horsePosition.Value.X,
                (activeBuilding.tileY.Value + activeBuilding.tilesHigh.Value + 0.75f) * Game1.tileSize
            );
            activeBuilding.modData[MovingKey] = "exit";
        }

        // Move continuously every game tick. Don't replace the target until arrival.
        if (horsePosition is not null && horseTarget is not null)
        {
            Vector2 delta = horseTarget.Value - horsePosition.Value;
            float distance = delta.Length();
            float speed = Math.Max(4f, Game1.player.getMovementSpeed() * 0.5f);
            if (distance <= speed)
                horsePosition = horseTarget.Value;
            else
                horsePosition += delta / distance * speed;

            if (activeBuilding.modData.TryGetValue(MovingKey, out string? phase) &&
                phase == "exit" &&
                Vector2.DistanceSquared(horsePosition.Value, horseTarget.Value) <= 4f)
            {
                activeBuilding.modData[MovingKey] = "idle";
            }

            if (++anim >= 6) { anim = 0; frame = (frame + 1) % 4; }
        }

        // Run the work state machine frequently; travel itself is updated above every tick.
        if (e.Ticks % 10 == 0)
            Work(farm, activeBuilding);
    }

    void Work(Farm farm, Building b)
    {
        var seeds = new List<Item>();
        foreach (var data in ReadSeeds(b))
            try { seeds.Add(ItemRegistry.Create(data.Id, data.Count)); } catch { }

        if (seeds.Count == 0)
        {
            b.modData[ActiveKey] = "false";
            b.modData[MovingKey] = "idle";
            horsePosition = null;
            horseTarget = null;
            return;
        }

        string phase = b.modData.TryGetValue(MovingKey, out string? currentPhase) ? currentPhase : "exit";
        if (phase == "exit")
            return;

        // Visit 3x3 planting patches across a square extending 20 tiles from the hut.
        var patches = new List<Point>();
        for (int y = -18; y <= 18; y += 3)
            for (int x = -18; x <= 18; x += 3)
                patches.Add(new Point(x, y));

        int cursor = 0;
        if (b.modData.TryGetValue(CursorKey, out string? raw))
            int.TryParse(raw, out cursor);
        cursor = Math.Clamp(cursor, 0, patches.Count - 1);

        if (phase != "travel")
        {
            Point patch = patches[cursor];
            int targetX = b.tileX.Value + b.tilesWide.Value / 2 + patch.X;
            int targetY = b.tileY.Value + b.tilesHigh.Value / 2 + patch.Y;
            horseTarget = new Vector2((targetX + 0.5f) * Game1.tileSize, (targetY + 0.5f) * Game1.tileSize);
            b.modData[MovingKey] = "travel";
            return;
        }

        if (horsePosition is null || horseTarget is null ||
            Vector2.DistanceSquared(horsePosition.Value, horseTarget.Value) > 20f * 20f)
            return;

        Point center = patches[cursor];
        int centerX = b.tileX.Value + b.tilesWide.Value / 2 + center.X;
        int centerY = b.tileY.Value + b.tilesHigh.Value / 2 + center.Y;

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            int relativeX = center.X + dx;
            int relativeY = center.Y + dy;
            if (Math.Abs(relativeX) > Radius || Math.Abs(relativeY) > Radius)
                continue;

            Vector2 tile = new(centerX + dx, centerY + dy);
            if (!farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature))
            {
                var soil = new HoeDirt(0);
                farm.terrainFeatures.Add(tile, soil);
                feature = soil;
            }

            if (feature is HoeDirt dirt && dirt.crop is null)
                Plant(dirt, tile, seeds);
        }

        b.modData[SeedsKey] = JsonSerializer.Serialize(
            seeds.Where(item => item.Stack > 0)
                .Select(item => new SeedData(item.QualifiedItemId, item.Stack))
                .ToList()
        );

        b.modData[CursorKey] = ((cursor + 1) % patches.Count).ToString();
        b.modData[MovingKey] = "idle";
    }
    static bool Plant(HoeDirt dirt, Vector2 tile, List<Item> seeds)
    {
        // Stardew 1.6 planting API: item ID, farmer, fertilizer flag.
        foreach (Item item in seeds)
        {
            if (item is not StardewValley.Object seed || seed.Category != StardewValley.Object.SeedsCategory || seed.Stack <= 0)
                continue;

            try
            {
                if (dirt.plant(seed.QualifiedItemId, Game1.player, false))
                {
                    seed.Stack--;
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Keep the mod alive if the game rejects a tile or seed.
                ModEntry.LogPlantError(ex);
            }
        }
        return false;
    }

    private static void LogPlantError(Exception ex)
    {
        // Detailed error is available in the SMAPI log.
        System.Diagnostics.Debug.WriteLine(ex);
    }
    void OnRenderedWorld(object? s, RenderedWorldEventArgs e)
    {
        if(!Context.IsWorldReady || Game1.currentLocation!=Game1.getFarm() || horsePosition is null) return;
        try { horseTexture ??= Helper.ModContent.Load<Texture2D>("assets/horse_plow.png");
            Vector2 pos=Game1.GlobalToLocal(Game1.viewport,horsePosition.Value);
            e.SpriteBatch.Draw(horseTexture,pos,new Rectangle(frame*64,0,64,48),Color.White,0f,new Vector2(32,40),1.5f,SpriteEffects.None,Math.Clamp((horsePosition.Value.Y+16f)/100000f,0f,.99f));
        } catch(Exception ex) { Monitor.Log("Horse sprite error: "+ex.Message,LogLevel.Warn); }
    }
}