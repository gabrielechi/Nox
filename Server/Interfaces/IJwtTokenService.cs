using Server.Entities.PreKeys;

namespace Server.Interfaces
{
    public interface IJwtTokenService
    {
        string GenerateToken(ApplicationUser user);
    }
}
