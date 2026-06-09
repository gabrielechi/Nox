using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Auth
{
    public class RegisterRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public byte[] PayloadSalt { get; set; } = [];
        public byte[] EncryptedKeyPayload { get; set; } = []; 
        public byte[] X25519PublicKey { get; set; } = [];
        public byte[] Ed25519PublicKey { get; set; } = [];
    }
}
