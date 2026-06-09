using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Transfers
{
    public class TransferDetailResponse
    {
        public Guid TransferId { get; set; }
        public string SenderUsername { get; set; } = string.Empty;
        public byte[] SenderX25519PublicKey { get; set; } = [];
        public byte[] SenderEd25519PublicKey { get; set; } = [];
        public byte[] X3dhHeader { get; set; } = [];
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public List<TransferFileResponse> Files { get; set; } = [];
    }
}
