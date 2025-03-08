using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ACE.Database.Models
{
    [Table("auction_returns")]
    public class AuctionReturnEntry
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public ulong SellerGuid { get; set; }  // Seller who needs item returned
        public ulong BuyerGuid { get; set; }   // Buyer who needs item returned
        public ulong ItemGuid { get; set; }    // Item that needs to be returned
        public DateTime ReturnDate { get; set; } = DateTime.UtcNow; // Time when it was stored
    }
}
