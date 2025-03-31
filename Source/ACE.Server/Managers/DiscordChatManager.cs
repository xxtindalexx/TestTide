using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Newtonsoft.Json;
using ACE.Common;
using static System.Net.Mime.MediaTypeNames;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using System.Collections.Concurrent;
using ACE.Server.Network.GameMessages.Messages;


namespace ACE.Server.Managers
{
    public class DiscordChatManager
    {

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static DiscordSocketClient _discordSocketClient;

        private static ulong RELAY_CHANNEL_ID = (ulong)ConfigManager.Config.Chat.AdminChannelId;
        private static string BOT_TOKEN = ConfigManager.Config.Chat.DiscordToken;
        private static string WEBHOOK_URL = ConfigManager.Config.Chat.WebhookURL;

        private static ConcurrentQueue<string> outgoingMessages = new ConcurrentQueue<string>();

        public static void SendDiscordMessage(string player, string message, long channelId)
        {
            if (ConfigManager.Config.Chat.EnableDiscordConnection)
            {
                try
                {
                    _discordSocketClient.GetGuild((ulong)ConfigManager.Config.Chat.ServerId).GetTextChannel((ulong)channelId).SendMessageAsync(player + " : " + message);
                }
                catch (Exception ex)
                {
                    log.Error("Error sending discord message, " + ex.Message);
                }
            }

        }

        public static void SendDiscordFile(string player, string message, long channelId, FileAttachment fileContent)
        {

            try
            {
                var res = _discordSocketClient.GetGuild((ulong)ConfigManager.Config.Chat.ServerId).GetTextChannel((ulong)channelId).SendFileAsync(fileContent, player + " : " + message).Result;
            }
            catch (Exception ex)
            {
                log.Error("Error sending discord message, " + ex.Message);
            }
            

        }

        public static string GetSQLFromDiscordMessage(int topN, string identifier)
        {
            string res = "";

            try
            {
                _discordSocketClient.GetGuild((ulong)ConfigManager.Config.Chat.ServerId)
                    .GetTextChannel((ulong)ConfigManager.Config.Chat.WeenieUploadsChannelId)
                    .GetMessagesAsync(limit: topN)
                    .FlattenAsync().Result.ToList()
                    .ForEach(x =>
                    {
                        if(x.Content == identifier)
                        {
                            if(x.Attachments.Count == 1)
                            {
                                IAttachment attachment = x.Attachments.First();
                                if (attachment.Filename.ToLowerInvariant().Contains(".sql"))
                                {
                                    using (var client = new WebClient())
                                    {
                                        res = client.GetStringFromURL(attachment.Url).Result;
                                        return;
                                    }
                                }
                            }
                        }    
                    });
            }
            catch (Exception ex)
            {

                log.Error("Error getting discord messages, " + ex.Message);
            }
            

            return res;
        }

        public static string GetJsonFromDiscordMessage(int topN, string identifier)
        {
            string res = "";

            try
            {
                _discordSocketClient.GetGuild((ulong)ConfigManager.Config.Chat.ServerId)
                    .GetTextChannel((ulong)ConfigManager.Config.Chat.ClothingModUploadChannelId)
                    .GetMessagesAsync(limit: topN)
                    .FlattenAsync().Result.ToList()
                    .ForEach(x =>
                    {
                        if (x.Content == identifier)
                        {
                            if (x.Attachments.Count == 1)
                            {
                                IAttachment attachment = x.Attachments.First();
                                if (attachment.Filename.ToLowerInvariant().Contains(".json"))
                                {
                                    // Using HttpClient to download the JSON
                                    using (var client = new HttpClient())
                                    {
                                        res = client.GetStringAsync(attachment.Url).Result; // Fetching the JSON content synchronously
                                        return;
                                    }
                                }
                            }
                        }
                    });
            }
            catch (Exception ex)
            {
                log.Error("Error getting discord messages, " + ex.Message);
            }

            return res;
        }



        public static void Initialize()
        {
            _discordSocketClient = new DiscordSocketClient(new DiscordSocketConfig()
            {
                GatewayIntents = GatewayIntents.Guilds |
                                 GatewayIntents.GuildMessages |
                                 GatewayIntents.MessageContent // ✅ REQUIRED
            });

            _discordSocketClient.MessageReceived += OnDiscordChat; // ✅ Listen for messages
            _discordSocketClient.LoginAsync(TokenType.Bot, ConfigManager.Config.Chat.DiscordToken);
            _discordSocketClient.StartAsync();
        }

