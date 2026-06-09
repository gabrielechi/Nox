using CryptoEngine.Interfaces;
using CryptoEngine.Services;

namespace Tests.CryptoEngine
{
    public class ArgonKeyDerivationServiceTests
    {
        private readonly IArgonKeyDerivationService _argonService;

        public ArgonKeyDerivationServiceTests()
        {
            _argonService = new ArgonKeyDerivationService();
        }

        [Fact]
        public void HashPasswordForServer_ShouldVerifyCorrectPassword()
        {
            string password = "password123";

            string hash = _argonService.HashPasswordForServer(password);

            Assert.True(_argonService.VerifyPasswordForServer(password, hash));
        }

        [Fact]
        public void HashPasswordForServer_ShouldRejectWrongPassword()
        {
            string password = "password123";
            string wrongPassword = "passwordSbagliata";

            string hash = _argonService.HashPasswordForServer(password);

            Assert.False(_argonService.VerifyPasswordForServer(wrongPassword, hash));
        }

        [Fact]
        public void HashPasswordForServer_ShouldProduceDifferentHashesForSamePassword()
        {
            string password = "password123";

            string hash1 = _argonService.HashPasswordForServer(password);
            string hash2 = _argonService.HashPasswordForServer(password);

            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void PasswordNeedsRehash_ShouldReturnFalseForFreshHash()
        {
            string password = "password123";

            string hash = _argonService.HashPasswordForServer(password);

            Assert.False(_argonService.PasswordNeedsRehash(hash));
        }

        [Fact]
        public void DerivePayloadKey_ShouldReturn32Bytes()
        {
            byte[] salt = _argonService.GeneratePayloadSalt();

            byte[] key = _argonService.DerivePayloadKey("password123", salt);

            Assert.Equal(32, key.Length);
        }

        [Fact]
        public void DerivePayloadKey_ShouldBeDeterministicWithSamePasswordAndSalt()
        {
            byte[] salt = _argonService.GeneratePayloadSalt();

            byte[] key1 = _argonService.DerivePayloadKey("password123", salt);
            byte[] key2 = _argonService.DerivePayloadKey("password123", salt);

            Assert.Equal(key1, key2);
        }

        [Fact]
        public void DerivePayloadKey_ShouldChangeWithDifferentSalt()
        {
            byte[] salt1 = _argonService.GeneratePayloadSalt();
            byte[] salt2 = _argonService.GeneratePayloadSalt();

            byte[] key1 = _argonService.DerivePayloadKey("password123", salt1);
            byte[] key2 = _argonService.DerivePayloadKey("password123", salt2);

            Assert.NotEqual(key1, key2);
        }

        [Fact]
        public void DerivePayloadKey_ShouldChangeWithDifferentPassword()
        {
            byte[] salt = _argonService.GeneratePayloadSalt();

            byte[] key1 = _argonService.DerivePayloadKey("password123", salt);
            byte[] key2 = _argonService.DerivePayloadKey("passwordSbagliata", salt);

            Assert.NotEqual(key1, key2);
        }
    }
}