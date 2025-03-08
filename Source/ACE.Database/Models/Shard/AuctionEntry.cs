using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ACE.Database.Models
{
    [Table("auction_house")]
    public class AuctionEntry
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public ulong ItemGuid { get; set; }      // Item being auctioned
        public ulong SellerGuid { get; set; }    // Seller's player ID
        public ulong? BuyerGuid { get; set; }    // Buyer's player ID (if bid placed)

        public int MinBid { get; set; }
        public int? BuyoutPrice { get; set; }
        public int HighestBid { get; set; }

        public int DurationSeconds { get; set; } // Duration of the auction in seconds
        public DateTime StartTime { get; set; } // When the auction expires
    }
}