        public static async Task OnDiscordChat(SocketMessage message)
        {
            if (message.Author.IsBot) return;

            ulong adminChannelId = (ulong)ConfigManager.Config.Chat.AdminChannelId;
            ulong generalChannelId = (ulong)ConfigManager.Config.Chat.GeneralChannelId;
            ulong tradeChannelId = (ulong)ConfigManager.Config.Chat.TradeChannelId;
            ulong eventsChannelId = (ulong)ConfigManager.Config.Chat.EventsChannelId;

            string sender = (message.Author as SocketGuildUser)?.Nickname ?? message.Author.Username;
            string content = message.Content.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                log.Warn($"[DiscordRelay] Message from {sender} is empty or unsupported.");
                return;
            }

            //log.Info($"[DiscordRelay] Relaying Discord message from {sender}: {content}");

            if (message.Channel.Id == adminChannelId)
            {
                PlayerManager.BroadcastFromDiscord(Channel.Admin, sender, content);
            }
            else if (message.Channel.Id == generalChannelId)
            {
                SendTurbineChat(ChatType.General, sender, content);
            }
            else if (message.Channel.Id == tradeChannelId)
            {
                SendTurbineChat(ChatType.Trade, sender, content);
            }
            else if (message.Channel.Id == eventsChannelId)
            {
                var msg = $"Discord broadcast from {sender}: {content}";
                GameMessageSystemChat sysMessage = new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast);
                PlayerManager.BroadcastToAll(sysMessage);
            }
        }

        /// <summary>
        /// Sends a message to Turbine Chat (General, Trade, etc.)
        /// </summary>
        private static void SendTurbineChat(ChatType chatType, string sender, string message)
        {
            var chatMessage = new GameMessageTurbineChat(
                ChatNetworkBlobType.NETBLOB_EVENT_BINARY,
                ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME,
                (uint)chatType,
                sender,
                message,
                0,
                chatType
            );

            foreach (var player in PlayerManager.GetAllOnline())
            {
                if ((chatType == ChatType.General && !player.GetCharacterOption(CharacterOption.ListenToGeneralChat)) ||
                    (chatType == ChatType.Trade && !player.GetCharacterOption(CharacterOption.ListenToTradeChat)))
                {
                    continue;
                }

                player.Session.Network.EnqueueSend(chatMessage);
            }

            //log.Info($"[DiscordRelay] Sent Discord message from {sender} to {chatType} chat: {message}");
        }

        public static void SendAuctionNotification(ulong discordUserId, string message)
        {
            if (ConfigManager.Config.Chat.EnableDiscordConnection && discordUserId != 0)
            {
                try
                {
                    var guild = _discordSocketClient.GetGuild((ulong)ConfigManager.Config.Chat.ServerId);
                    var user = guild?.GetUser(discordUserId);
                    user?.SendMessageAsync(message);
                }
                catch (Exception ex)
                {
                    log.Error($"[DiscordAuctionNotify] Failed to send message to Discord user {discordUserId}: {ex.Message}");
                }
            }
        }

        public static async void SendDiscordDM(string playerName, string message, long userId)
        {
            if (ConfigManager.Config.Chat.EnableDiscordConnection)
            {
                try
                {
                    var user = await _discordSocketClient.GetUserAsync((ulong)userId);
                    if (user != null)
                    {
                        // Check if the user is a RestUser or SocketUser
                        if (user is IUser iUser)
                        {
                            // Create DM channel with IUser
                            var dmChannel = await iUser.CreateDMChannelAsync(); // Creates DM channel if not existing
                            await dmChannel.SendMessageAsync(message); // Send the message to their DM

                            // Log the message sent
                            Console.WriteLine($"[DISCORD DM LOG] Sent message to user {playerName} (ID: {userId}): {message}");
                        }
                        else
                        {
                            // Log if the user type is not recognized
                            Console.WriteLine($"[DISCORD DM ERROR] Unrecognized user type: {user.GetType().Name} for user {userId}.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[DISCORD DM ERROR] User with ID {userId} not found.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DISCORD DM ERROR] Error sending Discord DM: {ex.Message}");
                }
            }
        }

        public static void QueueMessageForDiscord(string message)
        {
            outgoingMessages.Enqueue(message);
        }
    }
}
