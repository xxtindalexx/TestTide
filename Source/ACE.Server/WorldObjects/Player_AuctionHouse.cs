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
using Org.BouncyCastle.Cms;

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
                    MinBid = entry.MinBid,
                    BuyoutPrice = entry.BuyoutPrice ?? 0,
                    HighestBid = entry.HighestBid,
                    CurrentBidder = entry.BuyerGuid != null
                        ? PlayerManager.FindByGuid((uint)entry.BuyerGuid) as Player
                        : null,
                    StartTime = entry.StartTime,
                    DurationSeconds = entry.DurationSeconds
                });

               // Console.WriteLine($"[AUCTION] Successfully loaded auction {entry.Id}.");
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

        public static void ListAuction(Player seller, WorldObject item, int minBid, int buyoutPrice, int durationMinutes)
        {
            lock (auctionLock)
            {
                var auction = new AuctionItem
                {
                    AuctionId = ActiveAuctions.Count + 1, // Sequential ID
                    Item = item,
                    Seller = seller,
                    MinBid = minBid,
                    BuyoutPrice = buyoutPrice,
                    HighestBid = 0,
                    DurationSeconds = durationMinutes * 60, // Store countdown time
                    StartTime = DateTime.UtcNow // Track when it was created
                };

                ActiveAuctions.Add(auction);
                SaveAuctionToDB(auction);

                seller.SendMessage($"[AUCTION LISTING] You have listed {item.NameWithMaterial} for auction with a minimum bid of {minBid} and a buyout price of {buyoutPrice} Enlightened Coins.");
            }
        }

        public static void SaveAuctionToDB(AuctionItem auction)
        {
            using (var context = new ShardDbContext())
            {
                var auctionEntry = new AuctionEntry
                {
                    ItemGuid = auction.Item.Guid.Full,
                    SellerGuid = auction.Seller.Guid.Full,
                    MinBid = auction.MinBid,
                    BuyoutPrice = auction.BuyoutPrice,
                    HighestBid = auction.HighestBid,
                    BuyerGuid = auction.CurrentBidder?.Guid.Full,
                    DurationSeconds = auction.DurationSeconds,
                    StartTime = DateTime.UtcNow // Ensure a valid timestamp
                };

                context.AuctionEntries.Add(auctionEntry);
                context.SaveChanges();
                auction.AuctionId = auctionEntry.Id;
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

                // ✅ Prevent seller from bidding on their own auction
                if (auction.Seller != null && auction.Seller.Guid.Full == bidder.Guid.Full)
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

                // Refund the previous highest bidder
                if (auction.CurrentBidder != null)
                {
                    auction.CurrentBidder.BankedEnlightenedCoins += auction.HighestBid;
                    auction.CurrentBidder.SendMessage($"[AUCTION WARN] You have been outbid on {auction.Item.NameWithMaterial}. Your {auction.HighestBid} Enlightened Coins have been refunded.");
                }

                // **Bid is at or above buyout price? Treat it as a buyout**
                if (auction.BuyoutPrice > 0 && bidAmount >= auction.BuyoutPrice)
                {
                    bidder.SendMessage($"[AUCTION PURCHASE] Your bid matches/exceeds the buyout price. Purchasing item immediately...");
                    BuyoutItem(bidder, auctionId);
                    return;
                }

                // Deduct coins and update auction
                bidder.BankedEnlightenedCoins -= bidAmount;
                auction.HighestBid = bidAmount;
                auction.CurrentBidder = bidder;

                // Update database
                using (var context = new ShardDbContext())
                {
                    var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId);
                    if (auctionEntry != null)
                    {
                        auctionEntry.HighestBid = bidAmount;
                        auctionEntry.BuyerGuid = bidder.Guid.Full;
                        context.SaveChanges();
                    }
                }

                // **Notify the seller that they have a new highest bid**
                auction.Seller?.SendMessage($"[AUCTION ATTENTION] A new highest bid of {bidAmount} Enlightened Coins has been placed on {auction.Item.NameWithMaterial}!");

                bidder.SendMessage($"[AUCTION BID] You have placed a bid of {bidAmount} Enlightened Coins on {auction.Item.NameWithMaterial}.");
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
                    //Console.WriteLine("[AUCTION ERROR] Seller GUID is invalid. Unable to process payment.");
                    buyer.SendMessage("[AUCTION ERROR] Error processing transaction. Contact support.");
                    return;
                }

                var sellerOnline = PlayerManager.GetOnlinePlayer((uint)sellerGuid);

                if (sellerOnline != null) // ✅ Seller is ONLINE
                {
                    sellerOnline.BankedEnlightenedCoins += auction.BuyoutPrice;
                    sellerOnline.SendMessage($"[AUCTION SOLD] Your item {auction.Item.NameWithMaterial} was sold for {auction.BuyoutPrice} Enlightened Coins.");
                }
                else // ✅ Seller is OFFLINE - Store pending payment in DB
                {
                   // Console.WriteLine($"[AUCTION] Storing {auction.BuyoutPrice} Enlightened Coins payment for offline seller (GUID: {sellerGuid}).");

                    StorePendingAuctionPayment(sellerGuid, auction.BuyoutPrice);
                }

                // Grant item to buyer
                buyer.TryCreateInInventoryWithNetworking(auction.Item);

                // Remove auction entry
                ActiveAuctions.Remove(auction);
                RemoveAuctionFromDB(auction);

                // Notify the buyer
                buyer.SendMessage($"[AUCTION WON] You have purchased {auction.Item.NameWithMaterial} for {auction.BuyoutPrice} Enlightened Coins.");
            }
        }

        public static void ExpireAuctions()
        {
            lock (auctionLock)
            {
                using (var context = new ShardDbContext())
                {
                   // Console.WriteLine("[AUCTION] Running ExpireAuctions check...");

                    var nowUtc = DateTime.UtcNow;
                    var expiredAuctions = ActiveAuctions
                        .Where(a => (nowUtc - a.StartTime).TotalSeconds >= a.DurationSeconds)
                        .ToList();

                   // Console.WriteLine($"[AUCTION] Found {expiredAuctions.Count} expired auctions.");

                    foreach (var auction in expiredAuctions)
                    {
                        try
                        {
                            ulong sellerGuid = auction.Seller?.Guid.Full ?? auction.SellerGuid;
                            ulong buyerGuid = auction.CurrentBidder?.Guid.Full ?? 0;

                            var seller = PlayerManager.GetOnlinePlayer((uint)sellerGuid);
                            var buyer = PlayerManager.GetOnlinePlayer((uint)buyerGuid);

                            if (auction.HighestBid > 0) // **Item was bought**
                            {
                                if (seller != null)
                                {
                                    seller.BankedEnlightenedCoins += auction.HighestBid;
                                    seller.SendMessage($"[AUCTION SOLD] Your auction for {auction.Item.NameWithMaterial} was sold for {auction.HighestBid} Enlightened Coin(s).");
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
                                    }
                                    else
                                    {
                                        buyer.SendMessage($"[AUCTION WON] Congratulations! You won {auction.Item.NameWithMaterial}.");
                                    }
                                }
                                else
                                {
                                    StorePendingAuctionReturn(buyerGuid, auction.Item, "BUYER", buyerGuid);
                                }
                            }
                            else
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
                                    }
                                }
                                else
                                {
                                    StorePendingAuctionReturn(sellerGuid, auction.Item, "SELLER");
                                }
                            }

                            context.AuctionEntries.Remove(context.AuctionEntries.FirstOrDefault(a => a.Id == auction.AuctionId));
                            ActiveAuctions.Remove(auction);
                        }
                        catch (Exception ex)
                        {
                           // Console.WriteLine($"[AUCTION ERROR] Exception processing auction ID {auction.AuctionId}: {ex.Message}");
                        }
                    }

                    context.SaveChanges();
                   // Console.WriteLine("[AUCTION] ExpireAuctions process complete.");
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

        public static void PreviewAuctionItem(Player player, int auctionId)
        {
            lock (auctionLock)
            {
                var auction = ActiveAuctions.FirstOrDefault(a => a.AuctionId == auctionId);
                if (auction == null)
                {
                    player.SendMessage("[AUCTION ERROR] Auction not found.");
                    return;
                }

                // Grant a temporary version of the item to the player
                var previewItem = auction.Item; // Use the same item reference, just like Buyout
                previewItem.SetProperty(PropertyInt.CreationTimestamp, (int)Time.GetUnixTime());

                // Set a decay timer (5 minutes = 300 seconds)
                previewItem.SetProperty(PropertyInt.RemainingLifespan, 300);
                previewItem.SetProperty(PropertyInt.Lifespan, 300);

                // Add to player's inventory (same as Buyout)
                if (!player.TryCreateInInventoryWithNetworking(previewItem))
                {
                    player.SendMessage("[AUCTION ERROR] Your inventory is full. Unable to provide auction preview.");
                    return;
                }

                player.SendMessage($"[AUCTION PREVIEW] A preview of {auction.Item.NameWithMaterial} has been placed in your inventory. It will expire in 5 minutes.");
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

                int auctionDbId = auction.AuctionId;
                ulong highestBidderGuid = auction.CurrentBidder?.Guid.Full ?? 0;
                int refundedAmount = auction.HighestBid;

                using (var context = new ShardDbContext())
                {
                    var auctionEntry = context.AuctionEntries.FirstOrDefault(a => a.Id == auctionDbId);
                    if (auctionEntry == null)
                    {
                        seller.SendMessage("[AUCTION ERROR] Auction entry not found in the database.");
                        return;
                    }

                    // **Refund highest bidder if there was one**
                    if (refundedAmount > 0 && highestBidderGuid > 0)
                    {
                        var bidder = PlayerManager.GetOnlinePlayer((uint)highestBidderGuid);

                        if (bidder != null) // **Bidder is ONLINE**
                        {
                            bidder.BankedEnlightenedCoins += refundedAmount;
                            bidder.SendMessage($"[AUCTION REFUND] Your bid of {refundedAmount} Enlightened Coin(s) on {auction.Item.NameWithMaterial} has been refunded.");
                        }
                        else // **Bidder is OFFLINE, store refund**
                        {
                            var refundEntry = new AuctionRefundEntry
                            {
                                BidderGuid = highestBidderGuid,
                                RefundedAmount = refundedAmount,
                                RefundDate = DateTime.UtcNow
                            };

                            context.AuctionRefunds.Add(refundEntry);
                        }
                    }

                    // **Return the item to the seller**
                    if (!seller.TryCreateInInventoryWithNetworking(auction.Item))
                    {
                        StorePendingAuctionReturn(seller.Guid.Full, auction.Item, "SELLER");
                    }
                    else
                    {
                        seller.SendMessage($"[AUCTION CANCEL] Your auction for {auction.Item.NameWithMaterial} has been canceled and returned to you.");
                    }

                    context.AuctionEntries.Remove(auctionEntry);
                    context.SaveChanges();
                }

                ActiveAuctions.Remove(auction);
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
                    var timeLeft = a.DurationSeconds - (DateTime.UtcNow - a.StartTime).TotalSeconds;
                    var formattedTime = TimeSpan.FromSeconds(Math.Max(0, timeLeft)).ToString(@"hh\:mm\:ss");

                    return $"[ID] {a.AuctionId}: [{a.Item.NameWithMaterial}] [Min Bid]: {a.MinBid} Enlightened Coins | [Buyout]: {a.BuyoutPrice} Enlightened Coins |  [Current Bid]: {a.HighestBid} Enlightened Coins | Ends in: {formattedTime}";
                }));
            }
        }

    }

    public class AuctionItem
    {
        public int AuctionId { get; set; }
        public WorldObject Item { get; set; }
        public Player Seller { get; set; }
        public ulong SellerGuid { get; set;}
        public Player CurrentBidder { get; set; }
        public int MinBid { get; set; }
        public int BuyoutPrice { get; set; }
        public int HighestBid { get; set; }
        public int DurationSeconds { get; set; } // Duration of the auction in seconds
        public DateTime StartTime { get; set; } // When the auction expires
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
