using System;

namespace MuninMap.Tiles
{
    // Colours for the rendered map. Kept in one place so the look can be tuned
    // without touching the renderer. All colours are plain bytes: the renderer
    // never allocates Unity Color structs on worker threads.
    internal static class Palette
    {
        public struct Rgb
        {
            public byte r, g, b;
            public Rgb(int r, int g, int b) { this.r = (byte)r; this.g = (byte)g; this.b = (byte)b; }
            public static Rgb Lerp(Rgb a, Rgb b, float t)
            {
                if (t <= 0) return a; if (t >= 1) return b;
                return new Rgb((int)(a.r + (b.r - a.r) * t), (int)(a.g + (b.g - a.g) * t), (int)(a.b + (b.b - a.b) * t));
            }
            public Rgb Scale(float f)
            {
                return new Rgb(Clamp(r * f), Clamp(g * f), Clamp(b * f));
            }
            private static int Clamp(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;
        }

        // Heightmap.Biome values (the enum is a bit field in the game).
        public const int B_NONE = 0, B_MEADOWS = 1, B_SWAMP = 2, B_MOUNTAIN = 4, B_BLACKFOREST = 8,
                         B_PLAINS = 16, B_ASHLANDS = 32, B_DEEPNORTH = 64, B_OCEAN = 256, B_MISTLANDS = 512;

        public static readonly Rgb Meadows     = new Rgb(112, 146, 72);
        public static readonly Rgb BlackForest = new Rgb(64, 84, 48);
        public static readonly Rgb Swamp       = new Rgb(84, 82, 56);
        public static readonly Rgb MountainRock= new Rgb(138, 136, 132);
        public static readonly Rgb Snow        = new Rgb(232, 236, 240);
        public static readonly Rgb Plains      = new Rgb(188, 172, 98);
        public static readonly Rgb Mistlands   = new Rgb(96, 92, 108);
        public static readonly Rgb Ashlands    = new Rgb(104, 52, 42);
        public static readonly Rgb AshlandsLava= new Rgb(200, 90, 30);
        public static readonly Rgb LavaCore    = new Rgb(255, 150, 48);
        public static readonly Rgb AshSeaShallow = new Rgb(92, 74, 66);
        public static readonly Rgb AshSeaDeep  = new Rgb(46, 38, 40);
        public static readonly Rgb DeepNorth   = new Rgb(220, 228, 236);
        public static readonly Rgb Sand        = new Rgb(196, 184, 140);
        public static readonly Rgb Shore       = new Rgb(150, 140, 110);
        public static readonly Rgb WaterShallow= new Rgb(52, 116, 148);
        public static readonly Rgb WaterDeep   = new Rgb(20, 44, 82);
        public static readonly Rgb Unknown     = new Rgb(80, 80, 80);

        public static readonly Rgb PaintDirt      = new Rgb(112, 86, 60);
        public static readonly Rgb PaintCultivated= new Rgb(92, 62, 40);
        public static readonly Rgb PaintPaved     = new Rgb(122, 120, 114);

        public static Rgb Biome(int biome)
        {
            switch (biome)
            {
                case B_MEADOWS: return Meadows;
                case B_BLACKFOREST: return BlackForest;
                case B_SWAMP: return Swamp;
                case B_MOUNTAIN: return MountainRock;
                case B_PLAINS: return Plains;
                case B_MISTLANDS: return Mistlands;
                case B_ASHLANDS: return Ashlands;
                case B_DEEPNORTH: return DeepNorth;
                case B_OCEAN: return WaterDeep;
                default: return Unknown;
            }
        }

        public static string BiomeName(int biome)
        {
            switch (biome)
            {
                case B_MEADOWS: return "Meadows";
                case B_BLACKFOREST: return "Black Forest";
                case B_SWAMP: return "Swamp";
                case B_MOUNTAIN: return "Mountain";
                case B_PLAINS: return "Plains";
                case B_MISTLANDS: return "Mistlands";
                case B_ASHLANDS: return "Ashlands";
                case B_DEEPNORTH: return "Deep North";
                case B_OCEAN: return "Ocean";
                default: return "Unknown";
            }
        }

