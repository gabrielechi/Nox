using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.X3DH
{
    public class UploadSignedPreKeyRequest
    {
        public int KeyId { get; set; }
        public byte[] PublicKey { get; set; } = [];
        public byte[] Signature { get; set; } = [];
    }
}
