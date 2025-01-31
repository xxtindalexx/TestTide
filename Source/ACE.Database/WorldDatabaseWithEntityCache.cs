using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common;
using ACE.Database.Adapter;
using ACE.Database.Models.World;
using ACE.Database.Extensions;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Model;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    public class WorldDatabaseWithEntityCache : WorldDatabase
    {
        // =====================================
        // Weenie
        // =====================================

        private readonly ConcurrentDictionary<uint /* WCID */, ACE.Entity.Models.Weenie> weenieCache = new ConcurrentDictionary<uint /* WCID */, ACE.Entity.Models.Weenie>();

        private readonly ConcurrentDictionary<string /* Class Name */, uint /* WCID */> weenieClassNameToClassIdCache = new ConcurrentDictionary<string, uint>();

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest<para />
        /// This will also update the weenie cache.
        /// </summary>
        public override Weenie GetWeenie(WorldDbContext context, uint weenieClassId)
        {
            var weenie = base.GetWeenie(context, weenieClassId);

            // If the weenie doesn't exist in the cache, we'll add it.
            if (weenie != null)
            {
                weenieCache[weenieClassId] = WeenieConverter.ConvertToEntityWeenie(weenie);
                weenieClassNameToClassIdCache[weenie.ClassName.ToLower()] = weenie.ClassId;
            }
            else
                weenieCache[weenieClassId] = null;

            return weenie;
        }

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest<para />
        /// This will also update the weenie cache.
        /// </summary>
        public override List<Weenie> GetAllWeenies()
        {
            var weenies = base.GetAllWeenies();

            // Add the weenies to the cache
            foreach (var weenie in weenies)
            {
                weenieCache[weenie.ClassId] = WeenieConverter.ConvertToEntityWeenie(weenie);
                weenieClassNameToClassIdCache[weenie.ClassName.ToLower()] = weenie.ClassId;
            }

            return weenies;
        }


        /// <summary>
        /// This will make sure every weenie in the database has been read and cached.<para />
        /// This function may take 10+ seconds to complete.
        /// </summary>
        public void CacheAllWeenies()
        {
            GetAllWeenies();

            PopulateWeenieSpecificCaches();
        }

        /// <summary>
        /// Returns the number of weenies currently cached.
        /// </summary>
        public int GetWeenieCacheCount()
        {
            return weenieCache.Count(r => r.Value != null);
        }

        public void ClearWeenieCache()
        {
            weenieCache.Clear();
            weenieClassNameToClassIdCache.Clear();

            weenieSpecificCachesPopulated = false;
        }

        /// <summary>
        /// Weenies will have all their collections populated except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public ACE.Entity.Models.Weenie GetCachedWeenie(uint weenieClassId)
        {
            if (weenieCache.TryGetValue(weenieClassId, out var value))
                return value;

            GetWeenie(weenieClassId); // This will add the result into the caches

            weenieCache.TryGetValue(weenieClassId, out value);

            return value;
        }

        /// <summary>
        /// Weenies will have all their collections populated except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public ACE.Entity.Models.Weenie GetCachedWeenie(string weenieClassName)
        {
            if (weenieClassNameToClassIdCache.TryGetValue(weenieClassName.ToLower(), out var value))
                return GetCachedWeenie(value); // This will add the result into the caches

            GetWeenie(weenieClassName); // This will add the result into the caches

            weenieClassNameToClassIdCache.TryGetValue(weenieClassName.ToLower(), out value);

            return GetCachedWeenie(value); // This will add the result into the caches
        }

        public bool ClearCachedWeenie(uint weenieClassId)
        {
            return weenieCache.TryRemove(weenieClassId, out _);
        }

        public uint GetNextAvailableWeenieClassID(uint start)
        {
            uint next = start;
            while (weenieCache.ContainsKey(next))
            {
                next++;
            }
            return next;
        }


        private bool weenieSpecificCachesPopulated;

        private readonly ConcurrentDictionary<WeenieType, List<ACE.Entity.Models.Weenie>> weenieCacheByType = new ConcurrentDictionary<WeenieType, List<ACE.Entity.Models.Weenie>>();

        private readonly Dictionary<uint /* Spell ID */, ACE.Entity.Models.Weenie> scrollsBySpellID = new Dictionary<uint, ACE.Entity.Models.Weenie>();

        private void PopulateWeenieSpecificCaches()
        {
            // populate weenieCacheByType
            foreach (var weenie in weenieCache.Values)
            {
                if (weenie == null)
                    continue;

                if (!weenieCacheByType.TryGetValue(weenie.WeenieType, out var weenies))
                {
                    weenies = new List<ACE.Entity.Models.Weenie>();
                    weenieCacheByType[weenie.WeenieType] = weenies;
                }

                if (!weenies.Contains(weenie))
                    weenies.Add(weenie);
            }

            // populate scrollsBySpellID
            foreach (var weenie in weenieCache.Values)
            {
                if (weenie == null)
                    continue;

                if (weenie.WeenieType == WeenieType.Scroll)
                {
                    if (weenie.PropertiesDID.TryGetValue(PropertyDataId.Spell, out var value))
                        scrollsBySpellID[value] = weenie;
                }
            }

            weenieSpecificCachesPopulated = true;
        }

        public uint GetRandomNPCWeenieIDFromWhitelist(uint weenieToSkip = 0)
        {
            var list = ACE.Common.NPCList.GetNPCs();
            var index = ThreadSafeRandom.Next(0, list.Count - 1);
            while (index == weenieToSkip)
            {
                index = ThreadSafeRandom.Next(0, list.Count - 1); //reroll
            }
            var weenie = list[index];
            return weenie;
        }

        public ACE.Entity.Models.Weenie GetRandomNPCWeenie()
        {
            var w = GetRandomWeeniesOfType(10, 50);
            if (w == null) return null;

            List<ACE.Entity.Models.Weenie> npcs = new List<ACE.Entity.Models.Weenie>();
            foreach (var x in w)
            {
                bool attackable = true; int targetTactic = 99; bool looksLikeObj = true; bool socMember = true; bool inDung = false;
                if (x.PropertiesBool == null || !x.PropertiesBool.TryGetValue(PropertyBool.Attackable, out attackable))
                {
                    continue;
                }
                if (x.PropertiesInt == null || !x.PropertiesInt.TryGetValue(PropertyInt.TargetingTactic, out targetTactic))
                {
                    targetTactic = 0;
                }
                if (x.PropertiesBool == null || !x.PropertiesBool.TryGetValue(PropertyBool.NpcLooksLikeObject, out looksLikeObj))
                {
                    looksLikeObj = false;
                }
                if (x.PropertiesInt != null &&
                        (!x.PropertiesInt.ContainsKey(PropertyInt.SocietyRankRadblo) &&
                        !x.PropertiesInt.ContainsKey(PropertyInt.SocietyRankEldweb) &&
                        !x.PropertiesInt.ContainsKey(PropertyInt.SocietyRankCelhan)))
                {
                    socMember = false;
                }

                // MONSTEROUSLY NON PERFORMANT
                //List<LandblockInstance> locations = LandblockInstance.GetLandblockByStaticWeenieId(x.WeenieClassId);
                //foreach (var item in locations)
                //{
                //    if (item.Landblock > 192)
                //    {
                //        //dungeon - skip
                //        inDung = true; break;
                //    }
                //}

                //also exclude Town Criers, Emissary of Asheron, and Society Members(?)
                if (!attackable && targetTactic == 0 && !looksLikeObj && !x.ClassName.Contains("towncrier") && !x.ClassName.Contains("emissaryofasheron") && !socMember && !inDung)
                {
                    npcs.Add(x);
                }
                
            }
            var index = ThreadSafeRandom.Next(0, npcs.Count - 1);

            var weenie = GetCachedWeenie(npcs[index].WeenieClassId);

            return weenie;
        }

        public ACE.Entity.Models.Weenie GetRandomEquippableItem()
        {
            List<ACE.Entity.Models.Weenie> weenies = new List<ACE.Entity.Models.Weenie>();

            var foodW = GetRandomWeeniesOfType(18, 12);
            var missleW = GetRandomWeeniesOfType(3, 8);
            var meleeW = GetRandomWeeniesOfType(6, 12);
            var clothingW = GetRandomWeeniesOfType(2, 12);

            weenies.AddRange(foodW);
            weenies.AddRange(missleW);
            weenies.AddRange(meleeW);
            weenies.AddRange(clothingW);

            var index = ThreadSafeRandom.Next(0, weenies.Count - 1);

            var weenie = GetCachedWeenie(weenies[index].WeenieClassId);

            return weenie;

        }

        public List<ACE.Entity.Models.Weenie> GetRandomWeeniesOfType(int weenieTypeId, int count)
        {
            if (!weenieCacheByType.TryGetValue((WeenieType)weenieTypeId, out var weenies))
            {
                if (!weenieSpecificCachesPopulated)
                {
                    using (var context = new WorldDbContext())
                    {
                        var results = context.Weenie
                            .AsNoTracking()
                            .Where(r => r.Type == weenieTypeId)
                            .ToList();

                        weenies = new List<ACE.Entity.Models.Weenie>();

                        if (results.Count == 0)
                            return weenies;

                        for (int i = 0; i < count; i++) //todo: convert to parallel.for
                        {
                            var index = ThreadSafeRandom.Next(0, results.Count - 1);

                            var weenie = GetCachedWeenie(results[index].ClassId);

                            weenies.Add(weenie);
                        }

                        return weenies;
                    }
                }

                weenies = new List<ACE.Entity.Models.Weenie>();
                weenieCacheByType[(WeenieType)weenieTypeId] = weenies;
            }

            if (weenies.Count == 0)
                return new List<ACE.Entity.Models.Weenie>();

            {
                var results = new List<ACE.Entity.Models.Weenie>();

                for (int i = 0; i < count; i++)
                {
                    var index = ThreadSafeRandom.Next(0, weenies.Count - 1);

                    var weenie = GetCachedWeenie(weenies[index].WeenieClassId);

                    results.Add(weenie);
                }

                return results;
            }
        }

        public ACE.Entity.Models.Weenie GetScrollWeenie(uint spellID)
        {
            if (!scrollsBySpellID.TryGetValue(spellID, out var weenie))
            {
                if (!weenieSpecificCachesPopulated)
                {
                    using (var context = new WorldDbContext())
                    {
                        var query = from weenieRecord in context.Weenie
                                    join did in context.WeeniePropertiesDID on weenieRecord.ClassId equals did.ObjectId
                                    where weenieRecord.Type == (int)WeenieType.Scroll && did.Type == (ushort)PropertyDataId.Spell && did.Value == spellID
                                    select weenieRecord;

                        var result = query.FirstOrDefault();

                        if (result == null) return null;

                        weenie = WeenieConverter.ConvertToEntityWeenie(result);

                        scrollsBySpellID[spellID] = weenie;
                    }
                }
            }

            return weenie;
        }

        private readonly ConcurrentDictionary<string, uint> creatureWeenieNamesLowerInvariantCache = new ConcurrentDictionary<string, uint>();

        public bool IsCreatureNameInWorldDatabase(string name)
        {
            if (creatureWeenieNamesLowerInvariantCache.TryGetValue(name.ToLowerInvariant(), out _))
                return true;

            using (var context = new WorldDbContext())
            {
                return IsCreatureNameInWorldDatabase(context, name);
            }
        }

        public bool IsCreatureNameInWorldDatabase(WorldDbContext context, string name)
        {
            var query = from weenieRecord in context.Weenie
                        join stringProperty in context.WeeniePropertiesString on weenieRecord.ClassId equals stringProperty.ObjectId
                        where weenieRecord.Type == (int)WeenieType.Creature && stringProperty.Type == (ushort)PropertyString.Name && stringProperty.Value == name
                        select weenieRecord;

            var weenie = query
                .Include(r => r.WeeniePropertiesString)
                .AsNoTracking()
                .FirstOrDefault();

            if (weenie == null)
                return false;

            var weenieName = weenie.GetProperty(PropertyString.Name).ToLowerInvariant();

            creatureWeenieNamesLowerInvariantCache.TryAdd(weenieName, weenie.ClassId);

            return true;
        }


        // =====================================
        // CookBook
        // =====================================

        private readonly Dictionary<uint /* source WCID */, Dictionary<uint /* target WCID */, CookBook>> cookbookCache = new Dictionary<uint, Dictionary<uint, CookBook>>();

        private readonly Dictionary<uint, Recipe> recipeCache = new Dictionary<uint, Recipe>();

        public override CookBook GetCookbook(WorldDbContext context, uint sourceWeenieClassId, uint targetWeenieClassId)
        {
            var cookbook = base.GetCookbook(context, sourceWeenieClassId, targetWeenieClassId);

            lock (cookbookCache)
            {
                // We double check before commiting the recipe.
                // We could be in this lock, and queued up behind us is an attempt to add a result for the same source:target pair.
                if (cookbookCache.TryGetValue(sourceWeenieClassId, out var sourceRecipes))
                {
                    if (!sourceRecipes.ContainsKey(targetWeenieClassId))
                        sourceRecipes.Add(targetWeenieClassId, cookbook);
                }
                else
                    cookbookCache.Add(sourceWeenieClassId, new Dictionary<uint, CookBook>() { { targetWeenieClassId, cookbook } });
            }

            if (cookbook != null)
            {
                // build secondary index for RecipeManager_New caching
                lock (recipeCache)
                {
                    if (!recipeCache.ContainsKey(cookbook.RecipeId))
                        recipeCache.Add(cookbook.RecipeId, cookbook.Recipe);
                }
            }
            return cookbook;
        }

        public override List<CookBook> GetAllCookbooks()
        {
            var cookbooks = base.GetAllCookbooks();

            // Add the cookbooks to the cache
            lock (cookbookCache)
            {
                foreach (var cookbook in cookbooks)
                {
                    // We double check before commiting the recipe.
                    // We could be in this lock, and queued up behind us is an attempt to add a result for the same source:target pair.
                    if (cookbookCache.TryGetValue(cookbook.SourceWCID, out var sourceRecipes))
                    {
                        if (!sourceRecipes.ContainsKey(cookbook.TargetWCID))
                            sourceRecipes.Add(cookbook.TargetWCID, cookbook);
                    }
                    else
                        cookbookCache.Add(cookbook.SourceWCID, new Dictionary<uint, CookBook>() { { cookbook.TargetWCID, cookbook } });
                }
            }

            // build secondary index for RecipeManager_New caching
            lock (recipeCache)
            {
                foreach (var cookbook in cookbooks)
                {
                    if (!recipeCache.ContainsKey(cookbook.RecipeId))
                        recipeCache.Add(cookbook.RecipeId, cookbook.Recipe);
                }
            }

            return cookbooks;
        }


        public void CacheAllCookbooks()
        {
            GetAllCookbooks();
        }

        /// <summary>
        /// Returns the number of Cookbooks currently cached.
        /// </summary>
        public int GetCookbookCacheCount()
        {
            lock (cookbookCache)
                return cookbookCache.Count(r => r.Value != null);
        }

        public void ClearCookbookCache()
        {
            lock (cookbookCache)
                cookbookCache.Clear();

            lock (recipeCache)
                recipeCache.Clear();
        }

        public CookBook GetCachedCookbook(uint sourceWeenieClassId, uint targetWeenieClassId)
        {
            lock (cookbookCache)
            {
                if (cookbookCache.TryGetValue(sourceWeenieClassId, out var recipesForSource))
                {
                    if (recipesForSource.TryGetValue(targetWeenieClassId, out var value))
                        return value;
                }
            }
            return GetCookbook(sourceWeenieClassId, targetWeenieClassId);  // This will add the result into the cache
        }

        public Recipe GetCachedRecipe(uint recipeId)
        {
            lock (recipeCache)
            {
                if (recipeCache.TryGetValue(recipeId, out var recipe))
                    return recipe;
            }
            return GetRecipe(recipeId);  // This will add the result in the cache
        }

        public override Recipe GetRecipe(WorldDbContext context, uint recipeId)
        {
            var recipe = base.GetRecipe(context, recipeId);

            lock (recipeCache)
            {
                if (!recipeCache.ContainsKey(recipeId))
                    recipeCache.Add(recipeId, recipe);
            }
            return recipe;
        }

        // =====================================
        // Encounter
        // =====================================

        private readonly ConcurrentDictionary<ushort /* Landblock */, List<Encounter>> cachedEncounters = new ConcurrentDictionary<ushort, List<Encounter>>();

        /// <summary>
        /// Returns the number of Encounters currently cached.
        /// </summary>
        public int GetEncounterCacheCount()
        {
            return cachedEncounters.Count(r => r.Value != null);
        }

        public List<Encounter> GetCachedEncountersByLandblock(ushort landblock)
        {
            if (cachedEncounters.TryGetValue(landblock, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.Encounter
                    .AsNoTracking()
                    .Where(r => r.Landblock == landblock)
                    .ToList();

                cachedEncounters.TryAdd(landblock, results);
                return results;
            }
        }

        public bool ClearCachedEncountersByLandblock(ushort landblock)
        {
            return cachedEncounters.TryRemove(landblock, out _);
        }


        // =====================================
        // Event
        // =====================================

        private readonly ConcurrentDictionary<string /* Event Name */, Event> cachedEvents = new ConcurrentDictionary<string, Event>();

        public override List<Event> GetAllEvents(WorldDbContext context)
        {
            var events = base.GetAllEvents(context);

            foreach (var result in events)
                cachedEvents[result.Name.ToLower()] = result;

            return events;
        }

        /// <summary>
        /// Returns the number of Events currently cached.
        /// </summary>
        public int GetEventsCacheCount()
        {
            return cachedEvents.Count(r => r.Value != null);
        }

        public Event GetCachedEvent(string name)
        {
            var nameToLower = name.ToLower();

            if (cachedEvents.TryGetValue(nameToLower, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.Event
                    .AsNoTracking()
                    .FirstOrDefault(r => r.Name.ToLower() == nameToLower);

                cachedEvents[nameToLower] = result;
                return result;
            }
        }


        // =====================================
        // HousePortal
        // =====================================

        private readonly ConcurrentDictionary<uint /* House ID */, List<HousePortal>> cachedHousePortals = new ConcurrentDictionary<uint, List<HousePortal>>();

        /// <summary>
        /// This takes under ? second to complete.
        /// </summary>
        public void CacheAllHousePortals()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.HousePortal
                    .AsNoTracking()
                    .AsEnumerable()
                    .GroupBy(r => r.HouseId);

                foreach (var result in results)
                    cachedHousePortals[result.Key] = result.ToList();
            }
        }

        public List<HousePortal> GetCachedHousePortals(uint houseId)
        {
            if (cachedHousePortals.TryGetValue(houseId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.HousePortal
                    .AsNoTracking()
                    .Where(p => p.HouseId == houseId)
                    .ToList();

                cachedHousePortals[houseId] = results;

                return results;
            }
        }


        private readonly ConcurrentDictionary<uint /* Landblock */, List<HousePortal>> cachedHousePortalsByLandblock = new ConcurrentDictionary<uint, List<HousePortal>>();

        public List<HousePortal> GetCachedHousePortalsByLandblock(uint landblockId)
        {
            if (cachedHousePortalsByLandblock.TryGetValue(landblockId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.HousePortal
                    .AsNoTracking()
                    .Where(p => landblockId == p.ObjCellId >> 16)
                    .ToList();

                cachedHousePortalsByLandblock[landblockId] = results;

                return results;
            }
        }


        // =====================================
        // LandblockInstance
        // =====================================

        private readonly ConcurrentDictionary<VariantCacheId /* Landblock */, List<LandblockInstance>> cachedLandblockInstances = new ConcurrentDictionary<VariantCacheId, List<LandblockInstance>>();

        /// <summary>
        /// Returns the number of LandblockInstances currently cached.
        /// </summary>
        public int GetLandblockInstancesCacheCount()
        {
            return cachedLandblockInstances.Count(r => r.Value != null);
        }

        /// <summary>
        /// Clears the cached landblock instances for all landblocks
        /// </summary>
        public void ClearCachedLandblockInstances()
        {
            cachedLandblockInstances.Clear();
        }

        public bool ClearCachedInstancesByLandblock(ushort Landblock, int? variationId)
        {
            VariantCacheId cacheKey = new VariantCacheId { Landblock = Landblock, Variant = variationId ?? 0 };
            return cachedLandblockInstances.TryRemove(cacheKey, out _);
        }

        /// <summary>
        /// Clears the cached landblock instances for a specific landblock
        /// </summary>
        public bool ClearCachedInstancesByLandblock(VariantCacheId cacheKey)
        {
            return cachedLandblockInstances.TryRemove(cacheKey, out _);
        }

        /// <summary>
        /// Only used for CreateInst - do not call this normally as it's not variation aware.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="landblockId"></param>
        /// <returns></returns>
        public List<LandblockInstance> GetLandblockInstancesByLandblockBypassCache(ushort landblockId)
        {
            using (var context = new WorldDbContext())
            {
                var results = context.LandblockInstance
                    .Include(r => r.LandblockInstanceLink)
                    .AsNoTracking()
                    .Where(r => r.Landblock == landblockId)
                    .ToList();

                return results;
            }
        }

        public List<LandblockInstance> GetCachedInstancesByLandblock(WorldDbContext context, ushort landblock, int? variation = null)
        {
            VariantCacheId cacheKey = new VariantCacheId { Landblock = landblock, Variant = variation ?? 0 };
            if (cachedLandblockInstances.TryGetValue(cacheKey, out var value))
                return value;

            List<LandblockInstance> results;
            if (variation.HasValue)
            {
                results = context.LandblockInstance
                    .Include(r => r.LandblockInstanceLink)
                    .AsNoTracking()
                    .Where(r => r.Landblock == landblock && r.VariationId == variation)
                    .ToList();
            }
            else
            {
                results = context.LandblockInstance
                    .Include(r => r.LandblockInstanceLink)
                    .AsNoTracking()
                    .Where(r => r.Landblock == landblock && r.VariationId == null)
                    .ToList();
            }

            cachedLandblockInstances.TryAdd(cacheKey, results);

            return cachedLandblockInstances[cacheKey];
        }

        /// <summary>
        /// Returns statics spawn map and their links for the landblock
        /// </summary>
        public List<LandblockInstance> GetCachedInstancesByLandblock(ushort landblock, int? variation = null)
        {
            using (var context = new WorldDbContext())
                return GetCachedInstancesByLandblock(context, landblock, variation);
        }


        private readonly ConcurrentDictionary<ushort /* Landblock */, uint /* House GUID */> cachedBasementHouseGuids = new ConcurrentDictionary<ushort, uint>();

        public uint GetCachedBasementHouseGuid(ushort landblock)
        {
            if (cachedBasementHouseGuids.TryGetValue(landblock, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.LandblockInstance
                    .AsNoTracking()
                    .Where(r => r.Landblock == landblock
                                && r.WeenieClassId != 11730 /* Exclude House Portal */
                                && r.WeenieClassId != 278   /* Exclude Door */
                                && r.WeenieClassId != 568   /* Exclude Door (entry) */
                                && !r.IsLinkChild)
                    .FirstOrDefault();

                if (result == null)
                    return 0;

                cachedBasementHouseGuids[landblock] = result.Guid;

                return result.Guid;
            }
        }


        // =====================================
        // PointsOfInterest
        // =====================================

        private readonly ConcurrentDictionary<string, PointsOfInterest> cachedPointsOfInterest = new ConcurrentDictionary<string, PointsOfInterest>();

        /// <summary>
        /// Retrieves all points of interest from the database and adds/updates the points of interest cache entries with every point of interest retrieved.
        /// 57 entries cached in 00:00:00.0057937
        /// </summary>
        public void CacheAllPointsOfInterest()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.PointsOfInterest
                    .AsNoTracking();

                foreach (var result in results)
                    cachedPointsOfInterest[result.Name.ToLower()] = result;
            }
        }

        /// <summary>
        /// Returns the number of PointsOfInterest currently cached.
        /// </summary>
        public int GetPointsOfInterestCacheCount()
        {
            return cachedPointsOfInterest.Count(r => r.Value != null);
        }

        /// <summary>
        /// Returns the PointsOfInterest cache.
        /// </summary>
        public ConcurrentDictionary<string, PointsOfInterest> GetPointsOfInterestCache()
        {
            return new ConcurrentDictionary<string, PointsOfInterest>(cachedPointsOfInterest);
        }

        public PointsOfInterest GetCachedPointOfInterest(string name)
        {
            var nameToLower = name.ToLower();

            if (cachedPointsOfInterest.TryGetValue(nameToLower, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.PointsOfInterest
                    .AsNoTracking()
                    .FirstOrDefault(r => r.Name.ToLower() == nameToLower);

                cachedPointsOfInterest[nameToLower] = result;
                return result;
            }
        }


        // =====================================
        // Quest
        // =====================================

        private readonly ConcurrentDictionary<string, Quest> cachedQuest = new ConcurrentDictionary<string, Quest>();

        public bool ClearCachedQuest(string questName)
        {
            return cachedQuest.TryRemove(questName, out _);
        }

        public void ClearAllCachedQuests()
        {
             cachedQuest.Clear();
        }

        public Quest GetCachedQuest(string questName)
        {
            if (cachedQuest.TryGetValue(questName, out var quest))
                return quest;

            using (var context = new WorldDbContext())
            {
                quest = context.Quest.FirstOrDefault(q => q.Name.Equals(questName));
                cachedQuest[questName] = quest;

                return quest;
            }
        }


        // =====================================
        // Recipe
        // =====================================


        // =====================================
        // Spell
        // =====================================

        private readonly ConcurrentDictionary<uint /* Spell ID */, Spell> spellCache = new ConcurrentDictionary<uint, Spell>();

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public void CacheAllSpells()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.Spell
                    .AsNoTracking();

                foreach (var result in results)
                    spellCache[result.Id] = result;
            }
        }

        /// <summary>
        /// Returns the number of Spells currently cached.
        /// </summary>
        public int GetSpellCacheCount()
        {
            return spellCache.Count(r => r.Value != null);
        }

        public void ClearSpellCache()
        {
            spellCache.Clear();
        }

        public Spell GetCachedSpell(uint spellId)
        {
            if (spellCache.TryGetValue(spellId, out var spell))
                return spell;

            using (var context = new WorldDbContext())
            {
                var result = context.Spell
                    .AsNoTracking()
                    .FirstOrDefault(r => r.Id == spellId);

                spellCache[spellId] = result;
                return result;
            }
        }



        // =====================================
        // TreasureDeath
        // =====================================

        private readonly ConcurrentDictionary<uint /* Data ID */, TreasureDeath> cachedDeathTreasure = new ConcurrentDictionary<uint, TreasureDeath>();

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public void CacheAllTreasuresDeath()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.TreasureDeath
                    .AsNoTracking();

                foreach (var result in results)
                    cachedDeathTreasure[result.TreasureType] = result;
            }
        }

        /// <summary>
        /// Returns the number of TreasureDeath currently cached.
        /// </summary>
        public int GetDeathTreasureCacheCount()
        {
            return cachedDeathTreasure.Count(r => r.Value != null);
        }

        public TreasureDeath GetCachedDeathTreasure(uint dataId)
        {
            if (cachedDeathTreasure.TryGetValue(dataId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var result = context.TreasureDeath
                    .AsNoTracking()
                    .FirstOrDefault(r => r.TreasureType == dataId);

                cachedDeathTreasure[dataId] = result;
                return result;
            }
        }


        // =====================================
        // TreasureMaterial
        // =====================================

        // The Key is the Material Code (derived from PropertyInt.TsysMaterialData)
        // The Value is a list of all
        private Dictionary<int /* Material Code */, Dictionary<int /* Tier */, List<TreasureMaterialBase>>> cachedTreasureMaterialBase;
        
        public void CacheAllTreasureMaterialBase()
        {
            using (var context = new WorldDbContext())
            {
                var table = new Dictionary<int, Dictionary<int, List<TreasureMaterialBase>>>();

                var results = context.TreasureMaterialBase.Where(i => i.Probability > 0).ToList();

                foreach (var result in results)
                {
                    if (!table.TryGetValue((int)result.MaterialCode, out var materialCode))
                    {
                        materialCode = new Dictionary<int, List<TreasureMaterialBase>>();
                        table.Add((int)result.MaterialCode, materialCode);
                    }
                    if (!materialCode.TryGetValue((int)result.Tier, out var chances))
                    {
                        chances = new List<TreasureMaterialBase>();
                        materialCode.Add((int)result.Tier, chances);
                    }
                    chances.Add(result.Clone());
                }
                TreasureMaterialBase_Normalize(table);

                cachedTreasureMaterialBase = table;
            }
        }

        private static readonly float NormalizeEpsilon = 0.00001f;

        private void TreasureMaterialBase_Normalize(Dictionary<int, Dictionary<int, List<TreasureMaterialBase>>> materialBase)
        {
            foreach (var kvp in materialBase)
            {
                //var materialCode = kvp.Key;
                var tiers = kvp.Value;

                foreach (var kvp2 in tiers)
                {
                    //var tier = kvp2.Key;
                    var list = kvp2.Value;

                    var totalProbability = list.Sum(i => i.Probability);

                    if (Math.Abs(1.0f - totalProbability) < NormalizeEpsilon)
                        continue;

                    //Console.WriteLine($"TotalProbability {totalProbability} found for TreasureMaterialBase {materialCode} tier {tier}");

                    var factor = 1.0f / totalProbability;

                    foreach (var item in list)
                        item.Probability *= factor;

                    /*totalProbability = list.Sum(i => i.Probability);

                    Console.WriteLine($"After: {totalProbability}");*/
                }
            }
        }

        public List<TreasureMaterialBase> GetCachedTreasureMaterialBase(int materialCode, int tier)
        {
            if (cachedTreasureMaterialBase == null)
                CacheAllTreasureMaterialBase();

            if (cachedTreasureMaterialBase.TryGetValue(materialCode, out var tiers) && tiers.TryGetValue(tier, out var treasureMaterialBase))
                return treasureMaterialBase;
            else
                return null;
        }


        private Dictionary<int /* Material ID */, Dictionary<int /* Color Code */, List<TreasureMaterialColor>>> cachedTreasureMaterialColor;
        
        public void CacheAllTreasureMaterialColor()
        {
            using (var context = new WorldDbContext())
            {
                var table = new Dictionary<int, Dictionary<int, List<TreasureMaterialColor>>>();

                var results = context.TreasureMaterialColor.ToList();

                foreach (var result in results)
                {
                    if (!table.TryGetValue((int)result.MaterialId, out var colorCodes))
                    {
                        colorCodes = new Dictionary<int, List<TreasureMaterialColor>>();
                        table.Add((int)result.MaterialId, colorCodes);
                    }
                    if (!colorCodes.TryGetValue((int)result.ColorCode, out var list))
                    {
                        list = new List<TreasureMaterialColor>();
                        colorCodes.Add((int)result.ColorCode, list);
                    }
                    list.Add(result.Clone());
                }

                TreasureMaterialColor_Normalize(table);

                cachedTreasureMaterialColor = table;
            }
        }

        private void TreasureMaterialColor_Normalize(Dictionary<int, Dictionary<int, List<TreasureMaterialColor>>> materialColor)
        {
            foreach (var kvp in materialColor)
            {
                //var material = kvp.Key;
                var colorCodes = kvp.Value;

                foreach (var kvp2 in colorCodes)
                {
                    //var colorCode = kvp2.Key;
                    var list = kvp2.Value;

                    var totalProbability = list.Sum(i => i.Probability);

                    if (Math.Abs(1.0f - totalProbability) < NormalizeEpsilon)
                        continue;

                    //Console.WriteLine($"TotalProbability {totalProbability} found for TreasureMaterialColor {(MaterialType)material} ColorCode {colorCode}");

                    var factor = 1.0f / totalProbability;

                    foreach (var item in list)
                        item.Probability *= factor;

                    /*totalProbability = list.Sum(i => i.Probability);

                    Console.WriteLine($"After: {totalProbability}");*/
                }
            }
        }

        public List<TreasureMaterialColor> GetCachedTreasureMaterialColors(int materialId, int tsysColorCode)
        {
            if (cachedTreasureMaterialColor == null)
                CacheAllTreasureMaterialColor();

            if (cachedTreasureMaterialColor.TryGetValue(materialId, out var colorCodes) && colorCodes.TryGetValue(tsysColorCode, out var result))
                return result;
            else
                return null;
        }


        // The Key is the Material Group (technically a MaterialId, but more generic...e.g. "Material.Metal", "Material.Cloth", etc.)
        // The Value is a list of all
        private Dictionary<int /* Material Group */, Dictionary<int /* Tier */, List<TreasureMaterialGroups>>> cachedTreasureMaterialGroups;

        public void CacheAllTreasureMaterialGroups()
        {
            using (var context = new WorldDbContext())
            {
                var table = new Dictionary<int, Dictionary<int, List<TreasureMaterialGroups>>>();

                var results = context.TreasureMaterialGroups.ToList();

                foreach (var result in results)
                {
                    if (!table.TryGetValue((int)result.MaterialGroup, out var tiers))
                    {
                        tiers = new Dictionary<int, List<TreasureMaterialGroups>>();
                        table.Add((int)result.MaterialGroup, tiers);
                    }
                    if (!tiers.TryGetValue((int)result.Tier, out var list))
                    {
                        list = new List<TreasureMaterialGroups>();
                        tiers.Add((int)result.Tier, list);
                    }
                    list.Add(result.Clone());
                }
                TreasureMaterialGroups_Normalize(table);

                cachedTreasureMaterialGroups = table;
            }
        }

        private void TreasureMaterialGroups_Normalize(Dictionary<int, Dictionary<int, List<TreasureMaterialGroups>>> materialGroups)
        {
            foreach (var kvp in materialGroups)
            {
                //var materialGroup = kvp.Key;
                var tiers = kvp.Value;

                foreach (var kvp2 in tiers)
                {
                    //var tier = kvp2.Key;
                    var list = kvp2.Value;

                    var totalProbability = list.Sum(i => i.Probability);

                    if (Math.Abs(1.0f - totalProbability) < NormalizeEpsilon)
                        continue;

                    //Console.WriteLine($"TotalProbability {totalProbability} found for TreasureMaterialGroup {(MaterialType)materialGroup} tier {tier}");

                    var factor = 1.0f / totalProbability;

                    foreach (var item in list)
                        item.Probability *= factor;

                    /*totalProbability = list.Sum(i => i.Probability);

                    Console.WriteLine($"After: {totalProbability}");*/
                }
            }
        }

        public List<TreasureMaterialGroups> GetCachedTreasureMaterialGroup(int materialGroup, int tier)
        {
            if (cachedTreasureMaterialGroups == null)
                CacheAllTreasureMaterialGroups();

            if (cachedTreasureMaterialGroups.TryGetValue(materialGroup, out var tiers) && tiers.TryGetValue(tier, out var treasureMaterialGroup))
                return treasureMaterialGroup;
            else
                return null;
        }


        // =====================================
        // TreasureWielded
        // =====================================

        private readonly ConcurrentDictionary<uint /* Data ID */, List<TreasureWielded>> cachedWieldedTreasure = new ConcurrentDictionary<uint, List<TreasureWielded>>();

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public void CacheAllTreasureWielded()
        {
            using (var context = new WorldDbContext())
            {
                var results = context.TreasureWielded
                    .AsNoTracking()
                    .AsEnumerable()
                    .GroupBy(r => r.TreasureType);

                foreach (var result in results)
                    cachedWieldedTreasure[result.Key] = result.ToList();
            }
        }

        /// <summary>
        /// Returns the number of TreasureWielded currently cached.
        /// </summary>
        public int GetWieldedTreasureCacheCount()
        {
            return cachedWieldedTreasure.Count(r => r.Value != null);
        }


        public List<TreasureWielded> GetCachedWieldedTreasure(uint dataId)
        {
            if (cachedWieldedTreasure.TryGetValue(dataId, out var value))
                return value;

            using (var context = new WorldDbContext())
            {
                var results = context.TreasureWielded
                    .AsNoTracking()
                    .Where(r => r.TreasureType == dataId)
                    .ToList();

                cachedWieldedTreasure[dataId] = results;
                return results;
            }
        }

        public void ClearWieldedTreasureCache()
        {
            cachedWieldedTreasure.Clear();
        }

        public void ClearDeathTreasureCache()
        {
            cachedDeathTreasure.Clear();
        }

        public int? GetQuestIdByName(string questName)
        {
            using (var context = new WorldDbContext())
            {
                var quest = context.Quest
                    .FirstOrDefault(q => q.Name.ToLower() == questName.ToLower()); // Case-insensitive match
                return (int)quest?.Id;
            }
        }

        private string FormatCooldown(TimeSpan time)
        {
            if (time.TotalDays >= 1)
                return $"{(int)time.TotalDays}d {(int)time.Hours}h {(int)time.Minutes}m {(int)time.Seconds}s";
            if (time.TotalHours >= 1)
                return $"{(int)time.Hours}h {(int)time.Minutes}m {(int)time.Seconds}s";
            if (time.TotalMinutes >= 1)
                return $"{(int)time.Minutes}m {(int)time.Seconds}s";
            return $"{(int)time.Seconds}s";
        }

        public (bool, string) IncrementAndCheckIPQuestAttempts(uint questId, string playerIp, uint characterId, int maxAttempts)
        {
            using (var context = new WorldDbContext())
            using (var shardContext = new ShardDbContext())
            {
                var quest = context.Quest.FirstOrDefault(q => q.Id == questId);
                if (quest == null)
                {
                    //Console.WriteLine($"Quest with id {questId} does not exist.");
                    return (false, "Invalid quest data.");
                }

                // ---- Character-specific cooldown ----
                var characterTracking = shardContext.CharacterPropertiesQuestRegistry
                    .FirstOrDefault(q => q.CharacterId == characterId && q.QuestName == quest.Name);

                if (characterTracking == null)
                {
                    characterTracking = new CharacterPropertiesQuestRegistry
                    {
                        CharacterId = characterId,
                        QuestName = quest.Name,
                        LastTimeCompleted = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        NumTimesCompleted = 1
                    };
                    shardContext.CharacterPropertiesQuestRegistry.Add(characterTracking);
                }
                else
                {
                    var timeSinceLastComplete = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - characterTracking.LastTimeCompleted;
                    if (timeSinceLastComplete < quest.MinDelta)
                    {
                        var remainingCooldown = TimeSpan.FromSeconds(quest.MinDelta - timeSinceLastComplete);
                        //Console.WriteLine($"[Blocked] Character cooldown active for questId: {questId}, characterId: {characterId}. Remaining cooldown: {remainingCooldown.TotalSeconds}s.");
                        return (false, $"You have solved this quest too recently! You may complete this quest again in {FormatCooldown(remainingCooldown)}.");
                    }

                    characterTracking.LastTimeCompleted = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    characterTracking.NumTimesCompleted++;
                }

                // ---- IP-wide solve count enforcement ----
                var ipTracking = context.QuestIpTracking.FirstOrDefault(q => q.QuestId == questId && q.IpAddress == playerIp);
                if (ipTracking == null)
                {
                    ipTracking = new QuestIpTracking
                    {
                        QuestId = questId,
                        IpAddress = playerIp,
                        SolvesCount = 1,
                        LastSolveTime = DateTime.UtcNow
                    };
                    context.QuestIpTracking.Add(ipTracking);
                }
                else
                {
                    // Check if MinDelta has expired
                    var timeSinceLastSolve = (DateTime.UtcNow - ipTracking.LastSolveTime)?.TotalSeconds ?? double.MaxValue;
                    if (timeSinceLastSolve >= quest.MinDelta)
                    {
                        //Console.WriteLine($"[IP Tracking] Cooldown expired for playerIp: {playerIp}, questId: {questId}. Resetting SolvesCount.");
                        ipTracking.SolvesCount = 0; // Reset solves count if cooldown expired
                        ipTracking.LastSolveTime = DateTime.UtcNow;
                    }

                    // Enforce maxAttempts
                    if (ipTracking.SolvesCount >= maxAttempts)
                    {
                        //Console.WriteLine($"[Blocked] IP-based max attempts reached for questId: {questId}, playerIp: {playerIp}. Blocking loot.");
                        return (false, "You cannot loot this item. Your IP-wide limit has been reached.");
                    }

                    ipTracking.SolvesCount++;
                    ipTracking.LastSolveTime = DateTime.UtcNow;
                }

                // Save changes
                shardContext.SaveChanges();
                context.SaveChanges();

                //Console.WriteLine($"[Success] Loot allowed for questId: {questId}, playerIp: {playerIp}, characterId: {characterId}.");
                return (true, string.Empty);
            }
        }

        /// <summary>
        /// Retrieves the quest IP tracking entry for a specific quest and IP.
        /// </summary>
        public QuestIpTracking GetQuestIpTracking(int questId, string playerIp)
        {
            using (var context = new WorldDbContext())
            {
                return context.QuestIpTracking
                    .FirstOrDefault(q => q.QuestId == questId && q.IpAddress == playerIp);
            }
        }
    }
}
