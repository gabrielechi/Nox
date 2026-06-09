namespace Server.Interfaces
{
    public interface ITransferStorageService
    {
        string GetStorageObjectName(Guid recipientId, Guid senderId, Guid transferId, Guid fileId);
        Task SaveAsync(string storageObjectName, Stream content, CancellationToken cancellationToken);
        Task<Stream> OpenReadAsync(string storageObjectName, CancellationToken cancellationToken);
        Task DeleteAsync(string storageObjectName, CancellationToken cancellationToken);
        Task DeleteTransferDirectoryAsync(Guid recipientId, Guid senderId, Guid transferId, CancellationToken cancellationToken);
    }
}
