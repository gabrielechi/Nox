using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.Auth
{
    public class RegisterResponse
    {
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
