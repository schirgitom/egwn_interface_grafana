using System.Security.Cryptography;
using System.Text;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed record TenantDataSource(string Schema, string User, string Password, string Host, int Port, string Database);

public sealed class TenantMeasurementProvisioner(IOptions<MeasurementOptions> options)
{
    public bool Enabled => options.Value.Enabled;

    public async Task<TenantDataSource> ReconcileAsync(CustomerSnapshot customer, CancellationToken ct)
    {
        var schema = ResourceIdentity.TenantSchema(customer.CustomerNumber);
        var user = schema.Replace("grafana_customer_", "grafana_reader_", StringComparison.Ordinal);
        var settings = options.Value;
        var connectionSettings = new NpgsqlConnectionStringBuilder(settings.ProvisioningConnectionString);
        var password = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(settings.CredentialKey), Encoding.UTF8.GetBytes("postgres:" + customer.CustomerNumber)));
        await using (var preflight = new NpgsqlConnection(settings.ProvisioningConnectionString))
        {
            await preflight.OpenAsync(ct);
            await using var find = new NpgsqlCommand(
                "SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname=@schema", preflight);
            find.Parameters.AddWithValue("schema", schema);
            var existingOwner = (await find.ExecuteScalarAsync(ct))?.ToString();
            if (existingOwner is not null && existingOwner != customer.Id.ToString("N"))
                throw new InvalidEventException("Tenant schema name collides with an existing schema.");
            if (existingOwner is not null)
            {
                await using var disable = new NpgsqlCommand($"ALTER ROLE {user} NOLOGIN", preflight);
                await disable.ExecuteNonQueryAsync(ct);
                await TerminateConnectionsAsync(preflight, user, ct);
            }
        }
        var communities = customer.EnergyCommunities ?? [];
        if (communities.Any(c => string.IsNullOrWhiteSpace(c.SourceId)) ||
            communities.Select(c => c.SourceId).Distinct(StringComparer.Ordinal).Count() != communities.Count)
            throw new InvalidEventException("Each energy community must have a unique Persistence sourceId.");
        var ownMeters = (customer.MeteringPoints ?? []).ToDictionary(m => m.Id);
        var pairs = communities.SelectMany(c => c.MeteringPoints.Select(p => (Community: c.SourceId!,
            Meter: p.MeteringPointCode, Id: p.Id))).ToList();
        if (pairs.Any(p => !ownMeters.TryGetValue(p.Id, out var own) || own.MeteringPointCode != p.Meter) ||
            ownMeters.Values.Any(p => p.EnergyDirection is not ("consumption" or "generation")))
            throw new InvalidEventException("Community meter points must belong to the customer snapshot.");
        await using var connection = new NpgsqlConnection(settings.ProvisioningConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        async Task<string?> ScalarAsync(string sql, params NpgsqlParameter[] parameters)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            cmd.Parameters.AddRange(parameters);
            return (await cmd.ExecuteScalarAsync(ct))?.ToString();
        }
        async Task ExecuteAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        async Task<string> LiteralAsync(string value) =>
            (await ScalarAsync("SELECT quote_literal(@value)", new NpgsqlParameter("value", value)))!;

        var owner = await ScalarAsync("SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname=@name",
            new NpgsqlParameter("name", schema));
        if (owner is not null && owner != customer.Id.ToString("N"))
            throw new InvalidEventException("Tenant schema name collides with an existing schema.");
        var roleExists = await ScalarAsync("SELECT 1 FROM pg_roles WHERE rolname=@name",
            new NpgsqlParameter("name", user));
        if (roleExists is not null && owner is null)
            throw new InvalidEventException("Tenant database role name is already in use.");
        if (roleExists is null)
            await ExecuteAsync($"CREATE ROLE {user} NOLOGIN NOINHERIT PASSWORD {await LiteralAsync(password)}");
        else
            await ExecuteAsync($"ALTER ROLE {user} NOLOGIN NOINHERIT PASSWORD {await LiteralAsync(password)}");
        if (owner is null)
        {
            await ExecuteAsync($"CREATE SCHEMA {schema}");
            await ExecuteAsync($"COMMENT ON SCHEMA {schema} IS {await LiteralAsync(customer.Id.ToString("N"))}");
        }
        await ExecuteAsync($"REVOKE ALL ON SCHEMA {schema} FROM PUBLIC");
        await ExecuteAsync($"REVOKE ALL ON ALL TABLES IN SCHEMA {schema} FROM PUBLIC");

        var communityValues = communities.Count == 0 ? "NULL" :
            string.Join(", ", await Task.WhenAll(communities.Select(c => LiteralAsync(c.SourceId!))));
        var pairValues = new List<string>();
        foreach (var (community, meter, _) in pairs)
            pairValues.Add($"({await LiteralAsync(community)}, {await LiteralAsync(meter)})");
        var meterFilter = pairValues.Count == 0 ? "FALSE" :
            $"(mc.community_id, mp.meter_point_number) IN (VALUES {string.Join(", ", pairValues)})";
        await ExecuteAsync($"""
            CREATE OR REPLACE VIEW {schema}.meter_point_communities WITH (security_barrier=true) AS
            SELECT mc.meter_id, mc.community_id
            FROM public.meter_point_communities mc JOIN public.meter_points mp ON mp."Id" = mc.meter_id
            WHERE mc.community_id IN ({communityValues}) AND {meterFilter}
            """);
        await ExecuteAsync($"""
            CREATE OR REPLACE VIEW {schema}.meter_points WITH (security_barrier=true) AS
            SELECT DISTINCT mp."Id", mp.meter_point_number
            FROM public.meter_points mp
            JOIN {schema}.meter_point_communities mc ON mc.meter_id = mp."Id"
            """);
        await ExecuteAsync($"""
            CREATE OR REPLACE VIEW {schema}.raw_measurements WITH (security_barrier=true) AS
            SELECT r.meter_id, r.community_id, r.timestamp, r.total_consumption, r.grid_share, r.community_share
            FROM public.raw_measurements r
            JOIN {schema}.meter_point_communities mc
              ON mc.meter_id = r.meter_id AND mc.community_id = r.community_id
            """);
        await ExecuteAsync($"""
            CREATE OR REPLACE VIEW {schema}.kpi_measurements WITH (security_barrier=true) AS
            SELECT community_id, timestamp, autarky, own_consumption, community, feed, remaining_demand
            FROM public.kpi_measurements WHERE community_id IN ({communityValues})
            """);
        await ExecuteAsync($"""
            CREATE OR REPLACE VIEW {schema}.community_totals WITH (security_barrier=true) AS
            SELECT r.community_id, r.timestamp, mp.energy_direction,
                   SUM(r.total_consumption) AS total, SUM(r.grid_share) AS grid_share,
                   SUM(r.community_share) AS community_share
            FROM public.raw_measurements r JOIN public.meter_points mp ON mp."Id" = r.meter_id
            WHERE r.community_id IN ({communityValues})
            GROUP BY r.community_id, r.timestamp, mp.energy_direction
            HAVING COUNT(DISTINCT r.meter_id) FILTER (WHERE NOT EXISTS (
                SELECT 1 FROM {schema}.meter_point_communities own
                WHERE own.meter_id = r.meter_id AND own.community_id = r.community_id
            )) >= 3
            """);
        await ExecuteAsync($"GRANT USAGE ON SCHEMA {schema} TO {user}");
        await ExecuteAsync($"GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {user}");

        // PUBLIC grants and inherited roles can bypass a per-customer view even without direct grants.
        var unsafeGrants = await ScalarAsync("""
            SELECT count(*) FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', @schema)
              AND n.nspname NOT LIKE 'pg_toast%'
              AND n.nspname NOT LIKE 'pg_temp_%'
              AND r.relkind IN ('r','p','v','m','f')
              AND has_table_privilege(@role, r.oid, 'SELECT')
              AND NOT EXISTS (
                  SELECT 1 FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
                  WHERE d.classid='pg_class'::regclass AND d.objid=r.oid
                    AND d.refclassid='pg_extension'::regclass AND d.deptype='e'
                    AND e.extname='timescaledb'
              )
            """, new NpgsqlParameter("schema", schema), new NpgsqlParameter("role", user));
        var roleMemberships = await ScalarAsync("""
            SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member WHERE r.rolname=@role
            """, new NpgsqlParameter("role", user));
        if (unsafeGrants != "0" || roleMemberships != "0")
            throw new InvalidOperationException("Tenant role has unexpected inherited or public database access.");
        await tx.CommitAsync(ct);
        await TerminateConnectionsAsync(connection, user, ct);
        return new(schema, user, password, settings.GrafanaDatabaseHost, settings.GrafanaDatabasePort,
            connectionSettings.Database!);
    }

    public async Task EnableAsync(string customerNumber, Guid customerId, CancellationToken ct)
    {
        var schema = ResourceIdentity.TenantSchema(customerNumber);
        var user = schema.Replace("grafana_customer_", "grafana_reader_", StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(options.Value.ProvisioningConnectionString);
        await connection.OpenAsync(ct);
        await using var check = new NpgsqlCommand(
            "SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname=@schema", connection);
        check.Parameters.AddWithValue("schema", schema);
        if ((await check.ExecuteScalarAsync(ct))?.ToString() != customerId.ToString("N"))
            throw new InvalidEventException("Tenant schema ownership changed.");
        await using var enable = new NpgsqlCommand($"ALTER ROLE {user} LOGIN", connection);
        await enable.ExecuteNonQueryAsync(ct);
    }

    private static async Task TerminateConnectionsAsync(NpgsqlConnection connection, string user, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT bool_and(pg_terminate_backend(a.pid))
            FROM pg_stat_activity a WHERE a.usename = @role AND a.pid <> pg_backend_pid()
            """, connection);
        cmd.Parameters.AddWithValue("role", user);
        if (await cmd.ExecuteScalarAsync(ct) is false)
            throw new InvalidOperationException("Could not terminate previous tenant database sessions.");
    }

    public async Task DeleteAsync(string customerNumber, Guid customerId, CancellationToken ct)
    {
        var schema = ResourceIdentity.TenantSchema(customerNumber);
        var user = schema.Replace("grafana_customer_", "grafana_reader_", StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(options.Value.ProvisioningConnectionString);
        await connection.OpenAsync(ct);
        await using (var preflightVerify = new NpgsqlCommand(
            "SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname=@schema", connection))
        {
            preflightVerify.Parameters.AddWithValue("schema", schema);
            var preflightOwner = (await preflightVerify.ExecuteScalarAsync(ct))?.ToString();
            if (preflightOwner is not null && preflightOwner != customerId.ToString("N"))
                throw new InvalidEventException("Refusing to delete a schema belonging to another customer.");
            if (preflightOwner is not null)
            {
                await using var disable = new NpgsqlCommand($"ALTER ROLE {user} NOLOGIN", connection);
                await disable.ExecuteNonQueryAsync(ct);
                await TerminateConnectionsAsync(connection, user, ct);
            }
        }
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var verify = new NpgsqlCommand(
            "SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname=@schema", connection, tx);
        verify.Parameters.AddWithValue("schema", schema);
        var owner = (await verify.ExecuteScalarAsync(ct))?.ToString();
        if (owner is not null && owner != customerId.ToString("N"))
            throw new InvalidEventException("Refusing to delete a schema belonging to another customer.");
        if (owner is not null)
        {
            await using var dropViews = new NpgsqlCommand($"""
                DROP VIEW {schema}.raw_measurements, {schema}.meter_points,
                    {schema}.meter_point_communities, {schema}.kpi_measurements,
                    {schema}.community_totals
                """, connection, tx);
            await dropViews.ExecuteNonQueryAsync(ct);
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA {schema}", connection, tx);
            await dropSchema.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var checkRole = new NpgsqlCommand(
                "SELECT 1 FROM pg_roles WHERE rolname=@role", connection, tx);
            checkRole.Parameters.AddWithValue("role", user);
            if (await checkRole.ExecuteScalarAsync(ct) is not null)
                throw new InvalidEventException("Refusing to delete a role without its owned tenant schema.");
        }
        await using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS {user}", connection, tx);
        await dropRole.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
