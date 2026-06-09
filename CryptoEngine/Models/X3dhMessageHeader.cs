using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Models
    {
        public record X3dhMessageHeader(
            int Version,
            Guid TransferContextId,
            string SenderUsername,
            byte[] SenderX25519IdentityPublicKey,
            byte[] SenderEd25519IdentityPublicKey,
            byte[] SenderEphemeralPublicKey,
            int RecipientSignedPreKeyId,
            int? RecipientOneTimePreKeyId
        );
    }
