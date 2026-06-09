using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Transfers
{
    public class CreateTransferRequest
    {
        public string RecipientUsername { get; set; } = string.Empty;
        public byte[] X3dhHeader { get; set; } = [];
        public List<CreateTransferFileRequest> Files { get; set; } = [];
    }
}
