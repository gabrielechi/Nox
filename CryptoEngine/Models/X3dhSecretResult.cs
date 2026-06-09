using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Models
{
    public record X3dhSecretResult(
        byte[] RootKey,
        X3dhMessageHeader Header
    );
}
