using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.X3DH
{
    public class PreKeyBundleResponse
    {
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public byte[] X25519IdentityPublicKey { get; set; } = [];
        public byte[] Ed25519IdentityPublicKey { get; set; } = [];
        public int SignedPreKeyId { get; set; }
        public byte[] SignedPreKeyPublicKey { get; set; } = [];
        public byte[] SignedPreKeySignature { get; set; } = [];
        public int? OneTimePreKeyId { get; set; }
        public byte[]? OneTimePreKeyPublicKey { get; set; }
    }
}
