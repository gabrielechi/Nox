using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Database;
using Server.Entities.PreKeys;
using Shared.DTO;
using Shared.DTO.X3DH;
using System.Security.Claims;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/prekeys")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class PreKeysController : ControllerBase
    {
        private const int PublicKeyLength = 32;
        private const int SignatureLength = 64;
        private const int MinimumOneTimePreKeysPerUpload = 25;
        private const int MaximumOneTimePreKeysPerUpload = 100;

        private readonly AppDbContext _dbContext;
        private readonly UserManager<ApplicationUser> _userManager;

        public PreKeysController(
            AppDbContext dbContext,
            UserManager<ApplicationUser> userManager)
        {
            _dbContext = dbContext;
            _userManager = userManager;
        }

        [HttpPost("signed")]
        public async Task<ActionResult> UploadSignedPreKey(UploadSignedPreKeyRequest request)
        {
            string? validationError = ValidateSignedPreKeyRequest(request);

            if (validationError is not null)
                return BadRequest(validationError);

            ApplicationUser? user = await GetCurrentUserAsync();

            if (user is null)
                return Unauthorized();

            List<SignedPreKey> existingKeys = await _dbContext.SignedPreKeys
                .Where(key => key.UserId == user.Id)
                .ToListAsync();

            _dbContext.SignedPreKeys.RemoveRange(existingKeys);

            var signedPreKey = new SignedPreKey
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                KeyId = request.KeyId,
                PublicKey = request.PublicKey,
                Signature = request.Signature,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(30)
            };

            _dbContext.SignedPreKeys.Add(signedPreKey);

            await _dbContext.SaveChangesAsync();

            return NoContent();
        }

        [HttpPost("one-time")]
        public async Task<ActionResult> UploadOneTimePreKeys(UploadOneTimePreKeysRequest request)
        {
            string? validationError = ValidateOneTimePreKeysRequest(request);

            if (validationError is not null)
                return BadRequest(validationError);

            ApplicationUser? user = await GetCurrentUserAsync();

            if (user is null)
                return Unauthorized();

            int[] requestedKeyIds = request.Keys
                .Select(key => key.KeyId)
                .ToArray();

            bool hasDuplicateKeyIdsInRequest = requestedKeyIds.Length != requestedKeyIds.Distinct().Count();

            if (hasDuplicateKeyIdsInRequest)
                return BadRequest("One-time prekey KeyId values must be unique in the request.");

            List<int> existingKeyIds = await _dbContext.OneTimePreKeys
                .Where(key => key.UserId == user.Id && requestedKeyIds.Contains(key.KeyId))
                .Select(key => key.KeyId)
                .ToListAsync();

            if (existingKeyIds.Count > 0)
                return Conflict($"One or more one-time prekey KeyId values already exist: {string.Join(", ", existingKeyIds)}.");

            var now = DateTime.UtcNow;

            List<OneTimePreKey> oneTimePreKeys = request.Keys
                .Select(key => new OneTimePreKey
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    KeyId = key.KeyId,
                    PublicKey = key.PublicKey,
                    CreatedAtUtc = now,
                    IsClaimed = false
                })
                .ToList();

            _dbContext.OneTimePreKeys.AddRange(oneTimePreKeys);

            await _dbContext.SaveChangesAsync();

            return NoContent();
        }

        [HttpGet("status")]
        public async Task<ActionResult<PreKeyStatusResponse>> GetStatus()
        {
            ApplicationUser? user = await GetCurrentUserAsync();

            if (user is null)
                return Unauthorized();

            SignedPreKey? signedPreKey = await _dbContext.SignedPreKeys
                .SingleOrDefaultAsync(key => key.UserId == user.Id);

            int availableOneTimePreKeys = await _dbContext.OneTimePreKeys
                .CountAsync(key => key.UserId == user.Id && !key.IsClaimed);

            var response = new PreKeyStatusResponse
            {
                HasSignedPreKey = signedPreKey is not null,
                SignedPreKeyId = signedPreKey?.KeyId,
                AvailableOneTimePreKeys = availableOneTimePreKeys
            };

            return Ok(response);
        }

        private async Task<ApplicationUser?> GetCurrentUserAsync()
        {
            string? userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(userIdValue))
                return null;

            return await _userManager.FindByIdAsync(userIdValue);
        }

        private static string? ValidateSignedPreKeyRequest(UploadSignedPreKeyRequest request)
        {
            if (request.KeyId < 0)
                return "SignedPreKey KeyId must be non-negative.";

            if (request.PublicKey.Length != PublicKeyLength)
                return $"SignedPreKey PublicKey must be {PublicKeyLength} bytes.";

            if (request.Signature.Length != SignatureLength)
                return $"SignedPreKey Signature must be {SignatureLength} bytes.";

            return null;
        }

        private static string? ValidateOneTimePreKeysRequest(UploadOneTimePreKeysRequest request)
        {
            if (request.Keys.Count < MinimumOneTimePreKeysPerUpload)
                return $"At least {MinimumOneTimePreKeysPerUpload} one-time prekeys are required.";

            if (request.Keys.Count > MaximumOneTimePreKeysPerUpload)
                return $"At most {MaximumOneTimePreKeysPerUpload} one-time prekeys can be uploaded at once.";

            foreach (OneTimePreKeyDto key in request.Keys)
            {
                if (key.KeyId < 0)
                    return "One-time prekey KeyId must be non-negative.";

                if (key.PublicKey.Length != PublicKeyLength)
                    return $"One-time prekey PublicKey must be {PublicKeyLength} bytes.";
            }

            return null;
        }
    }
}