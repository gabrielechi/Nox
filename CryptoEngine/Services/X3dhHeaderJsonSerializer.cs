using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using System.Text.Json;

namespace CryptoEngine.Services
{
    public class X3dhHeaderJsonSerializer : IX3dhHeaderSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public byte[] Serialize(X3dhMessageHeader header)
        {
            return JsonSerializer.SerializeToUtf8Bytes(header, Options);
        }

        public X3dhMessageHeader Deserialize(byte[] serializedHeader)
        {
            X3dhMessageHeader? header = JsonSerializer.Deserialize<X3dhMessageHeader>(
                serializedHeader,
                Options);

            if (header is null)
                throw new InvalidOperationException("Invalid X3DH header.");

            return header;
        }
    }
}