using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kuskus.Api.Data;
using Kuskus.Api.Data.Entities;
using Kuskus.Api.Messaging;
using Kuskus.Api.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kuskus.Api.Auth;

public enum SendCodeResult
{
    Sent,
    TooManyCodes,
}

public enum ConfirmCodeResult
{
    Confirmed,
    Wrong,
    Expired,
}

/// <summary>
/// Confirms that a client owns a phone number with a one-time code sent by WhatsApp.
/// Only a keyed hash of each code is stored, codes expire after a few minutes, and the
/// number of codes and guesses per phone is limited.
/// </summary>
public class PhoneVerificationService(
    AppDbContext db, IWhatsAppSender whatsApp, IOptions<JwtOptions> jwt, TimeProvider time)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(10);
    public const int MaxCodesPerWindow = 3;
    public const int MaxAttempts = 5;

    private const string PhoneClaim = "phone";

    private string Audience => jwt.Value.Issuer + "/phone";

    /// <summary>Sends a new code. The code itself is returned too, for the test-only show-on-screen setting.</summary>
    public async Task<(SendCodeResult Result, string? Code)> SendCodeAsync(string phone, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Expired codes are cleaned up whenever a new one is requested.
        await db.LoginCodes.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(ct);

        var since = now - SendWindow;
        if (await db.LoginCodes.CountAsync(c => c.Phone == phone && c.CreatedAt > since, ct) >= MaxCodesPerWindow)
            return (SendCodeResult.TooManyCodes, null);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.LoginCodes.Add(new LoginCode
        {
            Phone = phone,
            CodeHash = Hash(phone, code),
            CreatedAt = now,
            ExpiresAt = now + CodeLifetime,
        });
        await db.SaveChangesAsync(ct);

        await whatsApp.SendAsync(phone, OrderMessages.LoginCode(code), ct);
        return (SendCodeResult.Sent, code);
    }

    /// <summary>Checks the code. On success the code is used up and a short-lived token proves the phone.</summary>
    public async Task<(ConfirmCodeResult Result, string? Token)> ConfirmAsync(string phone, string? code, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var latest = await db.LoginCodes
            .Where(c => c.Phone == phone)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (latest is null || latest.ExpiresAt < now)
            return (ConfirmCodeResult.Expired, null);
        if (latest.Attempts >= MaxAttempts)
            return (ConfirmCodeResult.Expired, null);

        latest.Attempts++;
        var matches = !string.IsNullOrWhiteSpace(code)
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(latest.CodeHash), Convert.FromHexString(Hash(phone, code.Trim())));
        if (!matches)
        {
            await db.SaveChangesAsync(ct);
            return (ConfirmCodeResult.Wrong, null);
        }

        db.LoginCodes.Remove(latest);
        await db.SaveChangesAsync(ct);
        return (ConfirmCodeResult.Confirmed, CreateToken(phone, now));
    }

    /// <summary>True when the token was issued by <see cref="ConfirmAsync"/> for this phone and is still fresh.</summary>
    public async Task<bool> IsVerifiedAsync(string? token, string phone)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = AdminTokenService.SigningKey(jwt.Value.Secret),
            ClockSkew = TimeSpan.FromMinutes(1),
        });
        return result.IsValid
            && result.Claims.TryGetValue(PhoneClaim, out var claim)
            && string.Equals(claim as string, phone, StringComparison.Ordinal);
    }

    private string CreateToken(string phone, DateTimeOffset now) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim(PhoneClaim, phone)]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + TokenLifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                AdminTokenService.SigningKey(jwt.Value.Secret), SecurityAlgorithms.HmacSha256),
        });

    private string Hash(string phone, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt.Value.Secret), Encoding.UTF8.GetBytes($"{phone}:{code}")));
}
