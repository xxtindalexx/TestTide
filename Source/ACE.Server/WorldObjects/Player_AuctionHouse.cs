using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Database.Models.Shard;
using Microsoft.EntityFrameworkCore;
using ACE.Database.Models;
using ACE.Server.Managers;
using static ACE.Server.WorldObjects.Player;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Common;
using System.Threading.Tasks;
using ACE.Database;
using ACE.Entity.Models;
using Discord;
using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects
{
    public static class AuctionHouse
    {
        private static readonly object auctionLock = new object();
        private static readonly List<AuctionItem> ActiveAuctions = new List<AuctionItem>();
        private static readonly List<AuctionReturnItem> PendingReturns = new List<AuctionReturnItem>();

        // Load auctions from the database on server startup
        public static void LoadAuctionsFromDB()
        {
            StartAuctionExpirationTask();
            lock (auctionLock)
            {
                ActiveAuctions.Clear();
                //Console.WriteLine("[AUCTION] Loading auctions from DB...");

                using (var context = new ShardDbContext())
                {
                    var dbAuctions = context.AuctionEntries.AsNoTracking().ToList();
                    //Console.WriteLine($"[AUCTION] Found {dbAuctions.Count} auctions in DB.");

                    foreach (var entry in dbAuctions)
                    {
                        // Console.WriteLine($"[AUCTION] Loading auction {entry.Id} for item {entry.ItemGuid}...");

                        var item = FindWorldObject(entry.ItemGuid);
                        if (item == null)
                        {
                            //Console.WriteLine($"[AUCTION WARNING] Could not load WorldObject for item {entry.ItemGuid}. Skipping.");
                            continue;
                        }

                        ActiveAuctions.Add(new AuctionItem
                        {
                            AuctionId = entry.Id,
                            Item = item,
                            Seller = PlayerManager.FindByGuid((uint)entry.SellerGuid) as Player,
                            SellerGuid = entry.SellerGuid, // ✅ Store SellerGuid
                            SellerName = entry.SellerName ?? "Unknown",
                            MinBid = entry.MinBid,
                            BuyoutPrice = entry.BuyoutPrice ?? 0,
                            HighestBid = entry.HighestBid,
                            CurrentBidder = entry.BuyerGuid != null
                                ? PlayerManager.FindByGuid((uint)entry.BuyerGuid) as Player
                                : null,
                            StartTime = entry.StartTime,
                            DurationSeconds = entry.DurationSeconds
                        });

                        //Console.WriteLine($"[AUCTION] Successfully loaded auction {entry.Id} - Seller Guid: {entry.SellerGuid} Seller Name {entry.SellerName}.");
                    }
                }
            }
        }

        public static void LoadPendingReturnsFromDB()
        {
            lock (auctionLock)
            {
                PendingReturns.Clear();
                //Console.WriteLine("[AUCTION] Loading pending returns from DB...");

                using (var context = new ShardDbContext())
                {
                    var dbReturns = context.AuctionReturns.AsNoTracking().ToList();
                    //Console.WriteLine($"[AUCTION] Found {dbReturns.Count} pending returns in DB.");

                    foreach (var entry in dbReturns)
                    {
                        //Console.WriteLine($"[AUCTION] Loading return for item {entry.ItemGuid}...");

                        var item = FindWorldObject(entry.ItemGuid);
                        if (item == null)
                        {
                            // Console.WriteLine($"[AUCTION WARNING] Could not load WorldObject for item {entry.ItemGuid}. Skipping.");
                            continue;
                        }

                        PendingReturns.Add(new AuctionReturnItem
                        {
                            ReturnId = entry.Id,
                            Item = item,
                            SellerGuid = entry.SellerGuid,
                            ReturnDate = entry.ReturnDate
                        });

                        // Console.WriteLine($"[AUCTION] Successfully loaded return for item {entry.ItemGuid}.");
                    }
                }
            }
        }

        public static WorldObject FindWorldObject(ulong objectGuid)
        {
            if (objectGuid > uint.MaxValue)
                return null; // Prevent overflow issues

            var objGuid = new ObjectGuid((uint)objectGuid);
            // Console.WriteLine($"[AUCTION DEBUG] Searching for item {objectGuid}...");

            // **Step 1: Search for the item in active player inventories**
            foreach (var player in PlayerManager.GetAllPlayers())
            {
                if (player is Player realPlayer)
                {
                    var item = realPlayer.FindObject(objGuid, SearchLocations.Everywhere, out _, out _, out _);
                    if (item != null)
                    {
                        // Console.WriteLine($"[AUCTION DEBUG] Found item {objectGuid} in player's inventory.");
                        return item; // ✅ Found item, return it
                    }
                }
            }

            // Console.WriteLine($"[AUCTION DEBUG] Item {objectGuid} not found in any player's inventory.");

            // **Step 2: Try recreating the item from the database**
            var recreatedItem = RecreateItemFromDatabase(objGuid);
            if (recreatedItem != null)
            {
                // Console.WriteLine($"[AUCTION DEBUG] Successfully recreated item {objectGuid} from database.");
                return recreatedItem;
            }

            return null; // ❌ Item not found anywhere
        }

        private static WorldObject RecreateItemFromDatabase(ObjectGuid itemGuid)
        {
            // Console.WriteLine($"[AUCTION DEBUG] Attempting to recreate item {itemGuid.Full} from DB...");

            // Step 1: Try retrieving from Biota table
            using (var context = new ShardDbContext())
            {
                var dbItem = context.Biota
                    .Include(b => b.BiotaPropertiesInt)
                    .Include(b => b.BiotaPropertiesFloat)
                    .Include(b => b.BiotaPropertiesString)
                    .Include(b => b.BiotaPropertiesSpellBook)
                    .FirstOrDefault(b => b.Id == itemGuid.Full);

                if (dbItem == null)
                {
                    //  Console.WriteLine($"[AUCTION ERROR] Item {itemGuid.Full} not found in Biota table.");
                    return null;
                }

                // Console.WriteLine($"[AUCTION DEBUG] Found item {itemGuid.Full} in Biota table.");

                // ✅ Get the stored Weenie from the database
                var dbWeenie = DatabaseManager.World.GetWeenie((uint)dbItem.WeenieClassId);
                if (dbWeenie == null)
                {
                    //  Console.WriteLine($"[AUCTION ERROR] No Weenie found for WeenieClassId {dbItem.WeenieClassId}.");
                    return null;
                }

                // ✅ Convert database Weenie to entity Weenie
                var entityWeenie = ACE.Database.Adapter.WeenieConverter.ConvertToEntityWeenie(dbWeenie);
                if (entityWeenie == null)
                {
                    //  Console.WriteLine($"[AUCTION ERROR] Failed to convert Weenie {dbItem.WeenieClassId}.");
                    return null;
                }

                // ✅ Create the WorldObject
                var newItem = Factories.WorldObjectFactory.CreateWorldObject(entityWeenie, new ObjectGuid(dbItem.Id));

                if (newItem == null)
                {
                    // Console.WriteLine($"[AUCTION ERROR] Failed to create WorldObject for item {itemGuid.Full}.");
                    return null;
                }

                // ✅ Restore item properties from Biota table
                RestoreItemProperties(newItem, dbItem);

                //  Console.WriteLine($"[AUCTION DEBUG] Successfully recreated WorldObject for item {itemGuid.Full}.");
                return newItem;
            }
        }

        private static void RestoreItemProperties(WorldObject item, ACE.Database.Models.Shard.Biota dbItem)
        {
            //  Console.WriteLine($"[AUCTION DEBUG] Restoring properties for {item.NameWithMaterial}...");

            // Restore integer properties (e.g., damage, value, burden)
            foreach (var prop in dbItem.BiotaPropertiesInt)
            {
                item.SetProperty((PropertyInt)prop.Type, prop.Value);
            }

            // Restore float properties (e.g., weapon speed, variance)
            foreach (var prop in dbItem.BiotaPropertiesFloat)
            {
                item.SetProperty((PropertyFloat)prop.Type, prop.Value);
            }

            // Restore string properties (e.g., custom name modifications)
            foreach (var prop in dbItem.BiotaPropertiesString)
            {
                item.SetProperty((PropertyString)prop.Type, prop.Value);
            }

            // ✅ Restore spell properties (e.g., enchantments)
            if (dbItem.BiotaPropertiesSpellBook != null && dbItem.BiotaPropertiesSpellBook.Count > 0)
            {
                foreach (var spellEntry in dbItem.BiotaPropertiesSpellBook)
                {
                    bool spellAdded;
                    float spellProbability = item.Biota.GetOrAddKnownSpell(spellEntry.Spell, item.BiotaDatabaseLock, out spellAdded, spellEntry.Probability);

                    // ✅ Ensure the spell is stored in the WorldObject's spellbook
                    item.Biota.PropertiesSpellBook ??= new Dictionary<int, float>();
                    item.Biota.PropertiesSpellBook[spellEntry.Spell] = spellProbability;
                }
            }

            //  Console.WriteLine($"[AUCTION DEBUG] Successfully restored properties for {item.NameWithMaterial}.");
        }

        private static void ReassignAuctionIds()
        {
            lock (auctionLock)
            {
                using (var context = new ShardDbContext())
                {
                    // Load existing auctions ordered by expiration time
                    var dbAuctions = context.AuctionEntries.OrderBy(a => a.StartTime.AddSeconds(a.DurationSeconds)).ToList();

                    // Reset Auction ID to be sequential from 1
                    int newId = 1;
                    foreach (var dbAuction in dbAuctions)
                    {
                        dbAuction.Id = newId;
                        newId++;
                    }

                    context.SaveChanges();
                }

                // Refresh the ActiveAuctions list with updated IDs
                LoadAuctionsFromDB();
            }
        }

        public static bool CanListAuction(Player seller, string playerIp)
        {
            using (var context = new ShardDbContext())
            {
                // ✅ Check the total number of active auctions from this IP address
                int ipActiveAuctions = context.AuctionEntries.Count(a => a.SellerIp == playerIp);

                if (ipActiveAuctions >= 10)
                {
                    seller.SendMessage("[AUCTION ERROR] You have reached the maximum of 10 active auctions for your IP address.");
                    return false;
                }

                return true;
            }
        }

        public static void ListAuction(Player seller, WorldObject item, int minBid, int buyoutPrice, int durationMinutes, string playerIp, string sellerNote)
        {
            lock (auctionLock)
            {
                // ✅ We no longer check the auction limit here, since it's handled in `CanListAuction()`

                bool removedSuccessfully = seller.TryRemoveItemForAuction(item);
                //Console.WriteLine($"[DEBUG] Attempted to remove {item.NameWithMaterial} for auction. Success: {removedSuccessfully}");

                var existingItem = seller.FindObject((uint)item.Guid.Full, SearchLocations.Everywhere);
                if (!removedSuccessfully)
                {
                    if (existingItem != null)
                    {
                        //Console.WriteLine($"[DEBUG] {item.NameWithMaterial} still exists in inventory. Aborting auction.");
                        seller.SendMessage($"[Auction Error] Failed to remove {item.NameWithMaterial} from inventory. Ensure it is not equipped, in trade, attuned, or a container.");
                        return;
                    }
                    else
                    {
                        Console.WriteLine($"[WARNING] {item.NameWithMaterial} NOT FOUND in inventory despite removal failing. Proceeding with listing.");
                    }
                }

                string itemType = GetItemTypeCategory(item.ItemType);

                var auction = new AuctionItem
                {
                    AuctionId = ActiveAuctions.Count + 1,
                    Item = item,
                    Seller = seller,
                    SellerName = seller.Name,
                    SellerGuid = seller.Guid.Full,
                    SellerIp = playerIp,
                    ItemType = itemType,
                    MinBid = minBid,
                    BuyoutPrice = buyoutPrice,
                    HighestBid = 0,
                    DurationSeconds = durationMinutes * 60,
                    StartTime = DateTime.UtcNow,
                    SellerNote = sellerNote
                };

               // Console.WriteLine($"[DEBUG] Auction created. Seller GUID before save: {auction.SellerGuid} & Seller Name: {auction.SellerName} & ItemType: {auction.ItemType}");
                ActiveAuctions.Add(auction);
                SaveAuctionToDB(auction);
               // Console.WriteLine($"[DEBUG] Auction added to ActiveAuctions. Seller GUID after save: {auction.SellerGuid} & Seller Name: {auction.SellerName} & ItemType: {auction.ItemType}");

                int quantity = item.GetProperty(PropertyInt.StackSize) ?? 1;
                string stackMessage = quantity > 1 ? $"({quantity})" : "";
                seller.SendMessage($"[AUCTION LISTED] You have listed {stackMessage}{item.NameWithMaterial} for auction with a minimum bid of {minBid} and a buyout price of {buyoutPrice} Enlightened Coins."); 
            }
        }

        public static void SaveAuctionToDB(AuctionItem auction)
        {
            using (var context = new ShardDbContext())
            {
                string itemTypeCategory = GetItemTypeCategory(auction.Item.ItemType);
                if (string.IsNullOrEmpty(auction.SellerIp))
                {
                    //Console.WriteLine($"[AUCTION ERROR] Seller IP is null for Auction {auction.AuctionId}");
                    return;
                }

               // Console.WriteLine($"[DEBUG] Saving auction {auction.AuctionId} - Seller GUID: {auction.SellerGuid}, IP: {auction.SellerIp}, Type: {auction.ItemType}");
                var auctionEntry = new AuctionEntry
                {
                    ItemGuid = auction.Item.Guid.Full,
                    SellerGuid = auction.Seller.Guid.Full,
                    SellerName = auction.Seller?.Name ?? "Unknown",
                    SellerIp = auction.SellerIp,
                    MinBid = auction.MinBid,
                    BuyoutPrice = auction.BuyoutPrice,
                    HighestBid = auction.HighestBid,
                    BuyerGuid = auction.CurrentBidder?.Guid.Full,
                    LastBidderGuid = auction.LastBidderGuid ?? 0,
                    DurationSeconds = auction.DurationSeconds,
                    StartTime = DateTime.UtcNow, // Ensure a valid timestamp
                    ItemType = itemTypeCategory,
                    SellerNote = auction.SellerNote
                };

                context.AuctionEntries.Add(auctionEntry);
                context.SaveChanges();
                auction.AuctionId = auctionEntry.Id;
            }
        }

        public static string GetItemTypeCategory(ItemType itemType)
        {
            switch (itemType)
            {
                case ItemType.MeleeWeapon:
                case ItemType.Weapon:
                    return "Melee";

                case ItemType.MissileWeapon:
                    return "Missile";

                case ItemType.Caster:
                case ItemType.WeaponOrCaster:
                    return "Caster";

                case ItemType.Armor:
                case ItemType.Clothing:
                    return "Armor";

                case ItemType.Jewelry:
                    return "Jewelry";

                default:
                    return "Misc"; // If unclassified, fall under Misc
            }
        }


        public static void RemoveAuctionFromDB(AuctionItem auction)
        {
            using (var context = new ShardDbContext())
            {
                var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId);
                if (auctionEntry != null)
                {
                    context.AuctionEntries.Remove(auctionEntry);
                    context.SaveChanges();
                }
            }

            // Remove from memory
            ActiveAuctions.RemoveAll(a => a.AuctionId == auction.AuctionId);

            // Reassign auction IDs to start from 1
            ReassignAuctionIds();
        }

        public static void PlaceBid(Player bidder, int auctionId, int bidAmount)
        {
            lock (auctionLock)
            {
                var auction = ActiveAuctions.FirstOrDefault(a => a.AuctionId == auctionId);
                if (auction == null || DateTime.UtcNow > auction.StartTime.AddSeconds(auction.DurationSeconds))
                {
                    bidder.SendMessage("[AUCTION ERROR] Auction not found or has already ended.");
                    return;
                }

                // ✅ Get the bidder's IP address
                string bidderIp = bidder.Session.EndPoint.Address.ToString();

                // ✅ IP Block check unless IsPlussed
                using (var context = new ShardDbContext())
                {
                    var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auctionId);
                    if (auctionEntry != null)
                    {
                        if (auctionEntry.SellerIp == bidderIp && !bidder.IsPlussed)
                        {
                            bidder.SendMessage("[AUCTION ERROR] You cannot bid on this auction. Your IP address matches the seller's IP.");
                            return;
                        }
                    }
                }

                // ✅ Prevent seller from bidding on their own auction (unless admin)
                if (!bidder.IsPlussed && auction.Seller != null && auction.Seller.Guid.Full == bidder.Guid.Full)
                {
                    bidder.SendMessage("[AUCTION ERROR] You cannot bid on your own auction.");
                    return;
                }

                if (bidAmount < auction.MinBid || bidAmount <= auction.HighestBid)
                {
                    bidder.SendMessage($"[AUCTION ERROR] Your bid must be at least {auction.MinBid} and higher than the current highest bid ({auction.HighestBid}).");
                    return;
                }

                if (bidder.BankedEnlightenedCoins < bidAmount)
                {
                    bidder.SendMessage("[AUCTION ERROR] You do not have enough Enlightened Coins banked to place this bid.");
                    return;
                }

                // ✅ Refund current bidder if exists
                if (auction.CurrentBidder != null)
                {
                    auction.CurrentBidder.BankedEnlightenedCoins += auction.HighestBid;
                    auction.CurrentBidder.SendMessage($"[AUCTION WARN] You have been outbid on {auction.Item.NameWithMaterial}. Your {auction.HighestBid} Enlightened Coins have been refunded.");  
                }

                // ✅ Deduct from new bidder
                bidder.BankedEnlightenedCoins -= bidAmount;

                // ✅ Update in-memory auction state
                auction.LastBidderGuid = auction.CurrentBidder?.Guid.Full;
                auction.PreviousBidAmount = auction.HighestBid;
                auction.CurrentBidder = bidder;
                auction.HighestBid = bidAmount;
                NotifyBidderOutbid(auction, bidder);

                // ✅ Persist changes to database
                using (var context = new ShardDbContext())
                {
                    var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId);
                    if (auctionEntry != null)
                    {
                        auctionEntry.LastBidderGuid = auction.LastBidderGuid;
                        auctionEntry.PreviousBidAmount = auction.PreviousBidAmount;
                        auctionEntry.BuyerGuid = bidder.Guid.Full;
                        auctionEntry.HighestBid = bidAmount;
                        context.SaveChanges();
                    }
                }

                // ✅ Notify seller
                auction.Seller?.SendMessage($"[AUCTION ATTENTION] A new highest bid of {bidAmount} Enlightened Coins has been placed on {auction.Item.NameWithMaterial}.");

                // ✅ Confirm bid to bidder
                bidder.SendMessage($"[AUCTION BID] You have placed a bid of {bidAmount} Enlightened Coins on {auction.Item.NameWithMaterial}.");
            }
        }

        public static AuctionItem GetAuctionById(int auctionId)
        {
            lock (auctionLock)
            {
                var auction = ActiveAuctions.FirstOrDefault(a => a.AuctionId == auctionId);
                if (auction != null)
                    Console.WriteLine($"[DEBUG] Found auction {auction.AuctionId}, Seller GUID: {auction.SellerGuid}");
                else
                    Console.WriteLine($"[DEBUG] Auction ID {auctionId} not found in ActiveAuctions!");

                return auction;
            }
        }


        public static void BuyoutItem(Player buyer, int auctionId)
        {
            lock (auctionLock)
            {
                var auction = ActiveAuctions.FirstOrDefault(a => a.AuctionId == auctionId);

                if (auction == null || DateTime.UtcNow > auction.StartTime.AddSeconds(auction.DurationSeconds))
                {
                    buyer.SendMessage("[AUCTION ERROR] Auction not found or has already ended.");
                    return;
                }

                // ✅ Prevent seller from buying their own auction
                if (auction.Seller != null && auction.Seller.Guid.Full == buyer.Guid.Full)
                {
                    buyer.SendMessage("[AUCTION ERROR] You cannot buyout your own auction.");
                    return;
                }

                if (auction.BuyoutPrice <= 0)
                {
                    buyer.SendMessage("[AUCTION ERROR] This auction does not have a buyout price.");
                    return;
                }

                if (buyer.BankedEnlightenedCoins < auction.BuyoutPrice)
                {
                    buyer.SendMessage("[AUCTION ERROR] You do not have enough Enlightened Coins banked to buy this item.");
                    return;
                }

                // Deduct coins from the buyer
                buyer.BankedEnlightenedCoins -= auction.BuyoutPrice;

                var sellerGuid = auction.Seller?.Guid.Full ?? 0;
                var seller = auction.Seller;

                if (sellerGuid == 0)
                {
                    buyer.SendMessage("[AUCTION ERROR] Error processing transaction. Contact support.");
                    return;
                }

                var sellerOnline = PlayerManager.GetOnlinePlayer((uint)sellerGuid);

                if (sellerOnline != null) // ✅ Seller is ONLINE
                {
                    sellerOnline.BankedEnlightenedCoins += auction.BuyoutPrice;
                    sellerOnline.SendMessage($"[AUCTION SOLD] Your item {auction.Item.NameWithMaterial} was sold for {auction.BuyoutPrice} Enlightened Coins.");
                    NotifySellerAuctionSoldBuyout(auction);
                }
                else // ✅ Seller is OFFLINE - Store pending payment in DB
                {
                    StorePendingAuctionPayment(sellerGuid, auction.BuyoutPrice);
                    NotifySellerAuctionSoldBuyout(auction);
                }

                // ✅ **Ensure the item is given to the buyer**
                if (!buyer.TryCreateInInventoryWithNetworking(auction.Item))
                {
                    StorePendingAuctionReturn(buyer.Guid.Full, auction.Item, "BUYER", buyer.Guid.Full);

                    buyer.SendMessage($"[AUCTION WON] Since your inventory is full, your purchase of {auction.Item.NameWithMaterial} has been stored temporarily. Please use /auction retrieve after clearing space.");
                }
                else
                {
                    buyer.SendMessage($"[AUCTION WON] You have purchased {auction.Item.NameWithMaterial} for {auction.BuyoutPrice} Enlightened Coins.");
                    NotifyBidderAuctionSoldBid(auction);
                }

                // ✅ **Remove auction from active list and DB**
                ActiveAuctions.Remove(auction);
                RemoveAuctionFromDB(auction);
            }
        }

        public static void ExpireAuctions()
        {
            lock (auctionLock)
            {
                using (var context = new ShardDbContext())
                {
                    var nowUtc = DateTime.UtcNow;
                    var expiredAuctions = ActiveAuctions
                        .Where(a => (nowUtc - a.StartTime).TotalSeconds >= a.DurationSeconds)
                        .ToList();

                    foreach (var auction in expiredAuctions)
                    {
                        try
                        {
                            ulong sellerGuid = auction.Seller?.Guid.Full ?? auction.SellerGuid;
                            ulong buyerGuid = auction.CurrentBidder?.Guid.Full ?? 0;

                            var seller = PlayerManager.GetOnlinePlayer((uint)sellerGuid);
                            var buyer = PlayerManager.GetOnlinePlayer((uint)buyerGuid);

                            if (auction.HighestBid > 0) // **Item was sold**
                            {
                                if (seller != null)
                                {
                                    seller.BankedEnlightenedCoins += auction.HighestBid;
                                    seller.SendMessage($"[AUCTION SOLD] Your auction for {auction.Item.NameWithMaterial} was sold for {auction.HighestBid} Enlightened Coin(s).");
                                    NotifySellerAuctionSoldBid(auction);
                                }
                                else
                                {
                                    StorePendingAuctionPayment(sellerGuid, auction.HighestBid);
                                }

                                if (buyer != null)
                                {
                                    if (!buyer.TryCreateInInventoryWithNetworking(auction.Item))
                                    {
                                        StorePendingAuctionReturn(buyer.Guid.Full, auction.Item, "BUYER", buyer.Guid.Full);

                                        buyer.SendMessage($"[AUCTION WON] Since your inventory is full, your purchase of {auction.Item.NameWithMaterial} has been stored temporarily. Please use /auction retrieve after clearing space.");
                                    }
                                    else
                                    {
                                        buyer.SendMessage($"[AUCTION WON] Congratulations! You won {auction.Item.NameWithMaterial}.");
                                        NotifyBidderAuctionSoldBid(auction);
                                    }
                                }
                                else
                                {
                                    StorePendingAuctionReturn(buyerGuid, auction.Item, "BUYER", buyerGuid);
                                }
                            }
                            else // **Auction expired without sale**
                            {
                                if (seller != null)
                                {
                                    if (!seller.TryCreateInInventoryWithNetworking(auction.Item))
                                    {
                                        StorePendingAuctionReturn(seller.Guid.Full, auction.Item, "SELLER");
                                    }
                                    else
                                    {
                                        seller.SendMessage($"[AUCTION EXPIRED] Your auction for {auction.Item.NameWithMaterial} expired and was returned.");
                                        NotifySellerAuctionExpired(auction);
                                    }
                                }
                                else
                                {
                                    StorePendingAuctionReturn(sellerGuid, auction.Item, "SELLER");
                                }
                            }

                            // ✅ Remove from database
                            var expiredAuction = context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId);
                            if (expiredAuction != null)
                            {
                                context.AuctionEntries.Remove(expiredAuction);
                                context.SaveChanges();
                            }

                            // ✅ Remove from active auction list
                            ActiveAuctions.Remove(auction);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[AUCTION ERROR] Exception processing auction ID {auction.AuctionId}: {ex.Message}");
                        }
                    }
                }
            }
        }

        public static void StorePendingAuctionReturn(ulong ownerGuid, WorldObject item, string recipient, ulong? buyerGuid = null)
        {
            using (var context = new ShardDbContext())
            {
                // Console.WriteLine($"[AUCTION] Attempting to store {item.NameWithMaterial} for {recipient} ({ownerGuid})...");

                var existingReturn = context.AuctionReturns.FirstOrDefault(a => a.ItemGuid == item.Guid.Full);
                if (existingReturn != null)
                {
                    // Console.WriteLine($"[AUCTION] WARNING: Item {item.NameWithMaterial} ({item.Guid.Full}) is already stored in AuctionReturns.");
                    return;
                }

                var returnEntry = new AuctionReturnEntry
                {
                    SellerGuid = ownerGuid, // Keep seller guid for expired auctions
                    BuyerGuid = buyerGuid ?? 0, // Store buyerGuid if available, otherwise default to 0
                    ItemGuid = item.Guid.Full,
                    ReturnDate = DateTime.UtcNow
                };

                context.AuctionReturns.Add(returnEntry);
                //   Console.WriteLine($"[AUCTION] Added {item.NameWithMaterial} to auction_returns with seller {ownerGuid} and buyer {buyerGuid}.");

                int savedChanges = context.SaveChanges();
                if (savedChanges > 0)
                {
                    // Console.WriteLine($"[AUCTION] Successfully stored {item.NameWithMaterial} in auction_returns.");
                }
                else
                {
                    // Console.WriteLine($"[AUCTION] ERROR: Database changes did NOT persist for {item.NameWithMaterial}.");
                }
            }
        }

        public static void StorePendingAuctionPayment(ulong sellerGuid, int amount)
        {
            using (var context = new ShardDbContext())
            {
                // Console.WriteLine($"[AUCTION] Storing {amount} Enlightened Coins payment for offline seller (GUID: {sellerGuid})...");

                var paymentEntry = new AuctionPaymentEntry
                {
                    SellerGuid = sellerGuid,
                    Amount = amount,
                    PaymentDate = DateTime.UtcNow
                };

                context.AuctionPayments.Add(paymentEntry);
                context.SaveChanges();

                // Console.WriteLine($"[AUCTION] Successfully stored {amount} EC payment for seller (GUID: {sellerGuid}).");
            }
        }


        public static void StartAuctionExpirationTask()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    ExpireAuctions();
                    await Task.Delay(TimeSpan.FromMinutes(1)); // Check every 1 minute
                }
            });
        }

        public static void RetrieveAuctionReturns(Player player)
        {
            lock (auctionLock)
            {
                //  Console.WriteLine($"[AUCTION] {player.Name} is retrieving auction returns...");
                //  Console.WriteLine($"[AUCTION DEBUG] Player GUID: {player.Guid.Full}");

                using (var context = new ShardDbContext())
                {
                    var playerReturns = context.AuctionReturns
                        .Where(r => r.SellerGuid == player.Guid.Full || r.BuyerGuid == player.Guid.Full)
                        .ToList();

                    //  Console.WriteLine($"[AUCTION DEBUG] Found {playerReturns.Count} matching returns in database.");

                    if (playerReturns.Count == 0)
                    {
                        player.SendMessage("[AUCTION RETRIEVAL] You have no expired auction items to retrieve.");
                        return;
                    }

                    foreach (var returnEntry in playerReturns)
                    {
                        // Console.WriteLine($"[AUCTION] Attempting to retrieve stored item {returnEntry.ItemGuid} for {player.Name}.");

                        var item = RecreateItemFromDatabase(new ObjectGuid((uint)returnEntry.ItemGuid));
                        if (item == null)
                        {
                            // Console.WriteLine($"[AUCTION ERROR] Failed to retrieve {returnEntry.ItemGuid} for {player.Name}.");
                            continue;
                        }

                        if (!player.TryCreateInInventoryWithNetworking(item))
                        {
                            player.SendMessage($"[AUCTION ERROR] Your inventory is full! Unable to retrieve {item.NameWithMaterial}.");
                            continue;
                        }

                        // ✅ Differentiate seller retrieval vs buyer retrieval
                        string retrievalMessage = returnEntry.BuyerGuid == player.Guid.Full
                            ? $"[AUCTION RETRIEVAL] Your purchase for item {item.NameWithMaterial} has been delivered."
                            : $"[AUCTION EXPIRED] Your expired auction item {item.NameWithMaterial} has been returned.";

                        player.SendMessage(retrievalMessage);
                        // Console.WriteLine($"[AUCTION] {item.NameWithMaterial} successfully retrieved by {player.Name}. Removing from auction_returns.");

                        context.AuctionReturns.Remove(returnEntry);
                        context.SaveChanges();
                    }
                }
            }
        }

        public static void CancelAuction(Player seller, int auctionId)
        {
            lock (auctionLock)
            {
                var auction = ActiveAuctions.FirstOrDefault(a => a.AuctionId == auctionId);
                if (auction == null)
                {
                    seller.SendMessage("[AUCTION ERROR] Auction not found.");
                    return;
                }

                if (auction.Seller?.Guid.Full != seller.Guid.Full && auction.SellerGuid != seller.Guid.Full)
                {
                    seller.SendMessage("[AUCTION ERROR] You can only cancel your own auctions.");
                    return;
                }

                using (var context = new ShardDbContext())
                {
                    var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auctionId);
                    if (auctionEntry == null)
                    {
                        seller.SendMessage("[AUCTION ERROR] Auction entry not found in the database.");
                        return;
                    }

                    // ✅ Return item to seller
                    if (!seller.TryCreateInInventoryWithNetworking(auction.Item))
                    {
                        StorePendingAuctionReturn(seller.Guid.Full, auction.Item, "SELLER");
                    }
                    else
                    {
                        seller.SendMessage($"[AUCTION CANCEL] Your auction for {auction.Item.NameWithMaterial} has been canceled and returned to you.");
                        NotifyPreviousBidderAuctionCanceled(auction);
                    }

                    context.AuctionEntries.Remove(auctionEntry);
                    context.SaveChanges();
                }

                ActiveAuctions.Remove(auction);
            }
        }

        public static bool CancelBid(Player bidder, int auctionId)
        {
            lock (auctionLock)
            {
                var auction = ActiveAuctions.FirstOrDefault(a => a.AuctionId == auctionId);
                if (auction == null || DateTime.UtcNow > auction.StartTime.AddSeconds(auction.DurationSeconds))
                {
                    bidder.SendMessage("[AUCTION] Auction not found or has already ended.");
                    return false;
                }

                if (auction.CurrentBidder == null || auction.CurrentBidder.Guid.Full != bidder.Guid.Full)
                {
                    bidder.SendMessage("[AUCTION] You can only cancel your own highest bid.");
                    return false;
                }

                // Refund the bidder
                bidder.BankedEnlightenedCoins += auction.HighestBid;
                bidder.SendMessage($"[AUCTION] Your bid of {auction.HighestBid} Enlightened Coins on {auction.Item.NameWithMaterial} has been canceled and refunded.");

                // Load previous bidder details from auction object (hydrated from DB)
                Player previousBidder = null;
                if (auction.LastBidderGuid.HasValue)
                {
                    previousBidder = PlayerManager.FindByGuid((uint)auction.LastBidderGuid.Value) as Player;
                }

                bool validPreviousBidder = previousBidder != null && auction.PreviousBidAmount > 0 && previousBidder.BankedEnlightenedCoins >= auction.PreviousBidAmount;

                if (validPreviousBidder)
                {
                    // Deduct the reinstated bid amount
                    previousBidder.BankedEnlightenedCoins -= auction.PreviousBidAmount;

                    // Notify reinstated bidder
                    previousBidder.SendMessage($"[AUCTION INFO] Your previous bid of {auction.PreviousBidAmount} Enlightened Coins on {auction.Item.NameWithMaterial} has been reinstated.");
                    NotifyBidderBidCanceled(auction, previousBidder);

                    // Update auction state
                    auction.CurrentBidder = previousBidder;
                    auction.HighestBid = auction.PreviousBidAmount;
                    auction.LastBidderGuid = null;
                    auction.PreviousBidAmount = 0;
                }
                else
                {
                    // No valid previous bidder, clear auction
                    auction.CurrentBidder = null;
                    auction.HighestBid = 0;
                    auction.LastBidderGuid = null;
                    auction.PreviousBidAmount = 0;
                }

                // Save to DB
                using (var context = new ShardDbContext())
                {
                    var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId);
                    if (auctionEntry != null)
                    {
                        auctionEntry.HighestBid = auction.HighestBid;
                        auctionEntry.BuyerGuid = auction.CurrentBidder?.Guid.Full ?? (ulong?)null;
                        auctionEntry.LastBidderGuid = auction.LastBidderGuid;
                        auctionEntry.PreviousBidAmount = auction.PreviousBidAmount;
                        context.SaveChanges();
                    }
                }

                // Notify the seller
                if (auction.CurrentBidder != null)
                {
                    auction.Seller?.SendMessage($"[AUCTION UPDATE] {auction.CurrentBidder.Name} is now the highest bidder on {auction.Item.NameWithMaterial} at {auction.HighestBid} Enlightened Coins after a bid cancellation.");
                }
                else
                {
                    auction.Seller?.SendMessage($"[AUCTION UPDATE] The highest bid on {auction.Item.NameWithMaterial} was canceled. There are no valid bidders remaining.");
                }

                return true;
            }
        }

        public static string ListActiveAuctions()
        {
            lock (auctionLock)
            {
                if (ActiveAuctions.Count == 0)
                    return "No active auctions.";

                return string.Join("\n", ActiveAuctions.Select(a =>
                {
                    double timeLeft = a.DurationSeconds - (DateTime.UtcNow - a.StartTime).TotalSeconds;
                    string formattedTime = TimeSpan.FromSeconds(Math.Max(0, timeLeft)).ToString(@"hh\:mm\:ss");

                    // Pull directly from seller note
                    string sellerNote = !string.IsNullOrWhiteSpace(a.SellerNote) ? a.SellerNote : a.Item.NameWithMaterial;

                    return $"[ID] {a.AuctionId}: listed by {a.SellerName}, [{sellerNote}] [Min Bid] {a.MinBid} EC | [Buyout] {a.BuyoutPrice} EC | [Current Bid] {(a.HighestBid > 0 ? a.HighestBid + " EC" : "No bids")} | Ends in {formattedTime}";
                }));
            }
        }

        public static string SearchAuctionsByType(string searchType)
        {
            lock (auctionLock)
            {
                var filteredAuctions = ActiveAuctions.Where(a => a.ItemType.ToString().Equals(searchType, StringComparison.OrdinalIgnoreCase)).ToList();

                if (filteredAuctions.Count == 0)
                    return $"No active auctions found for category: {searchType}.";

                return string.Join("\n", filteredAuctions.Select(a =>
                {
                    double timeLeft = a.DurationSeconds - (DateTime.UtcNow - a.StartTime).TotalSeconds;
                    string formattedTime = TimeSpan.FromSeconds(Math.Max(0, timeLeft)).ToString(@"hh\:mm\:ss");

                    return $"[ID] {a.AuctionId} listed by {a.SellerName} : [{a.ItemType}] [{a.Item.NameWithMaterial}] [Min Bid]: {a.MinBid} Enlightened Coins | [Buyout]: {a.BuyoutPrice} Enlightened Coins | [Current Bid]: {a.HighestBid} Enlightened Coins | Ends in: {formattedTime}";
                }));
            }
        }

        public static void HandleCharacterDeletion(ulong characterGuid)
        {
            lock (auctionLock)
            {
                // ✅ Handle scenario where the deleted player was the top bidder
                foreach (var auction in ActiveAuctions.Where(a => a.CurrentBidder?.Guid.Full == characterGuid).ToList())
                {
                    Player previousBidder = null;
                    if (auction.LastBidderGuid.HasValue)
                    {
                        previousBidder = PlayerManager.FindByGuid((uint)auction.LastBidderGuid.Value) as Player;
                    }

                    bool validPreviousBidder = previousBidder != null && auction.PreviousBidAmount > 0 && previousBidder.BankedEnlightenedCoins >= auction.PreviousBidAmount;

                    if (validPreviousBidder)
                    {
                        // Deduct EC from reinstated bidder
                        previousBidder.BankedEnlightenedCoins -= auction.PreviousBidAmount;

                        // Update auction
                        auction.CurrentBidder = previousBidder;
                        auction.HighestBid = auction.PreviousBidAmount;
                        auction.LastBidderGuid = null;
                        auction.PreviousBidAmount = 0;

                        // Notify reinstated bidder
                        previousBidder.SendMessage($"[AUCTION NOTICE] Your previous bid of {auction.HighestBid} Enlightened Coins on {auction.Item.NameWithMaterial} has been reinstated after top bidder deletion.");
                        NotifyBidderBidCanceled(auction, previousBidder);

                        // Notify seller
                        auction.Seller?.SendMessage($"[AUCTION UPDATE] {previousBidder.Name} is now the highest bidder on {auction.Item.NameWithMaterial} at {auction.HighestBid} Enlightened Coins due to a bidder deletion.");
                    }
                    else
                    {
                        // No valid previous bidder, reset auction bid state
                        auction.CurrentBidder = null;
                        auction.HighestBid = 0;
                        auction.LastBidderGuid = null;
                        auction.PreviousBidAmount = 0;

                        auction.Seller?.SendMessage($"[AUCTION UPDATE] The highest bidder for {auction.Item.NameWithMaterial} was deleted. No valid bidders remain.");
                    }

                    // Update DB
                    using (var context = new ShardDbContext())
                    {
                        var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId);
                        if (auctionEntry != null)
                        {
                            auctionEntry.BuyerGuid = auction.CurrentBidder?.Guid.Full ?? (ulong?)null;
                            auctionEntry.HighestBid = auction.HighestBid;
                            auctionEntry.LastBidderGuid = auction.LastBidderGuid;
                            auctionEntry.PreviousBidAmount = auction.PreviousBidAmount;
                            context.SaveChanges();
                        }
                    }
                }

                // ✅ Handle scenario where the deleted player was the seller
                foreach (var auction in ActiveAuctions.Where(a => a.SellerGuid == characterGuid).ToList())
                {
                    // Refund bidder if necessary
                    if (auction.CurrentBidder != null && auction.HighestBid > 0)
                    {
                        auction.CurrentBidder.BankedEnlightenedCoins += auction.HighestBid;
                        auction.CurrentBidder.SendMessage($"[AUCTION REFUND] Your bid of {auction.HighestBid} Enlightened Coins on {auction.Item.NameWithMaterial} has been refunded due to seller deletion.");
                    }

                    // Remove from DB
                    using (var context = new ShardDbContext())
                    {
                        var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId);
                        if (auctionEntry != null)
                        {
                            context.AuctionEntries.Remove(auctionEntry);
                            context.SaveChanges();
                        }
                    }

                    // Remove from active auction list
                    ActiveAuctions.Remove(auction);
                }
            }
        }

        public static void NotifySellerAuctionSoldBuyout(AuctionItem auction)
        {
            if (auction.Seller?.DiscordUserId != null)
            {
                // Log the Discord User ID
               // Console.WriteLine($"[AUCTION LOG] Seller Discord User ID: {auction.Seller.DiscordUserId}");

                string message = $"Your auction for {auction.Item.NameWithMaterial} has been sold for {auction.BuyoutPrice} Enlightened Coins!";
                DiscordChatManager.SendDiscordDM(auction.Seller.Name, message, (long)auction.Seller.DiscordUserId);  // Use the seller's Discord user ID for notification
            }
        }

        public static void NotifyBidderAuctionSoldBid(AuctionItem auction)
        {
            if (auction.CurrentBidder?.DiscordUserId != null)
            {
                // Log the Discord User ID of the winner
               // Console.WriteLine($"[AUCTION LOG] Winner Discord User ID: {auction.CurrentBidder.DiscordUserId}");

                string message = $"You have won {auction.Item.NameWithMaterial} for {auction.HighestBid} Enlightened Coins!";
                DiscordChatManager.SendDiscordDM(auction.CurrentBidder.Name, message, (long)auction.CurrentBidder.DiscordUserId);  // Notify the winner
            }
        }

        public static void NotifySellerAuctionSoldBid(AuctionItem auction)
        {
            if (auction.Seller?.DiscordUserId != null)
            {
                // Log the Discord User ID
               // Console.WriteLine($"[AUCTION LOG] Seller Discord User ID: {auction.Seller.DiscordUserId}");

                string message = $"Your auction for {auction.Item.NameWithMaterial} has been sold for {auction.HighestBid} Enlightened Coins!";
                DiscordChatManager.SendDiscordDM(auction.Seller.Name, message, (long)auction.Seller.DiscordUserId);  // Use the seller's Discord user ID for notification
            }
        }

        public static void NotifySellerAuctionExpired(AuctionItem auction)
        {
            if (auction.Seller?.DiscordUserId != null)
            {
                // Log the Discord User ID
              //  Console.WriteLine($"[AUCTION LOG] Seller Discord User ID: {auction.Seller.DiscordUserId}");

                string message = $"Your auction for {auction.Item.NameWithMaterial} has expired without a purchase.";
                DiscordChatManager.SendDiscordDM(auction.Seller.Name, message, (long)auction.Seller.DiscordUserId);
            }
        }

        public static void NotifyBidderOutbid(AuctionItem auction, Player bidder)
        {
            // Ensure LastBidderGuid is set and check for value
            if (auction.LastBidderGuid.HasValue)
            {
                var previousBidder = PlayerManager.FindByGuid((uint)auction.LastBidderGuid.Value);

                if (previousBidder != null && previousBidder.DiscordUserId != null)
                {
                    // Log previous bidder info
                    // Console.WriteLine($"[AUCTION LOG] Previous Bidder Discord User ID: {previousBidder.DiscordUserId}");
                    // Console.WriteLine($"[AUCTION LOG] Previous Bidder Name: {previousBidder.Name}");

                    string message = $"You have been outbid on {auction.Item.NameWithMaterial}. Current bid: {auction.HighestBid} Enlightened Coins.";
                    DiscordChatManager.SendDiscordDM(previousBidder.Name, message, (long)previousBidder.DiscordUserId);  // Notify the previous bidder
                }
            }
        }

        public static void NotifyBidderBidCanceled(AuctionItem auction, Player bidder)
        {
            // Notify the previous bidder, if available
            if (auction.LastBidderGuid.HasValue)
            {
                var previousBidder = PlayerManager.FindByGuid((uint)auction.LastBidderGuid.Value);
                if (previousBidder?.DiscordUserId != null)
                {
                    // Log the previous bidder's Discord User ID
                    //Console.WriteLine($"[AUCTION LOG] Previous Bidder Discord User ID: {previousBidder.DiscordUserId}");

                    string messageToPreviousBidder = $"You have been reinstated as the highest bidder on {auction.Item.NameWithMaterial} with a bid of {auction.PreviousBidAmount} Enlightened Coins.";
                    DiscordChatManager.SendDiscordDM(previousBidder.Name, messageToPreviousBidder, (long)previousBidder.DiscordUserId);
                }
            }
        }

        public static void NotifyPreviousBidderAuctionCanceled(AuctionItem auction)
        {
            if (auction.CurrentBidder != null && auction.CurrentBidder.DiscordUserId != null)
            {
                // Log the current bidder's Discord User ID
                //Console.WriteLine($"[AUCTION LOG] Current Bidder Discord User ID: {auction.CurrentBidder.DiscordUserId}");

                string message = $"The auction for {auction.Item.NameWithMaterial} has been canceled by the seller. Your bid of {auction.HighestBid} Enlightened Coins has been refunded.";
                DiscordChatManager.SendDiscordDM(auction.CurrentBidder.Name, message, (long)auction.CurrentBidder.DiscordUserId);
            }

            // Notify the previous bidder if there's one
            if (auction.LastBidderGuid.HasValue)
            {
                var previousBidder = PlayerManager.FindByGuid((uint)auction.LastBidderGuid.Value);
                if (previousBidder?.DiscordUserId != null)
                {
                    // Log the previous bidder's Discord User ID
                   // Console.WriteLine($"[AUCTION LOG] Previous Bidder Discord User ID: {previousBidder.DiscordUserId}");

                    string messageToPreviousBidder = $"The auction for {auction.Item.NameWithMaterial} has been canceled. You were previously the highest bidder with {auction.PreviousBidAmount} Enlightened Coins.";
                    DiscordChatManager.SendDiscordDM(previousBidder.Name, messageToPreviousBidder, (long)previousBidder.DiscordUserId);
                }
            }
        }


        public class AuctionItem
        {
            public int AuctionId { get; set; }
            public WorldObject Item { get; set; }
            public Player Seller { get; set; }
            public string SellerName { get; set; }
            public ulong SellerGuid { get; set; }
            public string SellerIp { get; set; }
            public Player CurrentBidder { get; set; }
            public ulong? LastBidderGuid { get; set; }
            public int MinBid { get; set; }
            public int BuyoutPrice { get; set; }
            public int HighestBid { get; set; }
            public int DurationSeconds { get; set; } // Duration of the auction in seconds
            public DateTime StartTime { get; set; } // When the auction expires
            public Stack<(ulong BidderGuid, int Amount)> PreviousBids { get; set; } = new Stack<(ulong, int)>();
            public string ItemType { get; set; }
            public int PreviousBidAmount { get; set; }
            public string SellerNote { get; set; }

        }

        public class AuctionReturnItem
        {
            public int ReturnId { get; set; }
            public ulong SellerGuid { get; set; }
            public ulong BuyerGuid { get; set; }
            public WorldObject Item { get; set; }
            public DateTime ReturnDate { get; set; }
        }
    }
}
