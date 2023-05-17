using System;
using System.Collections.Generic;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using System.Linq;
using ACE.Entity.Enum.Properties;
using ACE.Database.Models.Auth;
using System.Xml.Linq;
using Lifestoned.DataModel.DerethForever;
using MySqlX.XDevAPI.Common;

namespace ACE.Server.Command.Handlers
{
    public static class PlayerCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        //custom commands
        [CommandHandler("bank", AccessLevel.Player, CommandHandlerFlag.None, "Handles Banking Operations", "")]
        public static void HandleBank(Session session, params string[] parameters)
        {
            if (parameters.Length == 0)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"---------------------------", ChatMessageType.Broadcast));
                session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] To use The Bank you must issue one of the commands listed below.", ChatMessageType.System));
                session.Network.EnqueueSend(new GameMessageSystemChat($"/bank Deposit to deposit all pyreals and luminance, or specify pyreals or luminance and an amount", ChatMessageType.System));
                session.Network.EnqueueSend(new GameMessageSystemChat($"/bank Withdraw Pyreals 100 to withdraw 100 pyreals. Groups of 250000 will be exchanged for MMDs", ChatMessageType.System));
                session.Network.EnqueueSend(new GameMessageSystemChat($"/bank Balance to see balance. All bank commands and keywords can be shortened to their first letter", ChatMessageType.System));
                return;
            }

            int iType = 0;
            int amount = -1;

            if (parameters.Count() >= 2)
            {
                if (parameters[1] == "pyreals" || parameters[1] == "p")
                {
                    //pyreals
                    iType = 1;
                }
                if (parameters[1] == "luminance" || parameters[1] == "l")
                {
                    //lum
                    iType = 2;
                }
                if (parameters[1] == "notes" || parameters[1] == "n")
                {
                    //trade notes
                    iType= 3;
                }
            }

            if (parameters.Count() == 3)
            {
                if (!int.TryParse(parameters[2], out amount))
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat($"Check the amount parameter, it needs to be a number.", ChatMessageType.System));
                }
            }

            if (parameters[0] == "deposit" || parameters[0] == "d")
            {
                //deposit
                if (parameters.Count() == 1) //only means all
                {
                    //deposit all
                    session.Player.DepositPyreals();
                    session.Player.DepositLuminance();

                    session.Network.EnqueueSend(new GameMessageSystemChat($"Deposited all Pyreals and Luminance!", ChatMessageType.System));
                }
                switch (iType)
                {
                    case 1:
                        //deposit pyreals
                        if (amount > 0)
                        {
                            session.Player.DepositPyreals(amount);                            
                        }
                        else
                        {
                            session.Player.DepositPyreals();
                            session.Network.EnqueueSend(new GameMessageSystemChat($"Deposited all pyreals!", ChatMessageType.System));
                        }
                        break;
                    case 2:
                        //deposit lum
                        if (amount > 0)
                        {
                            session.Player.DepositLuminance(amount);                            
                        }
                        else
                        {
                            session.Player.DepositLuminance();
                            session.Network.EnqueueSend(new GameMessageSystemChat($"Deposited all luminance!", ChatMessageType.System));
                        }
                        break;
                    case 3:
                        //deposit notes
                        session.Player.DepositTradeNotes();
                        session.Network.EnqueueSend(new GameMessageSystemChat($"Deposited all trade notes!", ChatMessageType.System));
                        break;
                    default:
                        break;
                }
            }

            if (parameters[0] == "withdraw" || parameters[0] == "w")
            {
                //withdraw
                switch (iType)
                {
                    case 1:
                        //withdraw pyreals
                        session.Player.WithdrawPyreals(amount);
                        break;
                    case 2:
                        //withdraw lum
                        session.Player.WithdrawLuminance(amount); 
                        break;
                    default:
                        break;
                }
            }

            if (parameters[0] == "transfer" || parameters[0] == "t")
            {
                //transfer
                switch (iType)
                {
                    case 1:
                        //transfer pyreals
                        break;
                    case 2:
                        //transfer lum
                        break;
                    default:
                        break;
                }
            }
            if (parameters[0] == "balance" || parameters[0] == "b")
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Your balances are:", ChatMessageType.System));
                session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Pyreals: {session.Player.BankedPyreals}", ChatMessageType.System));
                session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Luminance: {session.Player.BankedLuminance}", ChatMessageType.System));
            }
        }

        [CommandHandler("enlighten", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Handles Enlightenment", "")]
        public static void HandleEnlightenment(Session session, params string[] parameters)
        {
            //if(!session.Player.ConfirmationManager.EnqueueSend(new Confirmation_YesNo(session.Player.Guid, session.Player.Guid, "Enlightenment"), "Are you certain that you'd like to Englighten? You will lose all unspent experience, unspent Luminance not in your bank, and all skills. You will retain all attributes."))
            //    return;
            var message = "Are you certain that you'd like to Englighten? You will lose all unspent experience, unspent Luminance not in your bank, and all skills. You will retain all attributes.";
            var confirm = session.Player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(session.Player.Guid, () => Enlightenment.HandleEnlightenment(session.Player)), message);
            
        }

        [CommandHandler("bonus", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Handles Experience Checks", "Leave blank for level, pass first 3 letters of attribute for specific attribute cost")]
        public static void HandleMultiplier (Session session, params string[] paramters)
        {
            session.Player.QuestCompletionCount = session.Player.Account.GetCharacterQuestCompletions();
            var qb = session.Player.GetQuestCountXPBonus();
            var eq = session.Player.GetXPAndLuminanceModifier(XpType.Kill);
            var en = session.Player.GetEnglightenmentXPBonus();

            session.Network.EnqueueSend(new GameMessageSystemChat($"[BONUS] Your XP multiplier from Quests is: {qb-1:P}", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BONUS] Your XP multiplier from Equipment is: {eq-1:P}", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BONUS] Your XP multiplier from Enlightenment is: {en-1:P}", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BONUS] Your Total XP multiplier is: {(qb-1)+(eq-1)+(en-1):P}", ChatMessageType.System));
        }

        [CommandHandler("xp", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Handles Experience Checks", "Leave blank for level, pass first 3 letters of attribute for specific attribute cost")]
        public static void HandleExperience(Session session, params string[] parameters)
        {
            if (parameters.Count() == 0)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"[XP] Your XP to next level is: {session.Player.GetRemainingXP():N0}", ChatMessageType.System));
            }
            if (parameters.Count() == 1)
            {
                ulong xp = 0; string AttrName = ""; bool success = false;
                //check attribute costs
                switch (parameters[0])
                {
                    case "str": xp = GetOrRaiseAttrib(session, 1, PropertyAttribute.Strength, out AttrName, false, out success);
                        break;
                    case "end": xp = GetOrRaiseAttrib(session, 1, PropertyAttribute.Endurance, out AttrName, false, out success);
                        break;
                    case "coo": xp = GetOrRaiseAttrib(session, 1, PropertyAttribute.Coordination, out AttrName, false, out success);
                        break;
                    case "qui": xp = GetOrRaiseAttrib(session, 1, PropertyAttribute.Quickness, out AttrName, false, out success);
                        break; 
                    case "foc": xp = GetOrRaiseAttrib(session, 1, PropertyAttribute.Focus, out AttrName, false, out success);
                        break;
                    case "sel": xp = GetOrRaiseAttrib(session, 1, PropertyAttribute.Self, out AttrName, false, out success);
                        break;
                    case "sta": xp = GetOrRaise2ndAttrib(session, 1, PropertyAttribute2nd.MaxStamina, out AttrName, false, out success);
                        break;
                    case "hea": xp = GetOrRaise2ndAttrib(session, 1, PropertyAttribute2nd.MaxHealth, out AttrName, false, out success);
                        break;
                    case "man": xp = GetOrRaise2ndAttrib(session, 1, PropertyAttribute2nd.MaxMana, out AttrName, false, out success);
                        break;
                }

                session.Network.EnqueueSend(new GameMessageSystemChat($"[XP] Your XP cost for next {AttrName} level is: {xp:N0}", ChatMessageType.System));
            }            
        }

        public static ulong GetOrRaiseAttrib(Network.Session session, int RanksToRaise, PropertyAttribute attrib, out string AttrName, bool doRaise, out bool success)
        {            
            if (!session.Player.Attributes.TryGetValue(attrib, out var creatureAttribute))
            {}
            uint destinationRank = (uint)(creatureAttribute.Ranks + RanksToRaise);
            AttrName = creatureAttribute.Attribute.GetDescription();
            ulong xpCost = Player.GetXPDeltaCostByRank(destinationRank, creatureAttribute.Ranks);
            if (doRaise)
            {
                success = session.Player.HandleActionRaiseAttribute(attrib, xpCost);
            }
            else
            {
                success = false;
            }
            return xpCost;
        }

        public static ulong GetOrRaise2ndAttrib(Network.Session session, int RanksToRaise, PropertyAttribute2nd vital, out string AttrName, bool doRaise, out bool success)
        {
            if (!session.Player.Vitals.TryGetValue(vital, out var creatureAttribute))
            { }
            uint destinationRank = (uint)(creatureAttribute.Ranks + RanksToRaise);
            AttrName = creatureAttribute.Vital.GetDescription();
            ulong xpCost = Player.GetXPDeltaCostByRank(destinationRank, creatureAttribute.Ranks);
            if (doRaise)
            {
                success = session.Player.HandleActionRaiseVital(vital, xpCost);
            }
            else
            {
                success = false;
            }
            return xpCost;
        }

        [CommandHandler("attr", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1, "Handles Attribute Raising", "Type the first 3 letter abbreviation for the attribute to raise, followed optionally by a number (str, end, coo, qui, foc, sel, vit, sta, man)")]
        public static void HandleRaiseAttribute(Session session, params string[] parameters)
        {
            int amt = 1; ulong xpCost = 0; string AttrName = string.Empty; bool success = false;
            if (parameters.Count() == 2)
            {
                amt = Convert.ToInt32(parameters[1]);
                
            }
            switch (parameters[0])
            {
                case "str":
                    xpCost = GetOrRaiseAttrib(session, amt, PropertyAttribute.Strength, out AttrName, true, out success);
                    break;
                case "end":
                    xpCost = GetOrRaiseAttrib(session, amt, PropertyAttribute.Endurance, out AttrName, true, out success);
                    break;
                case "coo":
                    xpCost = GetOrRaiseAttrib(session, amt, PropertyAttribute.Coordination, out AttrName, true, out success);
                    break;
                case "qui":
                    xpCost = GetOrRaiseAttrib(session, amt, PropertyAttribute.Quickness, out AttrName, true, out success);
                    break;
                case "foc":
                    xpCost = GetOrRaiseAttrib(session, amt, PropertyAttribute.Focus, out AttrName, true, out success);
                    break;
                case "sel":
                    xpCost = GetOrRaiseAttrib(session, amt, PropertyAttribute.Self, out AttrName, true, out success);
                    break;
                case "sta":
                    xpCost = GetOrRaise2ndAttrib(session, amt, PropertyAttribute2nd.MaxStamina, out AttrName, true, out success);
                    break;
                case "hea":
                    xpCost = GetOrRaise2ndAttrib(session, amt, PropertyAttribute2nd.MaxHealth, out AttrName, true, out success);
                    break;
                case "man":
                    xpCost = GetOrRaise2ndAttrib(session, amt, PropertyAttribute2nd.MaxMana, out AttrName, true, out success);
                    break;
                default:
                    session.Network.EnqueueSend(new GameMessageSystemChat($"[ATTR] Invalid attribute. Type /attr for help.", ChatMessageType.System));
                    break;
            }
            if (success)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"[ATTR] {AttrName} attribute raised by {amt}, costing {xpCost:N0}!", ChatMessageType.Advancement));
            }
            else
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"[ATTR] Could not raise {AttrName} attribute, check your available XP and use /xp to see the cost and try again.", ChatMessageType.Advancement));
            }
        }

        // pop
        [CommandHandler("pop", AccessLevel.Player, CommandHandlerFlag.None, 0,
            "Show current world population",
            "")]
        public static void HandlePop(Session session, params string[] parameters)
        {
            CommandHandlerHelper.WriteOutputInfo(session, $"Current world population: {PlayerManager.GetOnlineCount():N0}", ChatMessageType.Broadcast);
        }

        [CommandHandler("qb", AccessLevel.Player, CommandHandlerFlag.None, "Show current quest bonus", "add List to list your current quests")]
        public static void DisplayQB(Session session, params string[] parameters)
        {
            bool list = false;
            if (parameters.Length > 0)
            {
                if (parameters[0] == "list")
                {
                    list = true;
                }
            }

            if (!list)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Your current quest bonus count: {session.Player.Account.GetCharacterQuestCompletions():N0}", ChatMessageType.Broadcast);
            }
            else
            {
                using (var context = new AuthDbContext())
                {
                    var res = context.AccountQuest.Where(x => x.AccountId == session.AccountId && x.NumTimesCompleted >= 1).ToList();
                    if (res != null)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, $"Your completed quest bonus list:", ChatMessageType.Broadcast);
                        foreach (var item in res)
                        {
                            CommandHandlerHelper.WriteOutputInfo(session, $"{item.Quest}", ChatMessageType.Broadcast);
                        }
                    }
                    var res2 = context.AccountQuest.Where(x => x.AccountId == session.AccountId && x.NumTimesCompleted == 0).ToList();
                    if (res2 != null)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, $"Your incomplete quest bonus list:", ChatMessageType.Broadcast);
                        foreach (var item in res2)
                        {
                            CommandHandlerHelper.WriteOutputInfo(session, $"{item.Quest}", ChatMessageType.Broadcast);
                        }
                    }
                }
            }
            
        }

        [CommandHandler("top", AccessLevel.Player, CommandHandlerFlag.None, "Show current leaderboards", "use top qb to list top quest bonus count, top level to list top character levels, enl for enlightenments")]
        public static void DisplayTop(Session session, params string[] parameters)
        {
            List<Leaderboard> list = new List<Leaderboard>();
            using (var context = new AuthDbContext())
            {
                if (parameters.Length > 0 && parameters[0] == "qb")
                {
                    list = Leaderboard.GetTopQBLeaderboard(context);
                    if (list.Count > 0)
                    {
                        session.Network.EnqueueSend(new GameMessageSystemChat("Top 25 Players by Quest Bonus:", ChatMessageType.Broadcast));
                    }
                }

                if (parameters.Length > 0 && parameters[0] == "level")
                {
                    list = Leaderboard.GetTopLevelLeaderboard(context);
                    if (list.Count > 0)
                    {
                        session.Network.EnqueueSend(new GameMessageSystemChat("Top 25 Players by Level:", ChatMessageType.Broadcast));
                    }
                }

                if (parameters.Length > 0 && parameters[0] == "enl")
                {
                    list = Leaderboard.GetTopEnlightenmentLeaderboard(context);
                    if (list.Count > 0)
                    {
                        session.Network.EnqueueSend(new GameMessageSystemChat("Top 25 Players by Enlightenment:", ChatMessageType.Broadcast));
                    }
                }
            }

            for (int i = 0; i < list.Count; i++)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"{i+1}: {list[i].Score:N0} - {list[i].Character}", ChatMessageType.Broadcast));
            }
        }

        // quest info (uses GDLe formatting to match plugin expectations)
        [CommandHandler("myquests", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows your quest log")]
        public static void HandleQuests(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("quest_info_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"myquests\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var quests = session.Player.QuestManager.GetQuests();

            if (quests.Count == 0)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("Quest list is empty.", ChatMessageType.Broadcast));
                return;
            }

            foreach (var playerQuest in quests)
            {
                var text = "";
                var questName = QuestManager.GetQuestName(playerQuest.QuestName);
                var quest = DatabaseManager.World.GetCachedQuest(questName);
                if (quest == null)
                {
                    Console.WriteLine($"Couldn't find quest {playerQuest.QuestName}");
                    continue;
                }

                var minDelta = quest.MinDelta;
                if (QuestManager.CanScaleQuestMinDelta(quest))
                    minDelta = (uint)(quest.MinDelta * PropertyManager.GetDouble("quest_mindelta_rate").Item);

                text += $"{playerQuest.QuestName.ToLower()} - {playerQuest.NumTimesCompleted} solves ({playerQuest.LastTimeCompleted})";
                text += $"\"{quest.Message}\" {quest.MaxSolves} {minDelta}";

                session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            }
        }

        /// <summary>
        /// For characters/accounts who currently own multiple houses, used to select which house they want to keep
        /// </summary>
        [CommandHandler("house-select", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1, "For characters/accounts who currently own multiple houses, used to select which house they want to keep")]
        public static void HandleHouseSelect(Session session, params string[] parameters)
        {
            HandleHouseSelect(session, false, parameters);
        }

        public static void HandleHouseSelect(Session session, bool confirmed, params string[] parameters)
        {
            if (!int.TryParse(parameters[0], out var houseIdx))
                return;

            // ensure current multihouse owner
            if (!session.Player.IsMultiHouseOwner(false))
            {
                log.Warn($"{session.Player.Name} tried to /house-select {houseIdx}, but they are not currently a multi-house owner!");
                return;
            }

            // get house info for this index
            var multihouses = session.Player.GetMultiHouses();

            if (houseIdx < 1 || houseIdx > multihouses.Count)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Please enter a number between 1 and {multihouses.Count}.", ChatMessageType.Broadcast));
                return;
            }

            var keepHouse = multihouses[houseIdx - 1];

            // show confirmation popup
            if (!confirmed)
            {
                var houseType = $"{keepHouse.HouseType}".ToLower();
                var loc = HouseManager.GetCoords(keepHouse.SlumLord.Location);

                var msg = $"Are you sure you want to keep the {houseType} at\n{loc}?";
                if (!session.Player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(session.Player.Guid, () => HandleHouseSelect(session, true, parameters)), msg))
                    session.Player.SendWeenieError(WeenieError.ConfirmationInProgress);
                return;
            }

            // house to keep confirmed, abandon the other houses
            var abandonHouses = new List<House>(multihouses);
            abandonHouses.RemoveAt(houseIdx - 1);

            foreach (var abandonHouse in abandonHouses)
            {
                var house = session.Player.GetHouse(abandonHouse.Guid.Full);

                HouseManager.HandleEviction(house, house.HouseOwner ?? 0, true);
            }

            // set player properties for house to keep
            var player = PlayerManager.FindByGuid(keepHouse.HouseOwner ?? 0, out bool isOnline);
            if (player == null)
            {
                log.Error($"{session.Player.Name}.HandleHouseSelect({houseIdx}) - couldn't find HouseOwner {keepHouse.HouseOwner} for {keepHouse.Name} ({keepHouse.Guid})");
                return;
            }

            player.HouseId = keepHouse.HouseId;
            player.HouseInstance = keepHouse.Guid.Full;

            player.SaveBiotaToDatabase();

            // update house panel for current player
            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(3.0f);  // wait for slumlord inventory biotas above to save
            actionChain.AddAction(session.Player, session.Player.HandleActionQueryHouse);
            actionChain.EnqueueChain();

            Console.WriteLine("OK");
        }

        [CommandHandler("debugcast", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows debug information about the current magic casting state")]
        public static void HandleDebugCast(Session session, params string[] parameters)
        {
            var physicsObj = session.Player.PhysicsObj;

            var pendingActions = physicsObj.MovementManager.MoveToManager.PendingActions;
            var currAnim = physicsObj.PartArray.Sequence.CurrAnim;

            session.Network.EnqueueSend(new GameMessageSystemChat(session.Player.MagicState.ToString(), ChatMessageType.Broadcast));
            session.Network.EnqueueSend(new GameMessageSystemChat($"IsMovingOrAnimating: {physicsObj.IsMovingOrAnimating}", ChatMessageType.Broadcast));
            session.Network.EnqueueSend(new GameMessageSystemChat($"PendingActions: {pendingActions.Count}", ChatMessageType.Broadcast));
            session.Network.EnqueueSend(new GameMessageSystemChat($"CurrAnim: {currAnim?.Value.Anim.ID:X8}", ChatMessageType.Broadcast));
        }

        [CommandHandler("fixcast", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Fixes magic casting if locked up for an extended time")]
        public static void HandleFixCast(Session session, params string[] parameters)
        {
            var magicState = session.Player.MagicState;

            if (magicState.IsCasting && DateTime.UtcNow - magicState.StartTime > TimeSpan.FromSeconds(5))
            {
                session.Network.EnqueueSend(new GameEventCommunicationTransientString(session, "Fixed casting state"));
                session.Player.SendUseDoneEvent();
                magicState.OnCastDone();
            }
        }

        [CommandHandler("castmeter", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows the fast casting efficiency meter")]
        public static void HandleCastMeter(Session session, params string[] parameters)
        {
            if (parameters.Length == 0)
            {
                session.Player.MagicState.CastMeter = !session.Player.MagicState.CastMeter;
            }
            else
            {
                if (parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
                    session.Player.MagicState.CastMeter = true;
                else
                    session.Player.MagicState.CastMeter = false;
            }
            session.Network.EnqueueSend(new GameMessageSystemChat($"Cast efficiency meter {(session.Player.MagicState.CastMeter ? "enabled" : "disabled")}", ChatMessageType.Broadcast));
        }

        private static List<string> configList = new List<string>()
        {
            "Common settings:\nConfirmVolatileRareUse, MainPackPreferred, SalvageMultiple, SideBySideVitals, UseCraftSuccessDialog",
            "Interaction settings:\nAcceptLootPermits, AllowGive, AppearOffline, AutoAcceptFellowRequest, DragItemOnPlayerOpensSecureTrade, FellowshipShareLoot, FellowshipShareXP, IgnoreAllegianceRequests, IgnoreFellowshipRequests, IgnoreTradeRequests, UseDeception",
            "UI settings:\nCoordinatesOnRadar, DisableDistanceFog, DisableHouseRestrictionEffects, DisableMostWeatherEffects, FilterLanguage, LockUI, PersistentAtDay, ShowCloak, ShowHelm, ShowTooltips, SpellDuration, TimeStamp, ToggleRun, UseMouseTurning",
            "Chat settings:\nHearAllegianceChat, HearGeneralChat, HearLFGChat, HearRoleplayChat, HearSocietyChat, HearTradeChat, HearPKDeaths, StayInChatMode",
            "Combat settings:\nAdvancedCombatUI, AutoRepeatAttack, AutoTarget, LeadMissileTargets, UseChargeAttack, UseFastMissiles, ViewCombatTarget, VividTargetingIndicator",
            "Character display settings:\nDisplayAge, DisplayAllegianceLogonNotifications, DisplayChessRank, DisplayDateOfBirth, DisplayFishingSkill, DisplayNumberCharacterTitles, DisplayNumberDeaths"
        };

        /// <summary>
        /// Mapping of GDLE -> ACE CharacterOptions
        /// </summary>
        private static Dictionary<string, string> translateOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Common
            { "ConfirmVolatileRareUse", "ConfirmUseOfRareGems" },
            { "MainPackPreferred", "UseMainPackAsDefaultForPickingUpItems" },
            { "SalvageMultiple", "SalvageMultipleMaterialsAtOnce" },
            { "SideBySideVitals", "SideBySideVitals" },
            { "UseCraftSuccessDialog", "UseCraftingChanceOfSuccessDialog" },

            // Interaction
            { "AcceptLootPermits", "AcceptCorpseLootingPermissions" },
            { "AllowGive", "LetOtherPlayersGiveYouItems" },
            { "AppearOffline", "AppearOffline" },
            { "AutoAcceptFellowRequest", "AutomaticallyAcceptFellowshipRequests" },
            { "DragItemOnPlayerOpensSecureTrade", "DragItemToPlayerOpensTrade" },
            { "FellowshipShareLoot", "ShareFellowshipLoot" },
            { "FellowshipShareXP", "ShareFellowshipExpAndLuminance" },
            { "IgnoreAllegianceRequests", "IgnoreAllegianceRequests" },
            { "IgnoreFellowshipRequests", "IgnoreFellowshipRequests" },
            { "IgnoreTradeRequests", "IgnoreAllTradeRequests" },
            { "UseDeception", "AttemptToDeceiveOtherPlayers" },

            // UI
            { "CoordinatesOnRadar", "ShowCoordinatesByTheRadar" },
            { "DisableDistanceFog", "DisableDistanceFog" },
            { "DisableHouseRestrictionEffects", "DisableHouseRestrictionEffects" },
            { "DisableMostWeatherEffects", "DisableMostWeatherEffects" },
            { "FilterLanguage", "FilterLanguage" },
            { "LockUI", "LockUI" },
            { "PersistentAtDay", "AlwaysDaylightOutdoors" },
            { "ShowCloak", "ShowYourCloak" },
            { "ShowHelm", "ShowYourHelmOrHeadGear" },
            { "ShowTooltips", "Display3dTooltips" },
            { "SpellDuration", "DisplaySpellDurations" },
            { "TimeStamp", "DisplayTimestamps" },
            { "ToggleRun", "RunAsDefaultMovement" },
            { "UseMouseTurning", "UseMouseTurning" },

            // Chat
            { "HearAllegianceChat", "ListenToAllegianceChat" },
            { "HearGeneralChat", "ListenToGeneralChat" },
            { "HearLFGChat", "ListenToLFGChat" },
            { "HearRoleplayChat", "ListentoRoleplayChat" },
            { "HearSocietyChat", "ListenToSocietyChat" },
            { "HearTradeChat", "ListenToTradeChat" },
            { "HearPKDeaths", "ListenToPKDeathMessages" },
            { "StayInChatMode", "StayInChatModeAfterSendingMessage" },

            // Combat
            { "AdvancedCombatUI", "AdvancedCombatInterface" },
            { "AutoRepeatAttack", "AutoRepeatAttacks" },
            { "AutoTarget", "AutoTarget" },
            { "LeadMissileTargets", "LeadMissileTargets" },
            { "UseChargeAttack", "UseChargeAttack" },
            { "UseFastMissiles", "UseFastMissiles" },
            { "ViewCombatTarget", "KeepCombatTargetsInView" },
            { "VividTargetingIndicator", "VividTargetingIndicator" },

            // Character Display
            { "DisplayAge", "AllowOthersToSeeYourAge" },
            { "DisplayAllegianceLogonNotifications", "ShowAllegianceLogons" },
            { "DisplayChessRank", "AllowOthersToSeeYourChessRank" },
            { "DisplayDateOfBirth", "AllowOthersToSeeYourDateOfBirth" },
            { "DisplayFishingSkill", "AllowOthersToSeeYourFishingSkill" },
            { "DisplayNumberCharacterTitles", "AllowOthersToSeeYourNumberOfTitles" },
            { "DisplayNumberDeaths", "AllowOthersToSeeYourNumberOfDeaths" },
        };

        /// <summary>
        /// Manually sets a character option on the server. Use /config list to see a list of settings.
        /// </summary>
        [CommandHandler("config", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1, "Manually sets a character option on the server.\nUse /config list to see a list of settings.", "<setting> <on/off>")]
        public static void HandleConfig(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("player_config_command").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"config\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            // /config list - show character options
            if (parameters[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in configList)
                    session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

                return;
            }

            // translate GDLE CharacterOptions for existing plugins
            if (!translateOptions.TryGetValue(parameters[0], out var param) || !Enum.TryParse(param, out CharacterOption characterOption))
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Unknown character option: {parameters[0]}", ChatMessageType.Broadcast));
                return;
            }

            var option = session.Player.GetCharacterOption(characterOption);

            // modes of operation:
            // on / off / toggle

            // - if none specified, default to toggle
            var mode = "toggle";

            if (parameters.Length > 1)
            {
                if (parameters[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                    mode = "on";
                else if (parameters[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                    mode = "off";
            }

            // set character option
            if (mode.Equals("on"))
                option = true;
            else if (mode.Equals("off"))
                option = false;
            else
                option = !option;

            session.Player.SetCharacterOption(characterOption, option);

            session.Network.EnqueueSend(new GameMessageSystemChat($"Character option {parameters[0]} is now {(option ? "on" : "off")}.", ChatMessageType.Broadcast));

            // update client
            session.Network.EnqueueSend(new GameEventPlayerDescription(session));
        }

        /// <summary>
        /// Force resend of all visible objects known to this player. Can fix rare cases of invisible object bugs.
        /// Can only be used once every 5 mins max.
        /// </summary>
        [CommandHandler("objsend", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Force resend of all visible objects known to this player. Can fix rare cases of invisible object bugs. Can only be used once every 5 mins max.")]
        public static void HandleObjSend(Session session, params string[] parameters)
        {
            // a good repro spot for this is the first room after the door in facility hub
            // in the portal drop / staircase room, the VisibleCells do not have the room after the door
            // however, the room after the door *does* have the portal drop / staircase room in its VisibleCells (the inverse relationship is imbalanced)
            // not sure how to fix this atm, seems like it triggers a client bug..

            if (DateTime.UtcNow - session.Player.PrevObjSend < TimeSpan.FromMinutes(5))
            {
                session.Player.SendTransientError("You have used this command too recently!");
                return;
            }

            var creaturesOnly = parameters.Length > 0 && parameters[0].Contains("creature", StringComparison.OrdinalIgnoreCase);

            var knownObjs = session.Player.GetKnownObjects();

            foreach (var knownObj in knownObjs)
            {
                if (creaturesOnly && !(knownObj is Creature))
                    continue;

                session.Player.RemoveTrackedObject(knownObj, false);
                session.Player.TrackObject(knownObj);
            }
            session.Player.PrevObjSend = DateTime.UtcNow;
        }

        // show player ace server versions
        [CommandHandler("aceversion", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows this server's version data")]
        public static void HandleACEversion(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("version_info_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"aceversion\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var msg = ServerBuildInfo.GetVersionInfo();

            session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast));
        }

        // reportbug < code | content > < description >
        [CommandHandler("reportbug", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 2,
            "Generate a Bug Report",
            "<category> <description>\n" +
            "This command generates a URL for you to copy and paste into your web browser to submit for review by server operators and developers.\n" +
            "Category can be the following:\n" +
            "Creature\n" +
            "NPC\n" +
            "Item\n" +
            "Quest\n" +
            "Recipe\n" +
            "Landblock\n" +
            "Mechanic\n" +
            "Code\n" +
            "Other\n" +
            "For the first three options, the bug report will include identifiers for what you currently have selected/targeted.\n" +
            "After category, please include a brief description of the issue, which you can further detail in the report on the website.\n" +
            "Examples:\n" +
            "/reportbug creature Drudge Prowler is over powered\n" +
            "/reportbug npc Ulgrim doesn't know what to do with Sake\n" +
            "/reportbug quest I can't enter the portal to the Lost City of Frore\n" +
            "/reportbug recipe I cannot combine Bundle of Arrowheads with Bundle of Arrowshafts\n" +
            "/reportbug code I was killed by a Non-Player Killer\n"
            )]
        public static void HandleReportbug(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("reportbug_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"reportbug\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var category = parameters[0];
            var description = "";

            for (var i = 1; i < parameters.Length; i++)
                description += parameters[i] + " ";

            description.Trim();

            switch (category.ToLower())
            {
                case "creature":
                case "npc":
                case "quest":
                case "item":
                case "recipe":
                case "landblock":
                case "mechanic":
                case "code":
                case "other":
                    break;
                default:
                    category = "Other";
                    break;
            }

            var sn = ConfigManager.Config.Server.WorldName;
            var c = session.Player.Name;

            var st = "ACE";

            //var versions = ServerBuildInfo.GetVersionInfo();
            var databaseVersion = DatabaseManager.World.GetVersion();
            var sv = ServerBuildInfo.FullVersion;
            var pv = databaseVersion.PatchVersion;

            //var ct = PropertyManager.GetString("reportbug_content_type").Item;
            var cg = category.ToLower();

            var w = "";
            var g = "";

            if (cg == "creature" || cg == "npc"|| cg == "item" || cg == "item")
            {
                var objectId = new ObjectGuid();
                if (session.Player.HealthQueryTarget.HasValue || session.Player.ManaQueryTarget.HasValue || session.Player.CurrentAppraisalTarget.HasValue)
                {
                    if (session.Player.HealthQueryTarget.HasValue)
                        objectId = new ObjectGuid((uint)session.Player.HealthQueryTarget);
                    else if (session.Player.ManaQueryTarget.HasValue)
                        objectId = new ObjectGuid((uint)session.Player.ManaQueryTarget);
                    else
                        objectId = new ObjectGuid((uint)session.Player.CurrentAppraisalTarget);

                    //var wo = session.Player.CurrentLandblock?.GetObject(objectId);

                    var wo = session.Player.FindObject(objectId.Full, Player.SearchLocations.Everywhere);

                    if (wo != null)
                    {
                        w = $"{wo.WeenieClassId}";
                        g = $"0x{wo.Guid:X8}";
                    }
                }
            }

            var l = session.Player.Location.ToLOCString();

            var issue = description;

            var urlbase = $"https://www.accpp.net/bug?";

            var url = urlbase;
            if (sn.Length > 0)
                url += $"sn={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sn))}";
            if (c.Length > 0)
                url += $"&c={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c))}";
            if (st.Length > 0)
                url += $"&st={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(st))}";
            if (sv.Length > 0)
                url += $"&sv={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sv))}";
            if (pv.Length > 0)
                url += $"&pv={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pv))}";
            //if (ct.Length > 0)
            //    url += $"&ct={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ct))}";
            if (cg.Length > 0)
            {
                if (cg == "npc")
                    cg = cg.ToUpper();
                else
                    cg = char.ToUpper(cg[0]) + cg.Substring(1);
                url += $"&cg={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cg))}";
            }
            if (w.Length > 0)
                url += $"&w={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(w))}";
            if (g.Length > 0)
                url += $"&g={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(g))}";
            if (l.Length > 0)
                url += $"&l={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(l))}";
            if (issue.Length > 0)
                url += $"&i={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(issue))}";

            var msg = "\n\n\n\n";
            msg += "Bug Report - Copy and Paste the following URL into your browser to submit a bug report\n";
            msg += "-=-\n";
            msg += $"{url}\n";
            msg += "-=-\n";
            msg += "\n\n\n\n";

            session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.AdminTell));
        }
    }
}
