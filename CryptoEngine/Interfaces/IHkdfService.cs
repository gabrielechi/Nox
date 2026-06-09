using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Interfaces
{
    public interface IHkdfService
    {
        byte[] DeriveKey(byte[] masterKey, string info, int outputLength = 32);
    }
}
