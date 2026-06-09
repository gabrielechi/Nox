using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Models
{
    public record FileDerivedKeys(
            byte[] FileKey,
            byte[] MetadataKey
        );
}
