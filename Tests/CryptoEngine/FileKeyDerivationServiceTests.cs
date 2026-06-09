using CryptoEngine.Interfaces;
using CryptoEngine.Services;

namespace Tests.CryptoEngine
{
    public class FileKeyDerivationServiceTests
    {
        private readonly IFileKeyDerivationService _service;

        public FileKeyDerivationServiceTests()
        {
            IHkdfService hkdfService = new HkdfService();
            _service = new FileKeyDerivationService(hkdfService);
        }

        [Fact]
        public void DeriveFileKeys_SameInputs_ReturnsSameKeys()
        {
            byte[] rootKey = Enumerable.Repeat((byte)7, 32).ToArray();
            Guid transferId = Guid.NewGuid();

            var first = _service.DeriveFileKeys(rootKey, transferId, 0);
            var second = _service.DeriveFileKeys(rootKey, transferId, 0);

            Assert.Equal(first.FileKey, second.FileKey);
            Assert.Equal(first.MetadataKey, second.MetadataKey);
        }

        [Fact]
        public void DeriveFileKeys_FileAndMetadataKeys_AreDifferent()
        {
            byte[] rootKey = Enumerable.Repeat((byte)7, 32).ToArray();
            Guid transferId = Guid.NewGuid();

            var keys = _service.DeriveFileKeys(rootKey, transferId, 0);

            Assert.NotEqual(keys.FileKey, keys.MetadataKey);
        }

        [Fact]
        public void DeriveFileKeys_DifferentFileIndex_ReturnsDifferentKeys()
        {
            byte[] rootKey = Enumerable.Repeat((byte)7, 32).ToArray();
            Guid transferId = Guid.NewGuid();

            var first = _service.DeriveFileKeys(rootKey, transferId, 0);
            var second = _service.DeriveFileKeys(rootKey, transferId, 1);

            Assert.NotEqual(first.FileKey, second.FileKey);
            Assert.NotEqual(first.MetadataKey, second.MetadataKey);
        }

        [Fact]
        public void DeriveFileKeys_InvalidRootKeyLength_Throws()
        {
            byte[] rootKey = new byte[31];

            Assert.Throws<ArgumentException>(() =>
                _service.DeriveFileKeys(rootKey, Guid.NewGuid(), 0));
        }
    }
}