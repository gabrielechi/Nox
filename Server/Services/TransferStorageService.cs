using Server.Interfaces;

namespace Server.Services
{
    public class TransferStorageService : ITransferStorageService
    {
        private readonly string _rootPath;

        public TransferStorageService(IConfiguration configuration, IWebHostEnvironment environment)
        {
            string configuredRootPath = configuration["Storage:RootPath"] ?? "storage";

            _rootPath = Path.IsPathRooted(configuredRootPath)
                ? configuredRootPath
                : Path.Combine(environment.ContentRootPath, configuredRootPath);

            Directory.CreateDirectory(_rootPath);
        }

        public string GetStorageObjectName(Guid recipientId, Guid senderId, Guid transferId, Guid fileId)
        {
            return Path.Combine(
                "users",
                recipientId.ToString("N"),
                "from",
                senderId.ToString("N"),
                transferId.ToString("N"),
                $"{fileId:N}.bin");
        }

        public async Task SaveAsync(string storageObjectName, Stream content, CancellationToken cancellationToken)
        {
            string fullPath = GetFullPath(storageObjectName);

            string? directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using FileStream fileStream = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 64,
                useAsync: true);

            await content.CopyToAsync(fileStream, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(string storageObjectName, CancellationToken cancellationToken)
        {
            string fullPath = GetFullPath(storageObjectName);

            Stream stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 64,
                useAsync: true);

            return Task.FromResult(stream);
        }

        public Task DeleteAsync(string storageObjectName, CancellationToken cancellationToken)
        {
            string fullPath = GetFullPath(storageObjectName);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return Task.CompletedTask;
        }

        public Task DeleteTransferDirectoryAsync(
            Guid recipientId,
            Guid senderId,
            Guid transferId,
            CancellationToken cancellationToken)
        {
            string transferDirectory = Path.Combine(
                _rootPath,
                "users",
                recipientId.ToString("N"),
                "from",
                senderId.ToString("N"),
                transferId.ToString("N"));

            string fullPath = EnsureInsideRoot(transferDirectory);

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }

            return Task.CompletedTask;
        }

        private string GetFullPath(string storageObjectName)
        {
            string fullPath = Path.Combine(_rootPath, storageObjectName);

            return EnsureInsideRoot(fullPath);
        }

        private string EnsureInsideRoot(string fullPath)
        {
            string normalizedRoot = Path.GetFullPath(_rootPath);
            string normalizedPath = Path.GetFullPath(fullPath);

            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Storage path escapes the configured root.");

            return normalizedPath;
        }
    }
}