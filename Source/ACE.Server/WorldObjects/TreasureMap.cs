using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using Position = ACE.Entity.Position;

namespace ACE.Server.WorldObjects
{
    public partial class TreasureMap : GenericObject
    {
        public TreasureMap(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
        }

        public TreasureMap(Biota biota) : base(biota)
        {
        }

        private List<uint> TreasureChests = new List<uint>()
        {
            90000112,
            90000113,
            90000116,
        };

        public static WorldObject TryCreateTreasureMap(Weenie creatureWeenie)
        {
            if (creatureWeenie.WeenieType != WeenieType.Creature)
                return null;

            var creature = WorldObjectFactory.CreateNewWorldObject(creatureWeenie) as Creature;

            if (creature == null)
                return null;

            var treasure = TryCreateTreasureMap(creature);  // Call the new simplified version for the treasure map creation
            creature.Destroy();

            return treasure;
        }

        public static WorldObject TryCreateTreasureMap(Creature creature)
        {
            if (creature == null)
                return null;

            // Retry mechanism to keep trying until valid coordinates are found
            float randomLatitude = 0, randomLongitude = 0;
            bool validCoordinates = false;

            // Loop to keep retrying until valid coordinates are found
            for (int attempt = 0; attempt < 10; attempt++)
            {
                // Generate random coordinates between -90 and 90 for both latitude and longitude
                randomLatitude = ThreadSafeRandom.Next(-90, 90);
                randomLongitude = ThreadSafeRandom.Next(-90, 90);

                //Console.WriteLine($"[DEBUG] Attempting to generate coordinates: {randomLatitude}, {randomLongitude}");

                // Validate coordinates
                if (AreCoordinatesValid(randomLatitude, randomLongitude))
                {
                    validCoordinates = true;
                    break; // Valid coordinates found, break out of the loop
                }
                else
                {
                    //Console.WriteLine("[DEBUG] Invalid coordinates, retrying...");
                }
            }

            // If no valid coordinates were found after retries, return null
            if (!validCoordinates)
            {
                //Console.WriteLine("[DEBUG] No valid coordinates found after retries.");
                return null;
            }

            // Create the treasure map world object
            var wo = WorldObjectFactory.CreateNewWorldObject((uint)Factories.Enum.WeenieClassName.treasureMap);
            if (wo == null)
                return null;

            // Set the map's name, description, and coordinates
            wo.Name = $"{creature.Name}'s Treasure Map";
            wo.LongDesc = $"This map was found in the corpse of a level {creature.Level} {creature.Name}. It leads to a hidden treasure.";

            // Assign the random coordinates to the treasure map
            wo.EWCoordinates = randomLongitude;
            wo.NSCoordinates = randomLatitude;

            return wo;
        }

        public static bool AreCoordinatesValid(float latitude, float longitude)
        {
            // Generate a Position object with the given latitude and longitude
            var position = new Position((float)latitude, (float)longitude, null);

            // Convert the position to map coordinates
            var mapCoords = position.GetMapCoords();

            if (mapCoords == null)
            {
                //Console.WriteLine($"[DEBUG] Invalid coordinates: Latitude {latitude}, Longitude {longitude}. Coordinates are outside the map bounds.");
                return false;  // If the coordinates are not valid map coordinates, return false
            }

            // Optionally, you can add additional checks for specific coordinate ranges
            // For example, ensuring the coordinates fall within the playable area:
            // Latitude: -102 to +102 map units, Longitude: -102 to +102 map units
            if (mapCoords.Value.X < -102 || mapCoords.Value.X > 102 || mapCoords.Value.Y < -102 || mapCoords.Value.Y > 102)
            {
                //Console.WriteLine($"[DEBUG] Coordinates are out of bounds on the map. Coordinates: {mapCoords.Value.X}, {mapCoords.Value.Y}");
                return false;
            }

            // Use LScape.get_landcell to check if the generated coordinates are blocked (by water, building, etc.)
            var landcell = LScape.get_landcell(position.GetCell(), null) as SortCell;
            if (landcell == null || landcell.has_building() || landcell.CurLandblock.WaterType == LandDefs.WaterType.EntirelyWater)
            {
                //Console.WriteLine($"[DEBUG] Coordinates are blocked by water or building at Latitude {latitude}, Longitude {longitude}.");
                return false;  // Coordinates are blocked, return false
            }

            // Ensure the location is walkable
            var pos = new Physics.Common.Position();
            var location = pos.ACEPosition();
            if (!location.IsWalkable())
            {
               // Console.WriteLine($"[DEBUG] Coordinates are not walkable at Latitude {latitude}, Longitude {longitude}.");
                return false;  // If the coordinates are not walkable, return false
            }

            // Coordinates are valid, return true
            //Console.WriteLine($"[DEBUG] Valid coordinates found: Latitude {latitude}, Longitude {longitude}.");
            return true;
        }

