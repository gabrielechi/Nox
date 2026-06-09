using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Transfers
{
    public class CreateTransferFileResponse
    {
        public Guid FileId { get; set; }
        public int FileIndex { get; set; }
        public string UploadPath { get; set; } = string.Empty;
    }
}
