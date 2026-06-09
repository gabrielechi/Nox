using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Models
{
    public record X3dhRecipientSecretInput(
       string RecipientUsername,
       byte[] RecipientX25519IdentityPrivateKey,
       byte[] SignedPreKeyPrivateKey,
       int SignedPreKeyId,
       byte[]? OneTimePreKeyPrivateKey,
       int? OneTimePreKeyId,
       X3dhMessageHeader Header
   );
}
