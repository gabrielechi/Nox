using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.X3DH
{
    public class OneTimePreKeyDto
    {
        public int KeyId { get; set; }
        public byte[] PublicKey { get; set; } = [];
    }
}
