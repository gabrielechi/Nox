using Client.Windows.Models;
using Client.Windows.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace Client.Windows.Interfaces
{
    public interface IPreKeyBootstrapService
    {
        Task EnsurePreKeysAsync(
            ApiClient apiClient,
            UserKeyPayload vault,
            CancellationToken cancellationToken = default);
    }
}
