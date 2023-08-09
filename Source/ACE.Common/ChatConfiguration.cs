using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ACE.Common
{
    public class ChatConfiguration
    {
        [System.ComponentModel.DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool EnableDiscordConnection { get; set; }
        public string DiscordToken { get; set; }
        public long GeneralChannelId { get; set; }
        public long TradeChannelId { get; set; }
        public long ServerId { get; set; }
        public long AdminAuditId { get; set; }
        public long EventsChannelId { get; set; }

    }
}