        public static bool WieldingShovel(Player player)
        {
            return player.QuestManager.GetCurrentSolves("WieldingShovel") >= 1;
        }

        // Method to give random loot to player
        private void GiveLootToPlayer(Player player)
        {
            //Console.WriteLine("[DEBUG] Giving loot to player...");

            // List of possible loot items to drop along with their quantities (Tuple: Item ID, Quantity)
            var lootItems = new List<(uint, int)>
    {
        (34276, 100),  // Empyrean Trinkets
        (34277, 50), // Falatacot Trinkets
        (300019, 50), // Blue powder
        (42644, 100), // Red Powder
        (300004, 100), // Enlightened Coins
        (20630, 250), // MMDS
    };

            // Check if the lootItems list is not empty
            if (lootItems.Count == 0)
            {
               // Console.WriteLine("[DEBUG] Loot items list is empty, no loot to give.");
                return;
            }

            // Select a random loot item from the list (We use Next(min, max) to pick the random item)
            var randomIndex = ThreadSafeRandom.Next(0, lootItems.Count - 1);  // Get a random index
            //Console.WriteLine("[DEBUG] Random index: " + randomIndex);

            if (randomIndex < 0 || randomIndex >= lootItems.Count)
            {
                //Console.WriteLine("[DEBUG] Random index out of range. Loot items list size: " + lootItems.Count);
                return;
            }

            var selectedLoot = lootItems[randomIndex];  // Get the selected loot item and quantity
            uint lootItemID = selectedLoot.Item1;
            int quantity = selectedLoot.Item2;

            // Attempt to create the loot in the player's inventory
            WorldObject loot = WorldObjectFactory.CreateNewWorldObject(lootItemID);

            if (loot != null)
            {
                // Set the loot quantity if needed (assuming the loot supports quantity)
                loot.SetStackSize(quantity);  // This assumes that the WorldObject has a method to set the quantity

                // Try adding the loot to the player's inventory
                if (!player.TryCreateInInventoryWithNetworking(loot))  // Pass quantity as argument
                {
                    //Console.WriteLine("[DEBUG] Failed to add loot to player's inventory.");
                    loot.Destroy();
                }
                else
                {
                    // Send a message to the player confirming the loot received
                    var lootName = loot.Name ?? "Unknown Item";  // Fallback if the item name is null
                    var lootMessage = $"You have received {quantity} of {lootName}";
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(lootMessage, ChatMessageType.Broadcast));

                    //Console.WriteLine("[DEBUG] Loot successfully added to player's inventory.");
                }
            }
            else
            {
                //Console.WriteLine("[DEBUG] Failed to create loot object.");
            }
        }


