# 🔒​**Self-Hostable End-to-End Encrypted File Transfer**
## 🔗Download
>[!Warning] 
> The **.exe** Client-installer does not carry a paid digital signature. Because of this, your browser or Windows SmartScreen might **flag it as an unrecognized app**. You can safely bypass this warning, or if you prefer, you can compile the client/server yourself directly from the source code.

| Tipo | File | OS | Version | Link |
| :--- | :--- | :--- | :--- | :--- |
| **Client** | `NOX-Setup-1.0.0.exe` | Windows | 1.0.0 | [Download](https://github.com/gabrielechi/Nox/releases/download/v1.0.0/NOX-Setup-1.0.0.exe) |
| **Server** | `LinuxServer.zip` | Linux | 1.0.0 | [Download](https://github.com/gabrielechi/Nox/releases/download/v1.0.0/LinuxServer.zip) |
| **Server** | `WinServer.zip` | Windows | 1.0.0 |  [Download](https://github.com/gabrielechi/Nox/releases/download/v1.0.0/WinServer.zip) |

## ⚙️​More about Nox
This project is a thesis prototype for asynchronous end-to-end encrypted file transfer.  
It implements a client-server architecture where the server is responsible for user management, public pre-key distribution, routing, and temporary-24 hours ciphertext storage, while file encryption and decryption are performed locally on the clients.

### 🔑Key Agreement Algorithm
The system uses an **X3DH-based** key agreement flow to derive a per-file encryption key, and each file is encrypted client-side before being uploaded to the server. As a result, the server stores only encrypted data and does not possess the private keys or file encryption keys required to decrypt the content. 

### 🧱Encrypted Vault and Offline-Attacks mitigation
User's private keys are locally encrypted in a vault before being sent to the server, this grants multi-device access to the account. 
The vault is encrypted using **AES-256-GCM**, while the encryption key is derived from the user's password using **Argon2id**, which is memory-hard, designed to resist **GPU/ASIC-based brute-force attacks**, making them more expensive than with traditional password hashing schemes.

>[!Note]
>**This is not intended to be a production-ready alternative to mature file transfer platforms. Some parts of the implementation are simplified or prototypical, and the main goal is academic: to demonstrate the design, security model, and practical implementation of an end-to-end encrypted file transfer system with an untrusted server for file content confidentiality.**
