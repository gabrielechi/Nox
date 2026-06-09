using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Models
{
    public record X3dhSenderSecretInput(
            string SenderUsername,
            byte[] SenderX25519IdentityPrivateKey,
            byte[] SenderX25519IdentityPublicKey,
            byte[] SenderEd25519IdentityPublicKey,
            X3dhPreKeyBundle RecipientBundle
        );
}
