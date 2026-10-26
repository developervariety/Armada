namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;

    public partial class SessionTokenService
    {
        private const string AskTokenPrefix = "ask1.";
        private static readonly byte[] AskTokenKeyPurpose = Encoding.UTF8.GetBytes("armada-ask-session-v1");
        private static readonly byte[] AskTokenAssociatedData = Encoding.UTF8.GetBytes("armada-session-token:ask1");

        internal static bool HasAskTokenFormat(string? token)
        {
            return !String.IsNullOrEmpty(token) && token.StartsWith(AskTokenPrefix, StringComparison.Ordinal);
        }

        private AuthenticateResult CreateAskTurnTokenCore(string tenantId, string userId, string threadId, string turnId)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));

            DateTime expiresUtc = DateTime.UtcNow.AddHours(Constants.SessionTokenLifetimeHours);
            SessionPayload payload = new SessionPayload
            {
                TenantId = tenantId,
                UserId = userId,
                AskThreadId = threadId,
                AskTurnId = turnId,
                ExpiresUtc = expiresUtc
            };

            byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            byte[] nonce = new byte[12];
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            byte[] key = DeriveAskTokenKey();
            try
            {
                RandomNumberGenerator.Fill(nonce);
                using (AesGcm aes = new AesGcm(key, tag.Length))
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, AskTokenAssociatedData);

                byte[] tokenBytes = new byte[nonce.Length + ciphertext.Length + tag.Length];
                Buffer.BlockCopy(nonce, 0, tokenBytes, 0, nonce.Length);
                Buffer.BlockCopy(ciphertext, 0, tokenBytes, nonce.Length, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, tokenBytes, nonce.Length + ciphertext.Length, tag.Length);
                return new AuthenticateResult
                {
                    Success = true,
                    Token = AskTokenPrefix + Convert.ToBase64String(tokenBytes),
                    ExpiresUtc = expiresUtc
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private byte[] DeriveAskTokenKey()
        {
            using (HMACSHA256 hmac = new HMACSHA256(_Key))
                return hmac.ComputeHash(AskTokenKeyPurpose);
        }
    }
}
