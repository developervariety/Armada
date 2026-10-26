namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text.Json;
    using Armada.Core.Models;

    public partial class SessionTokenService
    {
        private AuthContext? ValidateAskTurnToken(string token)
        {
            try
            {
                byte[] tokenBytes = Convert.FromBase64String(token.Substring(AskTokenPrefix.Length));
                const int nonceLength = 12;
                const int tagLength = 16;
                if (tokenBytes.Length <= nonceLength + tagLength) return null;

                int ciphertextLength = tokenBytes.Length - nonceLength - tagLength;
                byte[] plaintext = new byte[ciphertextLength];
                byte[] key = DeriveAskTokenKey();
                try
                {
                    using (AesGcm aes = new AesGcm(key, tagLength))
                        aes.Decrypt(
                            tokenBytes.AsSpan(0, nonceLength),
                            tokenBytes.AsSpan(nonceLength, ciphertextLength),
                            tokenBytes.AsSpan(nonceLength + ciphertextLength, tagLength),
                            plaintext,
                            AskTokenAssociatedData);

                    SessionPayload? payload = JsonSerializer.Deserialize<SessionPayload>(plaintext);
                    if (payload == null || String.IsNullOrWhiteSpace(payload.TenantId) || String.IsNullOrWhiteSpace(payload.UserId)
                        || payload.ExpiresUtc <= DateTime.UtcNow
                        || String.IsNullOrWhiteSpace(payload.AskThreadId) || String.IsNullOrWhiteSpace(payload.AskTurnId)
                        || !String.IsNullOrWhiteSpace(payload.MissionId)) return null;

                    return new AuthContext
                    {
                        IsAuthenticated = true,
                        TenantId = payload.TenantId,
                        UserId = payload.UserId,
                        IsAdmin = false,
                        IsTenantAdmin = false,
                        AuthMethod = "Session",
                        CredentialId = null,
                        PrincipalDisplay = payload.UserId,
                        AskThreadId = payload.AskThreadId,
                        AskTurnId = payload.AskTurnId
                    };
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch
            {
                return null;
            }
        }

    }
}
