using Microsoft.EntityFrameworkCore;
using Server.Database;
using Server.Entities.Transfers;
using Server.Interfaces;

namespace Server.Services
{
    public class TransferCleanupService : BackgroundService
    {
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TransferCleanupService> _logger;

        public TransferCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<TransferCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupExpiredTransfersAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while cleaning expired transfers.");
                }

                await Task.Delay(CleanupInterval, stoppingToken);
            }
        }

        private async Task CleanupExpiredTransfersAsync(CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ITransferStorageService storageService = scope.ServiceProvider.GetRequiredService<ITransferStorageService>();

            DateTime now = DateTime.UtcNow;

            List<FileTransfer> expiredTransfers = await dbContext.FileTransfers
                .Include(transfer => transfer.Files)
                .Where(transfer =>
                    transfer.Status == TransferStatus.Pending &&
                    transfer.ExpiresAtUtc <= now)
                .ToListAsync(cancellationToken);

            foreach (FileTransfer transfer in expiredTransfers)
            {
                await storageService.DeleteTransferDirectoryAsync(
                    transfer.RecipientId,
                    transfer.SenderId,
                    transfer.Id,
                    cancellationToken);

                dbContext.FileTransfers.Remove(transfer);
            }

            if (expiredTransfers.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Cleaned {ExpiredTransferCount} expired transfers.",
                    expiredTransfers.Count);
            }
        }
    }
}
