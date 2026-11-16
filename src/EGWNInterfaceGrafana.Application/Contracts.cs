using EGWNInterfaceGrafana.Domain;

namespace EGWNInterfaceGrafana.Application;

public interface IGrafanaProvisioner
{
    Task<long> ReconcileAsync(CustomerSnapshot customer, long? organizationId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid customerId, long? organizationId, CancellationToken cancellationToken);
}

public interface IIdentityRevoker
{
    Task RevokeAsync(string userId, CancellationToken cancellationToken);
}

public sealed class InvalidEventException(string message) : Exception(message);
public sealed class RegistrationDeniedException : Exception;

public static class ResourceIdentity
{
    public static string Organization(Guid customerId) => $"egwn-{customerId:N}";
    public static string OwnerTag(Guid customerId) => $"egwn-managed-{customerId:N}";
    public static string TenantSchema(string customerNumber)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(customerNumber, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,29}\z"))
            throw new InvalidEventException("Customer number cannot be safely used as a database identifier.");
        return "grafana_customer_" + customerNumber.ToLowerInvariant().Replace('-', '_');
    }

    public static string Dashboard(Guid customerId, string kind, Guid resourceId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{customerId:N}:{kind}:{resourceId:N}"));
        return "egwn-" + Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }
}
