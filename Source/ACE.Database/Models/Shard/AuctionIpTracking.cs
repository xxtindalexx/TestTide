using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ACE.Database.Models.Shard
{
    [Table("auction_ip_tracking")]
    public class AuctionIpTracking
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public string IpAddress { get; set; } // Player's IP Address
        public int ActiveAuctions { get; set; } // Number of ongoing auctions
        public DateTime LastAuctionListed { get; set; } // Timestamp of last auction listing
    }
}
