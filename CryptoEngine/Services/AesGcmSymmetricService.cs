using CryptoEngine.Interfaces;
using Sodium;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CryptoEngine.Services
{
    public class AesGcmSymmetricService : ISymmetricService
    {
        private const int NonceLength = 12; // 96 bits is standard for AES-GCM
        private const int TagLength = 16; // 128 bits is standard for AES-GCM

        public byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(32); // AES-256 key length (32 bytes)
        }

        public byte[] Encrypt(byte[] plaintext, byte[] key)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagLength];

            using var aesGcm = new AesGcm(key, TagLength);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

            byte[] encryptedData = new byte[NonceLength + TagLength + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, encryptedData, 0, NonceLength);
            Buffer.BlockCopy(tag, 0, encryptedData, NonceLength, TagLength);
            Buffer.BlockCopy(ciphertext, 0, encryptedData, NonceLength + TagLength, ciphertext.Length);

            return encryptedData;
        }

        public byte[] Decrypt(byte[] encryptedData, byte[] key)
        {
            if (encryptedData.Length <= NonceLength + TagLength)
                throw new ArgumentException("Ciphertext is too short");

            byte[] nonce = encryptedData[..NonceLength];
            byte[] tag = encryptedData[NonceLength..(NonceLength + TagLength)];
            byte[] ciphertext = encryptedData[(NonceLength + TagLength)..];
            byte[] plaintext = new byte[ciphertext.Length];

            using var aesGcm = new AesGcm(key, TagLength);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            // If tag verification fails, an AuthenticationMismatchException will be thrown automatically by AesGcm.Decrypt

            return plaintext;
        }
    }
}
