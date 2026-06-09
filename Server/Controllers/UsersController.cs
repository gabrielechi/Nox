using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Database;
using Server.Entities.PreKeys;
using Shared.DTO;
using Shared.DTO.X3DH;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/users")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly UserManager<ApplicationUser> _userManager;

        public UsersController(
            AppDbContext dbContext,
            UserManager<ApplicationUser> userManager)
        {
            _dbContext = dbContext;
            _userManager = userManager;
        }

        [HttpGet("{username}/prekey-bundle")]
        public async Task<ActionResult<PreKeyBundleResponse>> GetPreKeyBundle(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return BadRequest("Username is required.");

            string normalizedUsername = username.Trim();

            ApplicationUser? recipient = await _userManager.FindByNameAsync(normalizedUsername);

            if (recipient is null)
                return NotFound("User not found.");

            SignedPreKey? signedPreKey = await _dbContext.SignedPreKeys
                .SingleOrDefaultAsync(key => key.UserId == recipient.Id);

            if (signedPreKey is null)
                return Conflict("Recipient has no signed prekey.");

            OneTimePreKey? oneTimePreKey;

            await using (var transaction = await _dbContext.Database.BeginTransactionAsync())
            {
                try
                {
                    oneTimePreKey = await _dbContext.OneTimePreKeys
                        .Where(key => key.UserId == recipient.Id && !key.IsClaimed)
                        .OrderBy(key => key.CreatedAtUtc)
                    .FirstOrDefaultAsync();

                    if (oneTimePreKey is not null)
                    {
                        oneTimePreKey.IsClaimed = true;
                        oneTimePreKey.ClaimedAtUtc = DateTime.UtcNow;

                        await _dbContext.SaveChangesAsync();
                    }

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }

                var response = new PreKeyBundleResponse
                {
                    UserId = recipient.Id,
                    Username = recipient.UserName ?? normalizedUsername,
                    X25519IdentityPublicKey = recipient.X25519PublicKey,
                    Ed25519IdentityPublicKey = recipient.Ed25519PublicKey,
                    SignedPreKeyId = signedPreKey.KeyId,
                    SignedPreKeyPublicKey = signedPreKey.PublicKey,
                    SignedPreKeySignature = signedPreKey.Signature,
                    OneTimePreKeyId = oneTimePreKey?.KeyId,
                    OneTimePreKeyPublicKey = oneTimePreKey?.PublicKey
                };

                return Ok(response);
            }
        }
    }
}
