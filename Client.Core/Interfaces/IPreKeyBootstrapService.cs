using Client.Core.Models;
using Client.Core.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace Client.Core.Interfaces
{
    public interface IPreKeyBootstrapService
    {
        Task EnsurePreKeysAsync(
            ApiClient apiClient,
            UserKeyPayload vault,
            CancellationToken cancellationToken = default);

        void PrepareFreshRecoveryBundle(
            UserKeyPayload vault,
            bool preservePreviousDecryptionKeys);
    }
}
