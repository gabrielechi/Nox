using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Server.Entities.PreKeys;
using Server.Interfaces;
using Shared.DTO.Auth;
using System.Security.Claims;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private const int PayloadSaltLength = 16;
        private const int PublicKeyLength = 32;
        private const int AesGcmNonceLength = 12;
        private const int AesGcmTagLength = 16;
        private const int MinimumEncryptedPayloadLength = AesGcmNonceLength + AesGcmTagLength + 1;

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IJwtTokenService _jwtTokenService;

        public AuthController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IJwtTokenService jwtTokenService)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _jwtTokenService = jwtTokenService;
        }

        [HttpPost("register")]
        public async Task<ActionResult<RegisterResponse>> Register(RegisterRequest request)
        {
            string? validationError = ValidateRegisterRequest(request);

            if (validationError is not null)
                return BadRequest(validationError);

            string username = request.Username.Trim();

            ApplicationUser? existingUser = await _userManager.FindByNameAsync(username);

            if (existingUser is not null)
                return Conflict("Username already exists.");

            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = username,
                PayloadSalt = request.PayloadSalt,
                EncryptedKeyPayload = request.EncryptedKeyPayload,
                X25519PublicKey = request.X25519PublicKey,
                Ed25519PublicKey = request.Ed25519PublicKey,
                CreatedAtUtc = DateTime.UtcNow
            };

            IdentityResult result = await _userManager.CreateAsync(user, request.Password);

            if (!result.Succeeded)
            {
                string[] errors = result.Errors
                    .Select(error => error.Description)
                    .ToArray();

                return BadRequest(errors);
            }

            var response = new RegisterResponse
            {
                UserId = user.Id,
                Username = user.UserName ?? username,
                CreatedAtUtc = user.CreatedAtUtc
            };

            return Created($"/api/users/{user.Id}", response);
        }

        [EnableRateLimiting("LoginRateLimit")]
        [HttpPost("login")]
        public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
        {
            string? validationError = ValidateLoginRequest(request);

            if (validationError is not null)
                return BadRequest(validationError);

            string username = request.Username.Trim();

            ApplicationUser? user = await _userManager.FindByNameAsync(username);

            if (user is null)
                return Unauthorized("Invalid credentials.");

            Microsoft.AspNetCore.Identity.SignInResult signInResult = await _signInManager.CheckPasswordSignInAsync(
                user,
                request.Password,
                lockoutOnFailure: true);

            if (signInResult.IsLockedOut)
                return Unauthorized("Invalid credentials.");

            if (!signInResult.Succeeded)
                return Unauthorized("Invalid credentials.");

            user.LastLoginAtUtc = DateTime.UtcNow;

            IdentityResult updateResult = await _userManager.UpdateAsync(user);

            if (!updateResult.Succeeded)
                return StatusCode(StatusCodes.Status500InternalServerError, "Could not update user login metadata.");

            string jwt = _jwtTokenService.GenerateToken(user);

            var response = new LoginResponse
            {
                Jwt = jwt,
                PayloadSalt = user.PayloadSalt,
                EncryptedKeyPayload = user.EncryptedKeyPayload,
                X25519PublicKey = user.X25519PublicKey,
                Ed25519PublicKey = user.Ed25519PublicKey
            };

            return Ok(response);
        }

        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpGet("me")]
        public async Task<ActionResult<CurrentUserResponse>> Me()
        {
            string? userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(userIdValue))
                return Unauthorized();

            ApplicationUser? user = await _userManager.FindByIdAsync(userIdValue);

            if (user is null)
                return Unauthorized();

            var response = new CurrentUserResponse
            {
                Username = user.UserName ?? string.Empty,
                PayloadSalt = user.PayloadSalt,
                EncryptedKeyPayload = user.EncryptedKeyPayload,
                X25519PublicKey = user.X25519PublicKey,
                Ed25519PublicKey = user.Ed25519PublicKey
            };

            return Ok(response);
        }

        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpPut("vault")]
        public async Task<ActionResult> UpdateVault(UpdateVaultRequest request)
        {
            if (request.EncryptedKeyPayload.Length == 0)
                return BadRequest("EncryptedKeyPayload is required.");

            string? userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(userIdValue))
                return Unauthorized();

            ApplicationUser? user = await _userManager.FindByIdAsync(userIdValue);

            if (user is null)
                return Unauthorized();

            user.EncryptedKeyPayload = request.EncryptedKeyPayload;

            IdentityResult result = await _userManager.UpdateAsync(user);

            if (!result.Succeeded)
                return StatusCode(StatusCodes.Status500InternalServerError, "Could not update encrypted vault.");

            return NoContent();
        }

        private static string? ValidateRegisterRequest(RegisterRequest request)
        {
            string username = request.Username.Trim();

            if (string.IsNullOrWhiteSpace(username))
                return "Username is required.";

            if (string.IsNullOrWhiteSpace(request.Password))
                return "Password is required.";

            if (request.Password.Length < 12)
                return "Password must be at least 12 characters long.";

            if (!request.Password.Any(char.IsUpper))
                return "Password must contain at least one uppercase letter.";

            if (!request.Password.Any(char.IsLower))
                return "Password must contain at least one lowercase letter.";

            if (!request.Password.Any(char.IsDigit))
                return "Password must contain at least one number.";

            if (!request.Password.Any(ch => !char.IsLetterOrDigit(ch)))
                return "Password must contain at least one special character.";

            if (request.PayloadSalt.Length != PayloadSaltLength)
                return $"PayloadSalt must be {PayloadSaltLength} bytes.";

            if (request.EncryptedKeyPayload.Length < MinimumEncryptedPayloadLength)
                return "EncryptedKeyPayload is too short.";

            if (request.X25519PublicKey.Length != PublicKeyLength)
                return $"X25519PublicKey must be {PublicKeyLength} bytes.";

            if (request.Ed25519PublicKey.Length != PublicKeyLength)
                return $"Ed25519PublicKey must be {PublicKeyLength} bytes.";

            return null;
        }

        private static string? ValidateLoginRequest(LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
                return "Username is required.";

            if (string.IsNullOrWhiteSpace(request.Password))
                return "Password is required.";

            return null;
        }
    }
}
