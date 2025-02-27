using System;
using System.Linq;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Common;
using ACE.Server.Entity.Actions;
using System.Runtime.CompilerServices;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.Entity
{
    public class Prestige
    {
        public static void HandlePrestige(Player player)
        {
            if (!VerifyRequirements(player))
                return;

            DequipAllItems(player);
            RemoveFromFellowships(player);

            player.SendMotionAsCommands(MotionCommand.MarketplaceRecall, MotionStance.NonCombat);

            var startPos = new ACE.Entity.Position(player.Location);
            ActionChain prestigeChain = new ActionChain();
            prestigeChain.AddDelaySeconds(14);

            // Begin Prestige Process
            player.IsBusy = true;
            prestigeChain.AddAction(player, () =>
            {
                player.IsBusy = false;
                var endPos = new ACE.Entity.Position(player.Location);
                if (startPos.SquaredDistanceTo(endPos) > Player.RecallMoveThresholdSq)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You have moved too far during the prestige animation!", ChatMessageType.Broadcast));
                    return;
                }

                player.ThreadSafeTeleportOnDeath();
                RemoveAbilities(player);
                AddPrestigeBonuses(player);

                player.SaveBiotaToDatabase();
            });

            // Start the action chain
            prestigeChain.EnqueueChain();
        }

        public static bool VerifyRequirements(Player player)
        {
            if (player.Enlightenment < 10)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[PRESTIGE] You must be Enlightenment Level 10 or higher to Prestige.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.GetFreeInventorySlots() < 25)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[PRESTIGE] You must have at least 25 free inventory slots for Prestige.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.HasVitae)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[PRESTIGE] You cannot Prestige while under a Vitae Penalty.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.Teleporting || player.TooBusyToRecall || player.IsAnimating || player.IsInDeathProcess)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[PRESTIGE] Cannot Prestige while teleporting or busy. Try again later.", ChatMessageType.System));
                return false;
            }

            return true;
        }

        public static void RemoveFromFellowships(Player player)
        {
            player.FellowshipQuit(false);
        }

        public static void DequipAllItems(Player player)
        {
            var equippedObjects = player.EquippedObjects.Keys.ToList();
            foreach (var equippedObject in equippedObjects)
                player.HandleActionPutItemInContainer(equippedObject.Full, player.Guid.Full, 0);
        }

        public static void RemoveAbilities(Player player)
        {
            RemoveSkills(player);
            RemoveLevel(player);
            RemoveAllSpells(player);
        }

        public static void RemoveLevel(Player player)
        {
            player.TotalExperience = 0;
            player.Level = 1;

            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(player, PropertyInt64.TotalExperience, 0));
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.Level, 1));
        }

        public static void RemoveAllSpells(Player player)
        {
            player.EnchantmentManager.DispelAllEnchantments();
        }

        public static void RemoveSkills(Player player)
        {
            var propertyCount = Enum.GetNames(typeof(Skill)).Length;
            for (var i = 1; i < propertyCount; i++)
            {
                var skill = (Skill)i;
                player.ResetSkill(skill, false);
            }

            player.AvailableExperience = 0;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(player, PropertyInt64.AvailableExperience, 0));
        }

        public static void AddPrestigeBonuses(Player player)
        {
            player.PrestigeLevel += 1;
            player.Enlightenment = - 10; // - 10 enlightenment levels

            // Announce Prestige Level Up
            var msg = $"{player.Name} has achieved Prestige Level {player.PrestigeLevel}!";
            PlayerManager.BroadcastToAll(new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast));
            DiscordChatManager.SendDiscordMessage(player.Name, msg, ConfigManager.Config.Chat.GeneralChannelId);

            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[PRESTIGE] Congratulations! You are now Prestige Level {player.PrestigeLevel}.", ChatMessageType.System));
            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[PRESTIGE] Quest XP Bonus: {player.PrestigeQuestMultiplier:F2}x", ChatMessageType.System));
            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[PRESTIGE] Enlightenment XP Bonus: {player.PrestigeEnlightenmentMultiplier:F2}x", ChatMessageType.System));
        }
    }
}

