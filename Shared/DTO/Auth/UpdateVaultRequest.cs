namespace Shared.DTO.Auth
{
    public class UpdateVaultRequest
    {
        public byte[] EncryptedKeyPayload { get; set; } = [];
    }
}