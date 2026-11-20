using System;
using PadForge.Engine.Steering;

namespace PadForge.Common.Telemetry
{
    /// <summary>
    /// What each game's surface or weather field says of the road's grip
    /// (#588), in <see cref="RoadGrip"/>'s terms. Every list here is the
    /// game's own, from the reference named on it. 0 stands for no
    /// reading: a surface a game names that no figure fits reads as a dry
    /// road, and a value outside a list reads as nothing.
    /// </summary>
    internal static class SurfaceGrips
    {
        /// <summary>rFactor's surface types, the same in rFactor 1 and
        /// rFactor 2: 0 dry, 1 wet, 2 grass, 3 dirt, 4 gravel, 5 rumble
        /// strip, and in rFactor 2 6 special (rF2State.h mSurfaceType,
        /// rfSharedStruct.hpp rfSurfaceType). <paramref name="wetness"/>
        /// is the road's wetness from 0 to 1 where the game sends one,
        /// which rFactor 1 does not.</summary>
        internal static double RFactor(int surface, double wetness) => surface switch
        {
            0 or 5 or 6 => RoadGrip.FromWetness(wetness),
            1 => Math.Min(RoadGrip.Wet, RoadGrip.FromWetness(wetness)),
            2 => RoadGrip.Grass,
            3 => RoadGrip.Dirt,
            4 => RoadGrip.Gravel,
            _ => 0,
        };

        /// <summary>The F1 games' surface types (f1-24-udp README,
        /// "Surface types"): 0 tarmac, 1 rumble strip, 2 concrete, 3 rock,
        /// 4 gravel, 5 mud, 6 sand, 7 grass, 8 water, 9 cobblestone,
        /// 10 metal, 11 ridged.</summary>
        internal static double F1(int surface) => surface switch
        {
            0 or 1 or 2 or 3 or 9 or 10 or 11 => RoadGrip.Dry,
            4 => RoadGrip.Gravel,
            5 => RoadGrip.Mud,
            6 => RoadGrip.Sand,
            7 => RoadGrip.Grass,
            8 => RoadGrip.Wet,
            _ => 0,
        };

        /// <summary>RaceRoom's materials (r3e-api R3E.cs MtrlType):
        /// 1 tarmac, 2 grass, 3 dirt, 4 gravel, 5 rumble, 6 concrete.
        /// -1 is unavailable and 0 none.</summary>
        internal static double RaceRoom(int material) => material switch
        {
            1 or 5 or 6 => RoadGrip.Dry,
            2 => RoadGrip.Grass,
            3 => RoadGrip.Dirt,
            4 => RoadGrip.Gravel,
            _ => 0,
        };

        /// <summary>The Madness engine's terrain materials, which
        /// Project CARS 2 and Automobilista 2 share (vAzhure
        /// PC2SharedMemory.cs TerrainMaterials, in the header's order,
        /// which rust_ams2_sharedmem's SMS_MemMap.rs carries three names
        /// further): the roads, curbs, walls and drains read as a road,
        /// whose grip then follows the rain. <paramref name="rain"/> is
        /// the rain density from 0 to 1.</summary>
        internal static double Madness(int terrain, double rain) => terrain switch
        {
            // GRASSY_BERMS, GRASS, GRASSCRETE, LONG_GRASS, SLOPE_GRASS, ASTROTURF
            6 or 7 or 26 or 27 or 28 or 32 => RoadGrip.Grass,
            // GRAVEL, BUMPY_GRAVEL
            8 or 9 => RoadGrip.Gravel,
            // SAND, BUMPY_SAND, SAND_ROAD, ROUGH_SAND_MEDIUM, ROUGH_SAND_HEAVY
            15 or 16 or 30 or 42 or 43 => RoadGrip.Sand,
            // DIRT, BUMPY_DIRT, DIRT_ROAD, BUMPY_DIRT_ROAD, DIRT_BANK, DRY_VERGE, BAKED_CLAY
            17 or 18 or 19 or 20 or 22 or 24 or 31 => RoadGrip.Dirt,
            // SNOWHALF, SNOWFULL, SNOWWALLS
            33 or 34 or 44 => RoadGrip.Snow,
            // ICE_ROAD
            45 => RoadGrip.Ice,
            >= 0 and <= 50 => RoadGrip.FromWetness(rain),
            _ => 0,
        };

        /// <summary>The SCS trucks' wheel substance, a name from the
        /// game's own list (scs-sdk-plugin substances). The names are the
        /// game's data, so a name is read for the word in it: ice, snow,
        /// grass, gravel, sand, mud and dirt, and any other name is a
        /// road.</summary>
        internal static double Scs(ReadOnlySpan<byte> name)
        {
            int length = name.IndexOf((byte)0);
            if (length < 0) length = name.Length;
            if (length == 0) return 0;
            name = name.Slice(0, length);
            if (Has(name, "ice"u8)) return RoadGrip.Ice;
            if (Has(name, "snow"u8)) return RoadGrip.Snow;
            if (Has(name, "grass"u8)) return RoadGrip.Grass;
            if (Has(name, "gravel"u8)) return RoadGrip.Gravel;
            if (Has(name, "sand"u8)) return RoadGrip.Sand;
            if (Has(name, "mud"u8)) return RoadGrip.Mud;
            if (Has(name, "dirt"u8)) return RoadGrip.Dirt;
            return RoadGrip.Dry;
        }

        /// <summary>Whether <paramref name="word"/> is in an ASCII name,
        /// whatever its case.</summary>
        private static bool Has(ReadOnlySpan<byte> name, ReadOnlySpan<byte> word)
        {
            for (int at = 0; at + word.Length <= name.Length; at++)
            {
                int i = 0;
                for (; i < word.Length; i++)
                {
                    byte c = name[at + i];
                    if (c >= (byte)'A' && c <= (byte)'Z') c += 32;
                    if (c != word[i]) break;
                }
                if (i == word.Length) return true;
            }
            return false;
        }

        /// <summary>iRacing's track wetness (IRSDKSharper
        /// IRacingSdkEnum.TrackWetness): 1 dry through 7 extremely wet,
        /// seven even steps. 0 is unknown.</summary>
        internal static double IRacing(int wetness)
            => wetness >= 1 && wetness <= 7 ? RoadGrip.FromWetness((wetness - 1) / 6.0) : 0;

        /// <summary>Forza's puddle depth under a wheel, 0 to 1
        /// (forza-telemetry FMData.cs WheelInPuddle). Forza sends no
        /// other word on the road, so a wheel out of a puddle reads
        /// dry.</summary>
        internal static double Forza(float puddleDepth)
            => float.IsFinite(puddleDepth) ? RoadGrip.FromWetness(puddleDepth) : 0;

        /// <summary>Assetto Corsa's surface grip, which is already the
        /// track's grip against a fully rubbered one. A value outside 0
        /// to 1, or 0 itself, is no reading.</summary>
        internal static double AssettoCorsa(float surfaceGrip)
            => float.IsFinite(surfaceGrip) && surfaceGrip > 0f && surfaceGrip <= 1f ? surfaceGrip : 0;
    }
}
