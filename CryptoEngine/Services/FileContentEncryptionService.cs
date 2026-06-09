using CryptoEngine.Interfaces;
using System.Buffers.Binary;
using System.Text;

namespace CryptoEngine.Services
{
    public class FileContentEncryptionService : IFileContentEncryptionService
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ZKTF");
        private const int Version = 1;
        private const int KeyLength = 32;
        private const int ChunkSize = 1024 * 1024;
        private const int ChunkHeaderLength = 12;

        private readonly ISymmetricService _symmetricService;

        public FileContentEncryptionService(ISymmetricService symmetricService)
        {
            _symmetricService = symmetricService;
        }

        public async Task EncryptAsync(
            Stream plaintext,
            Stream encryptedOutput,
            byte[] fileKey,
            CancellationToken cancellationToken = default)
        {
            ValidateKey(fileKey);

            byte[] buffer = new byte[ChunkSize];
            int chunkIndex = 0;

            while (true)
            {
                int bytesRead = await plaintext.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken);

                if (bytesRead == 0)
                    break;

                byte[] chunkPlaintext = buffer[..bytesRead];

                byte[] encryptedChunk = _symmetricService.Encrypt(
                    chunkPlaintext,
                    fileKey);

                await WriteChunkAsync(
                    encryptedOutput,
                    chunkIndex,
                    encryptedChunk,
                    cancellationToken);

                chunkIndex++;
            }
        }

        public async Task DecryptAsync(
            Stream encryptedInput,
            Stream plaintextOutput,
            byte[] fileKey,
            CancellationToken cancellationToken = default)
        {
            ValidateKey(fileKey);

            int expectedChunkIndex = 0;

            while (true)
            {
                byte[]? encryptedChunk = await ReadChunkAsync(
                    encryptedInput,
                    expectedChunkIndex,
                    cancellationToken);

                if (encryptedChunk is null)
                    break;

                byte[] plaintextChunk = _symmetricService.Decrypt(
                    encryptedChunk,
                    fileKey);

                await plaintextOutput.WriteAsync(
                    plaintextChunk,
                    cancellationToken);

                expectedChunkIndex++;
            }
        }

        private static async Task WriteChunkAsync(
            Stream output,
            int chunkIndex,
            byte[] encryptedChunk,
            CancellationToken cancellationToken)
        {
            byte[] header = new byte[ChunkHeaderLength];

            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), Version);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), chunkIndex);

            await output.WriteAsync(header, cancellationToken);

            byte[] lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, encryptedChunk.Length);

            await output.WriteAsync(lengthBytes, cancellationToken);
            await output.WriteAsync(encryptedChunk, cancellationToken);
        }

        private static async Task<byte[]?> ReadChunkAsync(
            Stream input,
            int expectedChunkIndex,
            CancellationToken cancellationToken)
        {
            byte[] header = new byte[ChunkHeaderLength];

            int headerBytesRead = await ReadExactlyOrEndAsync(
                input,
                header,
                cancellationToken);

            if (headerBytesRead == 0)
                return null;

            if (headerBytesRead != ChunkHeaderLength)
                throw new InvalidOperationException("Incomplete encrypted file chunk header.");

            if (!header.AsSpan(0, 4).SequenceEqual(Magic))
                throw new InvalidOperationException("Invalid encrypted file magic.");

            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            int chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));

            if (version != Version)
                throw new InvalidOperationException("Unsupported encrypted file version.");

            if (chunkIndex != expectedChunkIndex)
                throw new InvalidOperationException("Unexpected encrypted file chunk index.");

            byte[] lengthBytes = new byte[4];

            int lengthBytesRead = await ReadExactlyOrEndAsync(
                input,
                lengthBytes,
                cancellationToken);

            if (lengthBytesRead != 4)
                throw new InvalidOperationException("Incomplete encrypted file chunk length.");

            int encryptedChunkLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

            if (encryptedChunkLength <= 0)
                throw new InvalidOperationException("Invalid encrypted chunk length.");

            byte[] encryptedChunk = new byte[encryptedChunkLength];

            int encryptedChunkBytesRead = await ReadExactlyOrEndAsync(
                input,
                encryptedChunk,
                cancellationToken);

            if (encryptedChunkBytesRead != encryptedChunkLength)
                throw new InvalidOperationException("Incomplete encrypted chunk.");

            return encryptedChunk;
        }

        private static async Task<int> ReadExactlyOrEndAsync(
            Stream input,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;

            while (totalRead < buffer.Length)
            {
                int read = await input.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    cancellationToken);

                if (read == 0)
                    break;

                totalRead += read;
            }

            return totalRead;
        }

        private static void ValidateKey(byte[] key)
        {
            if (key.Length != KeyLength)
                throw new ArgumentException($"File key must be {KeyLength} bytes.", nameof(key));
        }
    }
}