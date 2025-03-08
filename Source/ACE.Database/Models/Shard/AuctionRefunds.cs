using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ACE.Database.Models
{
    [Table("auction_refunds")]
    public class AuctionRefundEntry
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public ulong BidderGuid { get; set; } //Bidder who needs refunded
        public int RefundedAmount { get; set; } //Amount to refund
        public DateTime RefundDate { get; set; } = DateTime.UtcNow; // Time when refund was issued
    }
}
