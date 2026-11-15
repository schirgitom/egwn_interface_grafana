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
    public static string Dashboard(Guid customerId, string kind, Guid resourceId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{customerId:N}:{kind}:{resourceId:N}"));
        return "egwn-" + Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }
}
