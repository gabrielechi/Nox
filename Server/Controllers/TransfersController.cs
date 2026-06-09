using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Database;
using Server.Entities;
using Server.Entities.PreKeys;
using Server.Entities.Transfers;
using Server.Interfaces;
using Shared.DTO;
using Shared.DTO.Transfers;
using System.Security.Claims;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/transfers")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class TransfersController : ControllerBase
    {
        private const int MinimumX3dhHeaderLength = 1;
        private const int MinimumFileHeaderLength = 1;
        private const int MaximumFilesPerTransfer = 20;
        private const long MaximumCiphertextLength = 102L * 1024L * 1024L;

        private readonly AppDbContext _dbContext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ITransferStorageService _storageService;

        public TransfersController(
            AppDbContext dbContext,
            UserManager<ApplicationUser> userManager,
            ITransferStorageService storageService)
        {
            _dbContext = dbContext;
            _userManager = userManager;
            _storageService = storageService;
        }

        [HttpPost]
        public async Task<ActionResult<CreateTransferResponse>> CreateTransfer(CreateTransferRequest request)
        {
            string? validationError = ValidateCreateTransferRequest(request);

            if (validationError is not null)
                return BadRequest(validationError);

            ApplicationUser? sender = await GetCurrentUserAsync();

            if (sender is null)
                return Unauthorized();

            string recipientUsername = request.RecipientUsername.Trim();

            ApplicationUser? recipient = await _userManager.FindByNameAsync(recipientUsername);

            if (recipient is null)
                return NotFound("Recipient not found.");

            DateTime now = DateTime.UtcNow;
            Guid transferId = Guid.NewGuid();

            var transfer = new FileTransfer
            {
                Id = transferId,
                SenderId = sender.Id,
                RecipientId = recipient.Id,
                X3dhHeader = request.X3dhHeader,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddHours(24),
                Status = TransferStatus.Pending
            };

            List<FileTransferItem> fileItems = request.Files
                .Select(file =>
                {
                    Guid fileId = Guid.NewGuid();

                    return new FileTransferItem
                    {
                        Id = fileId,
                        TransferId = transferId,
                        FileIndex = file.FileIndex,
                        FileHeader = file.FileHeader,
                        CiphertextLength = file.CiphertextLength,
                        StorageObjectName = _storageService.GetStorageObjectName(
                            recipient.Id,
                            sender.Id,
                            transferId,
                            fileId),
                        CreatedAtUtc = now
                    };
                })
                .ToList();

            transfer.Files = fileItems;

            _dbContext.FileTransfers.Add(transfer);

            await _dbContext.SaveChangesAsync();

            var response = new CreateTransferResponse
            {
                TransferId = transfer.Id,
                ExpiresAtUtc = transfer.ExpiresAtUtc,
                Files = fileItems
                    .OrderBy(file => file.FileIndex)
                    .Select(file => new CreateTransferFileResponse
                    {
                        FileId = file.Id,
                        FileIndex = file.FileIndex,
                        UploadPath = $"/api/transfers/{transfer.Id}/files/{file.Id}/content"
                    })
                    .ToList()
            };

            return Created($"/api/transfers/{transfer.Id}", response);
        }


        [HttpPost("{transferId:guid}/files/{fileId:guid}/content")]
        [RequestSizeLimit(MaximumCiphertextLength)]
        public async Task<ActionResult> UploadFileContent(
            Guid transferId,
            Guid fileId,
            CancellationToken cancellationToken)
        {
            ApplicationUser? sender = await GetCurrentUserAsync();

            if (sender is null)
                return Unauthorized();

            FileTransfer? transfer = await _dbContext.FileTransfers
                .Include(transfer => transfer.Files)
                .SingleOrDefaultAsync(transfer => transfer.Id == transferId, cancellationToken);

            if (transfer is null)
                return NotFound("Transfer not found.");

            if (transfer.SenderId != sender.Id)
                return Forbid();

            if (transfer.Status != TransferStatus.Pending)
                return Conflict("Transfer is not pending.");

            if (transfer.ExpiresAtUtc <= DateTime.UtcNow)
                return Conflict("Transfer is expired.");

            FileTransferItem? file = transfer.Files
                .SingleOrDefault(file => file.Id == fileId);

            if (file is null)
                return NotFound("File not found.");

            if (file.IsUploaded)
                return Conflict("File content has already been uploaded.");

            if (Request.ContentLength.HasValue && Request.ContentLength.Value != file.CiphertextLength)
                return BadRequest("Content-Length does not match CiphertextLength.");

            await _storageService.SaveAsync(
                file.StorageObjectName,
                Request.Body,
                cancellationToken);

            file.IsUploaded = true;
            file.UploadedAtUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return NoContent();
        }

        [HttpGet("inbox")]
        public async Task<ActionResult<List<TransferSummaryResponse>>> GetInbox(CancellationToken cancellationToken)
        {
            ApplicationUser? recipient = await GetCurrentUserAsync();

            if (recipient is null)
                return Unauthorized();

            DateTime now = DateTime.UtcNow;

            List<TransferSummaryResponse> response = await _dbContext.FileTransfers
                .AsNoTracking()
                .Include(transfer => transfer.Sender)
                .Include(transfer => transfer.Files)
                .Where(transfer =>
                    transfer.RecipientId == recipient.Id &&
                    transfer.Status == TransferStatus.Pending &&
                    transfer.ExpiresAtUtc > now &&
                    transfer.Files.All(file => file.IsUploaded))
                .OrderByDescending(transfer => transfer.CreatedAtUtc)
                .Select(transfer => new TransferSummaryResponse
                {
                    TransferId = transfer.Id,
                    SenderUsername = transfer.Sender.UserName ?? string.Empty,
                    CreatedAtUtc = transfer.CreatedAtUtc,
                    ExpiresAtUtc = transfer.ExpiresAtUtc,
                    FileCount = transfer.Files.Count
                })
                .ToListAsync(cancellationToken);

            return Ok(response);
        }

        [HttpGet("{transferId:guid}")]
        public async Task<ActionResult<TransferDetailResponse>> GetTransferDetail(
              Guid transferId,
              CancellationToken cancellationToken)
        {
            ApplicationUser? currentUser = await GetCurrentUserAsync();

            if (currentUser is null)
                return Unauthorized();

            DateTime now = DateTime.UtcNow;

            FileTransfer? transfer = await _dbContext.FileTransfers
                .AsNoTracking()
                .Include(transfer => transfer.Sender)
                .Include(transfer => transfer.Files)
                .SingleOrDefaultAsync(transfer => transfer.Id == transferId, cancellationToken);

            if (transfer is null)
                return NotFound("Transfer not found.");

            bool isSender = transfer.SenderId == currentUser.Id;
            bool isRecipient = transfer.RecipientId == currentUser.Id;

            if (!isSender && !isRecipient)
                return Forbid();

            if (transfer.Status != TransferStatus.Pending)
                return Conflict("Transfer is not pending.");

            if (transfer.ExpiresAtUtc <= now)
                return Conflict("Transfer is expired.");

            var response = new TransferDetailResponse
            {
                TransferId = transfer.Id,
                SenderUsername = transfer.Sender.UserName ?? string.Empty,
                SenderX25519PublicKey = transfer.Sender.X25519PublicKey,
                SenderEd25519PublicKey = transfer.Sender.Ed25519PublicKey,
                X3dhHeader = transfer.X3dhHeader,
                CreatedAtUtc = transfer.CreatedAtUtc,
                ExpiresAtUtc = transfer.ExpiresAtUtc,
                Files = transfer.Files
                    .OrderBy(file => file.FileIndex)
                    .Select(file => new TransferFileResponse
                    {
                        FileId = file.Id,
                        FileIndex = file.FileIndex,
                        FileHeader = file.FileHeader,
                        CiphertextLength = file.CiphertextLength,
                        IsUploaded = file.IsUploaded,
                        DownloadPath = $"/api/transfers/{transfer.Id}/files/{file.Id}/content"
                    })
                    .ToList()
            };

            return Ok(response);
        }

        [HttpGet("{transferId:guid}/files/{fileId:guid}/content")]
        public async Task<ActionResult> DownloadFileContent(
            Guid transferId,
            Guid fileId,
            CancellationToken cancellationToken)
        {
            ApplicationUser? currentUser = await GetCurrentUserAsync();

            if (currentUser is null)
                return Unauthorized();

            FileTransfer? transfer = await _dbContext.FileTransfers
                .Include(transfer => transfer.Files)
                .SingleOrDefaultAsync(transfer => transfer.Id == transferId, cancellationToken);

            if (transfer is null)
                return NotFound("Transfer not found.");

            bool isSender = transfer.SenderId == currentUser.Id;
            bool isRecipient = transfer.RecipientId == currentUser.Id;

            if (!isSender && !isRecipient)
                return Forbid();

            if (transfer.Status != TransferStatus.Pending)
                return Conflict("Transfer is not pending.");

            if (transfer.ExpiresAtUtc <= DateTime.UtcNow)
                return Conflict("Transfer is expired.");

            FileTransferItem? file = transfer.Files
                .SingleOrDefault(file => file.Id == fileId);

            if (file is null)
                return NotFound("File not found.");

            if (!file.IsUploaded)
                return Conflict("File has not been uploaded yet.");

            Stream stream = await _storageService.OpenReadAsync(
                file.StorageObjectName,
                cancellationToken);

            return File(
                fileStream: stream,
                contentType: "application/octet-stream",
                fileDownloadName: $"{file.Id:N}.bin");
        }

        [HttpPost("{transferId:guid}/downloaded")]
        public async Task<ActionResult> MarkTransferAsDownloaded(
            Guid transferId,
            CancellationToken cancellationToken)
        {
            ApplicationUser? recipient = await GetCurrentUserAsync();

            if (recipient is null)
                return Unauthorized();

            FileTransfer? transfer = await _dbContext.FileTransfers
                .Include(transfer => transfer.Files)
                .SingleOrDefaultAsync(transfer => transfer.Id == transferId, cancellationToken);

            if (transfer is null)
                return NotFound("Transfer not found.");

            if (transfer.RecipientId != recipient.Id)
                return Forbid();

            if (transfer.Status != TransferStatus.Pending)
                return Conflict("Transfer is not pending.");

            if (transfer.ExpiresAtUtc <= DateTime.UtcNow)
                return Conflict("Transfer is expired.");

            bool allFilesUploaded = transfer.Files.All(file => file.IsUploaded);

            if (!allFilesUploaded)
                return Conflict("Transfer is not fully uploaded.");

            await _storageService.DeleteTransferDirectoryAsync(
                transfer.RecipientId,
                transfer.SenderId,
                transfer.Id,
                cancellationToken);

            _dbContext.FileTransfers.Remove(transfer);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return NoContent();
        }
        private async Task<ApplicationUser?> GetCurrentUserAsync()
        {
            string? userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(userIdValue))
                return null;

            return await _userManager.FindByIdAsync(userIdValue);
        }

        private static string? ValidateCreateTransferRequest(CreateTransferRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RecipientUsername))
                return "RecipientUsername is required.";

            if (request.X3dhHeader.Length < MinimumX3dhHeaderLength)
                return "X3dhHeader is required.";

            if (request.Files.Count == 0)
                return "At least one file is required.";

            if (request.Files.Count > MaximumFilesPerTransfer)
                return $"At most {MaximumFilesPerTransfer} files can be sent in one transfer.";

            bool hasDuplicateIndexes = request.Files
                .Select(file => file.FileIndex)
                .Distinct()
                .Count() != request.Files.Count;

            if (hasDuplicateIndexes)
                return "FileIndex values must be unique.";

            foreach (CreateTransferFileRequest file in request.Files)
            {
                if (file.FileIndex < 0)
                    return "FileIndex must be non-negative.";

                if (file.FileHeader.Length < MinimumFileHeaderLength)
                    return "FileHeader is required.";

                if (file.CiphertextLength <= 0)
                    return "CiphertextLength must be greater than zero.";

                if (file.CiphertextLength > MaximumCiphertextLength)
                    return $"CiphertextLength cannot exceed {MaximumCiphertextLength} bytes.";
            }

            return null;
        }
    }
}
