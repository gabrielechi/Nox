using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Models
{
    public record X3dhPreKeyBundle(
        string RecipientUsername,
        byte[] RecipientX25519IdentityPublicKey,
        byte[] RecipientEd25519IdentityPublicKey,
        int SignedPreKeyId,
        byte[] SignedPreKeyPublicKey,
        byte[] SignedPreKeySignature,
        int? OneTimePreKeyId,
        byte[]? OneTimePreKeyPublicKey
    );
}
