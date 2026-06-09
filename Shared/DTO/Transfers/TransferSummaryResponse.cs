using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Transfers
{
    public class TransferSummaryResponse
    {
        public Guid TransferId { get; set; }
        public string SenderUsername { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public int FileCount { get; set; }
    }
}
