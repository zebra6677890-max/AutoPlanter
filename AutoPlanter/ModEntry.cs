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
    const string Id = "AutoPlanter.HorsePlow", SeedsKey = "AutoPlanter/Seeds", ActiveKey = "AutoPlanter/Active", CursorKey = "AutoPlanter/Cursor";
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
            SourceRect = new Rectangle(0, 0, 112, 80),
            Builder = "Robin", BuildCost = 50000, BuildDays = 1, Size = new Point(3,2),
            CollisionMap = "XXX\nXOX", Metadata = new Dictionary<string,string>{{"AutoPlanter","HorsePlow"}}
        });
    }
    static Texture2D ResizeBuildingTexture(Texture2D source)
    {
        const int width = 112, height = 80;
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

        // Android touch controls can report a different button than desktop action-click.
        // Check every input press against all available tile targets, but only open
        // the menu when the tap/target is actually on this building.
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
            Game1.activeClickableMenu = new ItemGrabMenu(seedChest.Items, false);
        }
        else if (answer == "start")
        {
            if (ReadSeeds(selected).Count == 0) { Game1.drawObjectDialogue("Сначала загрузи семена."); return; }
            selected.modData[ActiveKey] = "true";
            Game1.drawObjectDialogue("Лошадь с плугом начинает работу.");
        }
    }
    void OnMenuChanged(object? s, MenuChangedEventArgs e)
    {
        // Save the temporary seed container whenever its item menu closes.
        // On Android, menu transitions aren't always reported as ItemGrabMenu.
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
        if (horsePosition is not null && horseTarget is not null)
        {
            Vector2 d=horseTarget.Value-horsePosition.Value; float len=d.Length(), speed=Math.Max(.1f,Game1.player.getMovementSpeed()*2f);
            horsePosition = len <= speed ? horseTarget : horsePosition + d/len*speed;
            if (++anim >= 8) { anim=0; frame=(frame+1)%4; }
        }
        if (e.Ticks-tick<60) return; tick=e.Ticks;
        Farm farm=Game1.getFarm();
        foreach (Building b in farm.buildings)
            if (b.buildingType.Value==Id && b.modData.TryGetValue(ActiveKey,out string? active) && active=="true") Work(farm,b);
    }
    void Work(Farm farm, Building b)
    {
        var stored=ReadSeeds(b); var seeds=new List<Item>();
        foreach(var d in stored) try { seeds.Add(ItemRegistry.Create(d.Id,d.Count)); } catch {}
        if (seeds.Count==0) { b.modData[ActiveKey]="false"; return; }
        int cursor=0; if(b.modData.TryGetValue(CursorKey,out string? c)) int.TryParse(c,out cursor);
        var centers=new List<Point>();
        for(int y=-Radius;y<=Radius;y+=3) for(int x=-Radius;x<=Radius;x+=3) if(x*x+y*y<=Radius*Radius) centers.Add(new(x,y));
        for(int n=0;n<centers.Count;n++)
        {
            Point p=centers[(cursor+n)%centers.Count]; int cx=b.tileX.Value+b.tilesWide.Value/2+p.X, cy=b.tileY.Value+b.tilesHigh.Value/2+p.Y;
            horseTarget=new Vector2((cx+.5f)*Game1.tileSize,(cy+.5f)*Game1.tileSize);
            horsePosition ??= new Vector2((b.tileX.Value+1.5f)*Game1.tileSize,(b.tileY.Value+2.5f)*Game1.tileSize);
            int planted=0;
            for(int dx=-1;dx<=1;dx++) for(int dy=-1;dy<=1;dy++)
            {
                Vector2 tile=new(cx+dx,cy+dy), origin=new(b.tileX.Value+b.tilesWide.Value/2f,b.tileY.Value+b.tilesHigh.Value/2f);
                if(Vector2.DistanceSquared(tile,origin)>Radius*Radius) continue;
                if(!farm.terrainFeatures.TryGetValue(tile,out TerrainFeature? f)) { var dirt=new HoeDirt(0); farm.terrainFeatures.Add(tile,dirt); f=dirt; }
                if(f is HoeDirt soil && soil.crop is null && Plant(soil,tile,seeds)) planted++;
            }
            b.modData[CursorKey]=((cursor+n+1)%centers.Count).ToString();
            if(planted>0) { b.modData[SeedsKey]=JsonSerializer.Serialize(seeds.Where(i=>i.Stack>0).Select(i=>new SeedData(i.QualifiedItemId,i.Stack))); return; }
        }
    }
    static bool Plant(HoeDirt dirt, Vector2 tile, List<Item> seeds)
    {
        var methods=typeof(HoeDirt).GetMethods(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).Where(m=>m.Name.Equals("plant",StringComparison.OrdinalIgnoreCase));
        foreach(Item item in seeds)
        {
            if(item is not StardewValley.Object seed || seed.Category!=StardewValley.Object.SeedsCategory || seed.Stack<=0) continue;
            foreach(var m in methods)
            {
                var ps=m.GetParameters(); var args=new object?[ps.Length]; int ix=0; bool ok=true;
                for(int j=0;j<ps.Length;j++) { Type t=ps[j].ParameterType;
                    if(t==typeof(int)) args[j]=ix++ switch {0=>seed.ParentSheetIndex,1=>(int)tile.X,2=>(int)tile.Y,_=>0};
                    else if(t==typeof(string)) args[j]=seed.QualifiedItemId; else if(t==typeof(Vector2)) args[j]=tile;
                    else if(typeof(Farmer).IsAssignableFrom(t)) args[j]=Game1.player; else if(t==typeof(bool)) args[j]=false;
                    else if(ps[j].HasDefaultValue) args[j]=ps[j].DefaultValue; else {ok=false;break;}
                }
                if(!ok) continue;
                try { object? result=m.Invoke(dirt,args); if(m.ReturnType==typeof(void)||(result is bool yes && yes)){seed.Stack--;return true;} } catch {}
            }
        }
        return false;
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