        // Vegetation classes baked into the tiles and instanced in 3D.
        public enum Veg : byte { None = 0, Deciduous = 1, Conifer = 2, SwampTree = 3, MistTree = 4, DeadTree = 5, Bush = 6, Rock = 7, Ore = 8, Stump = 9, Berry = 10, AshTree = 11,
                           Oak = 12, Birch = 13, Pine = 14, Raspberry = 15, Blueberry = 16, Cloudberry = 17, BirchAutumn = 18,
                           // plants you can pick and small growth (the map's "Mushrooms & plants" switch)
                           Mushroom = 19, MushroomYellow = 20, Magecap = 21, JotunPuffs = 22, SmokePuff = 23, Thistle = 24,
                           Dandelion = 25, Fiddlehead = 26, BarleyWild = 27, FlaxWild = 28, Lingonberry = 29, AshVine = 30, AshFern = 31,
                           // kinds of rock and ore, so the map can show or hide each (drawn like Rock and Ore: Base)
                           Cliff = 32, GiantBones = 33, Copper = 34, Tin = 35, Silver = 36, Obsidian = 37, MuddyScrap = 38 }

        // the kind a rock or ore kind is drawn and counted as
        public static Veg Base(Veg v) => v == Veg.Cliff || v == Veg.GiantBones ? Veg.Rock : v >= Veg.Copper && v <= Veg.MuddyScrap ? Veg.Ore : v;

        // bushes, berry bushes and stumps: under the canopy, not counted as trees
        public static bool IsLowPlant(Veg v) => v == Veg.Bush || v == Veg.Berry || v == Veg.Stump
            || v == Veg.Raspberry || v == Veg.Blueberry || v == Veg.Cloudberry || IsPickPlant(v);
        // mushrooms and small plants you can pick (and Ashlands vines and ferns)
        public static bool IsPickPlant(Veg v) => v >= Veg.Mushroom && v <= Veg.AshFern;

        public static Rgb VegColor(Veg v)
        {
            switch (v)
            {
                case Veg.Cliff:      return new Rgb(108, 108, 104);
                case Veg.GiantBones: return new Rgb(196, 190, 170);
                case Veg.Copper:     return new Rgb(176, 104, 58);
                case Veg.Tin:        return new Rgb(150, 156, 160);
                case Veg.Silver:     return new Rgb(200, 210, 222);
                case Veg.Obsidian:   return new Rgb(40, 34, 48);
                case Veg.MuddyScrap: return new Rgb(110, 84, 60);
                case Veg.Deciduous: return new Rgb(86, 138, 58);    // beech
                case Veg.Oak:       return new Rgb(70, 112, 40);
                case Veg.Birch:     return new Rgb(138, 160, 70);
                case Veg.BirchAutumn: return new Rgb(204, 150, 58);
                case Veg.Mushroom:       return new Rgb(214, 66, 58);
                case Veg.MushroomYellow: return new Rgb(232, 196, 70);
                case Veg.Magecap:        return new Rgb(176, 106, 214);
                case Veg.JotunPuffs:     return new Rgb(186, 224, 242);
                case Veg.SmokePuff:      return new Rgb(150, 140, 140);
                case Veg.Thistle:        return new Rgb(112, 110, 214);
                case Veg.Dandelion:      return new Rgb(242, 222, 90);
                case Veg.Fiddlehead:     return new Rgb(124, 186, 78);
                case Veg.BarleyWild:     return new Rgb(214, 190, 116);
                case Veg.FlaxWild:       return new Rgb(196, 214, 164);
                case Veg.Lingonberry:    return new Rgb(176, 34, 62);
                case Veg.AshVine:        return new Rgb(142, 58, 122);
                case Veg.AshFern:        return new Rgb(112, 60, 44);
                case Veg.Conifer:   return new Rgb(44, 82, 52);     // fir
                case Veg.Pine:      return new Rgb(72, 100, 48);
                case Veg.Raspberry: return new Rgb(196, 58, 74);
                case Veg.Blueberry: return new Rgb(78, 100, 204);
                case Veg.Cloudberry: return new Rgb(228, 168, 64);
                case Veg.SwampTree: return new Rgb(56, 62, 40);
                case Veg.MistTree:  return new Rgb(74, 104, 112);
                case Veg.DeadTree:  return new Rgb(70, 56, 46);
                case Veg.AshTree:   return new Rgb(60, 40, 34);
                case Veg.Bush:      return new Rgb(70, 110, 50);
                case Veg.Berry:     return new Rgb(90, 120, 60);
                case Veg.Rock:      return new Rgb(118, 118, 112);
                case Veg.Ore:       return new Rgb(134, 104, 74);
                case Veg.Stump:     return new Rgb(96, 70, 44);
                default:            return Unknown;
            }
        }

