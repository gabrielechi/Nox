using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Transfers
{
    public class TransferFileResponse
    {
        public Guid FileId { get; set; }
        public int FileIndex { get; set; }
        public byte[] FileHeader { get; set; } = [];
        public long CiphertextLength { get; set; }
        public bool IsUploaded { get; set; }
        public string DownloadPath { get; set; } = string.Empty;
    }
}
