using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.X3DH
{
    public class PreKeyStatusResponse
    {
        public bool HasSignedPreKey { get; set; }
        public int? SignedPreKeyId { get; set; }
        public int AvailableOneTimePreKeys { get; set; }
    }
}