        // Canopy / footprint radius in metres for a size class 1.0.
        public static float VegRadius(Veg v)
        {
            switch (Base(v))
            {
                case Veg.Deciduous: case Veg.Oak: case Veg.Birch: case Veg.BirchAutumn: return 4.5f;
                case Veg.Conifer:   case Veg.Pine: return 3.0f;
                case Veg.Raspberry: case Veg.Blueberry: case Veg.Cloudberry: case Veg.Lingonberry: return 1.0f;
                case Veg.AshVine: case Veg.AshFern: return 1.1f;
                case Veg.Mushroom: case Veg.MushroomYellow: case Veg.Magecap: case Veg.JotunPuffs: case Veg.SmokePuff:
                case Veg.Thistle: case Veg.Dandelion: case Veg.Fiddlehead: case Veg.BarleyWild: case Veg.FlaxWild: return 0.6f;
                case Veg.SwampTree: return 3.0f;
                case Veg.MistTree:  return 4.0f;
                case Veg.DeadTree:  return 2.5f;
                case Veg.AshTree:   return 3.0f;
                case Veg.Bush:      return 1.3f;
                case Veg.Berry:     return 1.0f;
                case Veg.Rock:      return 2.5f;
                case Veg.Ore:       return 2.5f;
                case Veg.Stump:     return 0.7f;
                default:            return 1f;
            }
        }

        // Approximate height in metres for the 3D view, size class 1.0.
        public static float VegHeight(Veg v)
        {
            switch (Base(v))
            {
                case Veg.Deciduous: case Veg.Oak: case Veg.Birch: case Veg.BirchAutumn: return 12f;
                case Veg.Conifer:   case Veg.Pine: return 16f;
                case Veg.Raspberry: case Veg.Blueberry: case Veg.Cloudberry: return 1.0f;
                case Veg.SwampTree: return 10f;
                case Veg.MistTree:  return 14f;
                case Veg.DeadTree:  return 7f;
                case Veg.AshTree:   return 9f;
                case Veg.Bush:      return 1.5f;
                case Veg.Berry:     return 1.0f;
                case Veg.Rock:      return 3f;
                case Veg.Ore:       return 3f;
                case Veg.Stump:     return 0.6f;
                default:            return 1f;
            }
        }

        // Build materials for structures (2D fill and 3D box colour).
        public enum Material : byte { Wood = 0, CoreWood = 1, DarkWood = 2, Stone = 3, BlackMarble = 4, Iron = 5, Thatch = 6, Fire = 7, Portal = 8, Crystal = 9, Grausten = 10, Flametal = 11, Misc = 12, Cloth = 13, Ashwood = 14 }

        public static Rgb MaterialColor(Material m)
        {
            switch (m)
            {
                case Material.Wood:        return new Rgb(160, 116, 70);
                case Material.CoreWood:    return new Rgb(132, 92, 56);
                case Material.DarkWood:    return new Rgb(92, 66, 46);
                case Material.Ashwood:     return new Rgb(112, 74, 52);
                case Material.Stone:       return new Rgb(154, 152, 146);
                case Material.Grausten:    return new Rgb(120, 128, 136);
                case Material.BlackMarble: return new Rgb(66, 66, 82);
                case Material.Iron:        return new Rgb(118, 126, 140);
                case Material.Flametal:    return new Rgb(170, 100, 60);
                case Material.Thatch:      return new Rgb(198, 162, 88);
                case Material.Fire:        return new Rgb(230, 130, 50);
                case Material.Portal:      return new Rgb(90, 200, 210);
                case Material.Crystal:     return new Rgb(170, 210, 230);
                case Material.Cloth:       return new Rgb(180, 170, 150);
                default:                   return new Rgb(150, 130, 110);
            }
        }

        public static string MaterialName(Material m) => m.ToString();
    }
}
