using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Transfers
{
    public class CreateTransferResponse
    {
        public Guid TransferId { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public List<CreateTransferFileResponse> Files { get; set; } = [];
    }
}
