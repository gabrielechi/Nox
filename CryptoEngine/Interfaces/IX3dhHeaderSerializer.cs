using CryptoEngine.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Interfaces
{
    public interface IX3dhHeaderSerializer
    {
        byte[] Serialize(X3dhMessageHeader header);

        X3dhMessageHeader Deserialize(byte[] serializedHeader);
    }
}
