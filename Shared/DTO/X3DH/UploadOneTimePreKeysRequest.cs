using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.DTO.X3DH
{
    public class UploadOneTimePreKeysRequest
    {
        public List<OneTimePreKeyDto> Keys { get; set; } = [];
    }
}
