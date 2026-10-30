using Client.Windows.Services;
using CryptoEngine.Services;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tests.Client;

public class FileDownloadServiceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("tampered")]
    [InlineData("truncated")]
    [InlineData("cancelled")]
    public async Task Download_PreservesOnlyCompletelyVerifiedFiles(string scenario)
    {
        var crypto = new FileContentEncryptionService(new AesGcmSymmetricService());
        var receiver = new FileDownloadService(crypto);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] original = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        using var encrypted = new MemoryStream();
        await crypto.EncryptAsync(new MemoryStream(original), encrypted, key);
        byte[] bytes = encrypted.ToArray();
        if (scenario == "tampered")
            bytes[^1] ^= 1;
        if (scenario == "truncated")
            bytes = bytes[..(16 + 1024 * 1024 + 28)];
        string path = Path.Combine(Path.GetTempPath(), $"nox-download-test-{Guid.NewGuid():N}");
        try
        {
            using var source = new MemoryStream(bytes);
            var token = scenario == "cancelled" ? new CancellationToken(true) : CancellationToken.None;
            if (scenario == "valid")
            {
                await receiver.DecryptToStagingFileAsync(source, path, key, original.LongLength, token);
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
            }
            else
            {
                if (scenario == "cancelled")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        receiver.DecryptToStagingFileAsync(source, path, key, original.LongLength, token));
                else
                    await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
                        receiver.DecryptToStagingFileAsync(source, path, key, original.LongLength, token));
                Assert.False(File.Exists(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_RejectsHugeChunkBeforeAllocatingItsDeclaredLength()
    {
        byte[] header = new byte[16];
        "ZKTF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), int.MaxValue);
        var crypto = new FileContentEncryptionService(new AesGcmSymmetricService());
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            crypto.DecryptAsync(new MemoryStream(header), output, new byte[32]));
        Assert.Equal(0, output.Length);
    }
}
