using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EGWNInterfaceGrafana.Application;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class RegistrationSecrets(IOptions<RegistrationOptions> options, TimeProvider time)
{
    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.HashKey);
    public string Hash(string code) =>
        Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("code:" + code)));

    public string CreateTicket(Guid customerId, string codeHash)
    {
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Ticket(customerId, codeHash, time.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds())));
        return payload + "." + Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("ticket:" + payload)));
    }

    public Ticket ReadTicket(string value)
    {
        if (value.Length > 2048) throw new RegistrationDeniedException();
        var parts = value.Split('.');
        if (parts.Length != 2) throw new RegistrationDeniedException();
        try
        {
            var actual = Convert.FromBase64String(parts[1]);
            var expected = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("ticket:" + parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new RegistrationDeniedException();
            var ticket = JsonSerializer.Deserialize<Ticket>(Convert.FromBase64String(parts[0]));
            if (ticket is null || ticket.ExpiresAt <= time.GetUtcNow().ToUnixTimeSeconds())
                throw new RegistrationDeniedException();
            return ticket;
        }
        catch (FormatException) { throw new RegistrationDeniedException(); }
        catch (JsonException) { throw new RegistrationDeniedException(); }
    }

    public bool Authenticate(string token) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)),
        SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.InternalToken)));

    public sealed record Ticket(Guid CustomerId, string CodeHash, long ExpiresAt);
}
