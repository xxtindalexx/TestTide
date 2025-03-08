using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ACE.Database.Models
{
    [Table("auction_payments")]
    public class AuctionPaymentEntry
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public ulong SellerGuid { get; set; }  // Seller receiving the payment
        public int Amount { get; set; }  // Amount of Enlightened Coins to be awarded
        public DateTime PaymentDate { get; set; } = DateTime.UtcNow; // Timestamp of payment storage
    }
}
