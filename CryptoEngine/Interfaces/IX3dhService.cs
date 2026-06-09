using CryptoEngine.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Interfaces
{
    public interface IX3dhService
    {
        X3dhSecretResult CreateSenderSecret(X3dhSenderSecretInput input);
        X3dhSecretResult CreateRecipientSecret(X3dhRecipientSecretInput input);
    }
}
