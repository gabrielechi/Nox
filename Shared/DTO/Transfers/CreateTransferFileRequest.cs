using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Transfers
{
    public class CreateTransferFileRequest
    {
        public int FileIndex { get; set; }
        public byte[] FileHeader { get; set; } = [];
        public long CiphertextLength { get; set; }
    }
}