        public override void ActOnUse(WorldObject activator)
        {
            //Console.WriteLine($"[DEBUG] ActOnUse called for {this.Name} by {activator.Name}");
            if (!(activator is Player player))
                return;

            // Check if player is wielding a shovel before proceeding
            if (!WieldingShovel(player))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("You must be wielding Tiny Tina's shovel to search for treasure.", ChatMessageType.Broadcast));
                return;
            }

            //Console.WriteLine("Treasure map used by player: " + player.Name);  // Debug log to verify ActOnUse is triggered

            player.SyncLocation(null);
            player.EnqueueBroadcast(new GameMessageUpdatePosition(player));

            var position = new Position((float)(NSCoordinates ?? 0f), (float)(EWCoordinates ?? 0f), null);
            //Console.WriteLine($"Treasure map coordinates: {position.CellX}, {position.CellY}");
            position.AdjustMapCoords();

            var distance = Math.Abs(position.GetLargestOffset(player.Location));
           // Console.WriteLine($"Distance from player: {distance}");

            if (distance > 5000)
            {
                //Console.WriteLine("[DEBUG] Distance is greater than 5000, starting animation...");
                var animTime = DatManager.PortalDat.ReadFromDat<MotionTable>(player.MotionTableId).GetAnimationLength(MotionCommand.Reading);
                var actionChain = new ActionChain();
                actionChain.AddAction(player, () => player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance, MotionCommand.Reading)));
                actionChain.AddDelaySeconds(animTime + 1);
                actionChain.AddAction(player, () =>
                {
                    string directions;
                    string name;
                    var entryLandblock = DatabaseManager.World.GetLandblockDescriptionsByLandblock((ushort)position.Landblock).FirstOrDefault();
                    if (entryLandblock != null)
                    {
                        name = entryLandblock.Name;
                        directions = $"{entryLandblock.Directions} {entryLandblock.Reference}";
                    }
                    else
                    {
                        name = $"an unknown location({position.Landblock})";
                        directions = "";
                    }

                    var message = $"The treasure map points to {name} {directions}.";
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));

                    player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance));
                });
                actionChain.EnqueueChain();
            }
            else
            {
                //Console.WriteLine("[DEBUG] Distance is within 5000, checking damage status...");

                if (distance > 2 || !DamageMod.HasValue)
                {
                    //Console.WriteLine("[DEBUG] DamageMod is null or distance > 2, triggering scan horizon animation...");
                    var animTime = DatManager.PortalDat.ReadFromDat<MotionTable>(player.MotionTableId).GetAnimationLength(MotionCommand.ScanHorizon);
                    var actionChain = new ActionChain();
                    actionChain.AddAction(player, () => player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance, MotionCommand.ScanHorizon)));
                    actionChain.AddDelaySeconds(animTime);
                    actionChain.AddAction(player, () =>
                    {
                        player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance));

                        var direction = player.Location.GetCardinalDirectionsTo(position);

                        player.Session.Network.EnqueueSend(new GameMessageSystemChat($"The treasure map points {(direction == "" ? "at" : $"{direction} of")} your current location.", ChatMessageType.Broadcast));

                        if (distance <= 2 && !DamageMod.HasValue)
                        {
                            //Console.WriteLine("[DEBUG] DamageMod set to 1, triggering cheer animation...");
                            DamageMod = 1;
                            player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance, MotionCommand.Cheer));
                            player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance));
                        }
                        else
                            DamageMod = null;
                    });
                    actionChain.EnqueueChain();
                }
                else
                {
                    if (!Damage.HasValue)
                        Damage = 0;

                    //Console.WriteLine("[DEBUG] Damage counter: " + Damage);
                    if (Damage < 7)
                    {
                        string msg;
                        if (Damage == 0)
                        {
                            msg = "You start to dig for treasure!";
                            //Console.WriteLine("[DEBUG] Starting to dig for treasure!");
                        }
                        else
                        {
                            msg = "You continue to dig for treasure!";
                            //Console.WriteLine("[DEBUG] Continuing to dig for treasure!");
                        }

                        Damage++;

                        var animTime = DatManager.PortalDat.ReadFromDat<MotionTable>(player.MotionTableId).GetAnimationLength(MotionCommand.Pickup);
                        var actionChain = new ActionChain();
                        actionChain.AddAction(player, () => player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance, MotionCommand.PointDown)));
                        actionChain.AddDelaySeconds(animTime);
                        actionChain.AddAction(player, () =>
                        {
                            player.EnqueueBroadcast(new GameMessageSound(player.Guid, Sound.HitLeather1, 1.0f));
                            player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance));
                            player.EnqueueBroadcast(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
                            var visibleCreatures = player.PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeCreature();
                            foreach (var creature in visibleCreatures)
                            {
                                if (!creature.IsDead && !creature.IsAwake)
                                    player.AlertMonster(creature);
                            }
                        });
                        actionChain.EnqueueChain();
                    }
                    else
                    {
                        //Console.WriteLine("[DEBUG] Damage counter has reached 7, creating treasure chest...");

                        var animTime = DatManager.PortalDat.ReadFromDat<MotionTable>(player.MotionTableId).GetAnimationLength(MotionCommand.Pickup);
                        var actionChain = new ActionChain();
                        actionChain.AddAction(player, () => player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance, MotionCommand.Pickup)));
                        actionChain.AddDelaySeconds(animTime);
                        actionChain.AddAction(player, () =>
                        {
                            player.EnqueueBroadcast(new GameMessageSound(player.Guid, Sound.HitPlate1, 1.0f));
                            player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance));
                            player.EnqueueBroadcast(new GameMessageSystemChat("You found the buried treasure!", ChatMessageType.Broadcast));

                            // After the message is shown, give loot to the player
                            GiveLootToPlayer(player);  // Call to give loot directly to the player's inventory

                            // Remove the treasure map from the player's inventory
                            if (!player.TryConsumeFromInventoryWithNetworking(this, 1))
                            {
                                //Console.WriteLine("[DEBUG] Failed to remove treasure map from player's inventory.");
                            }
                            else
                            {
                               // Console.WriteLine("[DEBUG] Treasure map successfully removed from player's inventory.");
                            }
                        });
                        actionChain.EnqueueChain();
                    }
                }
            }
        }
    }
}
