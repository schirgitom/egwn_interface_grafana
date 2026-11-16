# EGWN Interface Grafana

.NET 10 service for reconciling CentralAPI customer snapshots into Grafana organizations and dashboards. A Java Keycloak registration provider connects newly registered users to their customer using a separate secret registration code.

## Scope

- One organization per customer, with a customer overview, one dashboard per community and one per metering point.
- Customer updates replace the managed snapshot. Removed associations remove obsolete managed dashboards; customer deletion removes the managed organization.
- A common Keycloak realm; each user belongs to exactly one customer and receives Viewer access only.
- Reusable `registrationCode`: multiple users can register until the code is rotated or the customer deleted. Rotation preserves existing memberships.
- Measurement provisioning is opt-in. Until enabled, dashboards contain metadata and navigation, not SQL queries. With it enabled, each organization has its own PostgreSQL datasource and restricted measurement dashboards.
- The CentralAPI is a separate project and is not modified here.

## Projects

| Directory | Responsibility |
|---|---|
| `src\EGWNInterfaceGrafana.Domain` | Event contract and durable state |
| `src\EGWNInterfaceGrafana.Application` | Contract validation, snapshot merge rules and stable identities |
| `src\EGWNInterfaceGrafana.Infrastructure` | EF Core, RabbitMQ, Grafana and registration authorization |
| `src\EGWNInterfaceGrafana.Api` | Background consumer, authenticated internal API and configuration |
| `keycloak` | Java registration and login providers, theme, Docker image and setup |
| `tests` | Unit, real-infrastructure and browser integration tests |

Pinned integration targets are Grafana OSS **13.3.0**, Keycloak **26.8.2**, PostgreSQL **17**, RabbitMQ **4.2**, Java **21** and .NET **10**. The .NET SDK container supplies a stable SDK independently of the host installation. Rebuild and rerun integration tests when changing versions.

## RabbitMQ contract

| Setting | Default |
|---|---|
| Exchange | `egwncentral.events`, topic, durable |
| Queue | `grafana.customer-created.v1`, durable |
| Routing key for all customer events | `customer.created.v1` |
| Dead-letter queue | `grafana.customer-created.v1.dead`, durable |

The envelope contains `eventId` (GUID), `eventType`, `schemaVersion: 1`, `occurredAtUtc` (UTC) and `customer`. Accepted event types are `customer.created`, `customer.updated` and `customer.deleted`. `customer.isDeleted` must agree with the event type. Customer and resource IDs are GUIDs.

The full payload supplied by CentralAPI is supported, including `customer.meteringPoints`, `customer.energyCommunities` and community participation percentages. Add a separate field:

```json
{
  "customer": {
    "registrationCode": "<random-secret-generated-by-CentralAPI>"
  }
}
```

This is only the additional field, not a complete event. Generate at least 32 cryptographically random bytes and encode as hex or base64url. The service accepts 32-512 non-whitespace characters, but length validation alone cannot prove entropy. Do not use the customer number, a metering point, an email or a predictable password. Codes must be unique across active customers. A new random value in a subsequent snapshot rotates the code.

Historical missing/null codes preserve an existing code. A new customer without a code can be provisioned but cannot register users. Historical missing/null collections preserve previous collections; explicit empty arrays remove associations. Deleted customers retain a tombstone; IDs cannot be recycled.

### Delivery, ordering and recovery

The consumer uses manual ACK, prefetch 1, durable event-ID deduplication and PostgreSQL advisory locks across instances sharing the same service database. Duplicate IDs with changed content are rejected. Desired state and keyed code digests are committed before remote calls; registration remains closed while provisioning is pending or deletion incomplete. Redelivery resumes interrupted work using deterministic resource identities.

Transient failures retry up to `RabbitMQ:MaxAttempts` (default 5), with exponential backoff from `RetrySeconds` (default 2, capped at 60 seconds). Invalid or exhausted messages are copied to the durable dead-letter queue with publisher confirms before the original is acknowledged. Monitor that queue and the error logs; after repairing the cause, replay the original event, preserving its ID and content. Do not manufacture a new event ID to bypass a content conflict.

Older timestamps are ignored and equal-time conflicting events rejected. Timestamps are normalized to PostgreSQL microsecond precision. **This is not a total-order guarantee**: source clock skew and equal timestamps require a future monotonic per-customer revision from CentralAPI. A newer timestamp must never be treated as proof of causality across independent producer clocks.

RabbitMQ topology settings must agree with existing CentralAPI declarations. No new arguments are imposed on the existing source queue. The service account needs read/configure access plus write permission to the dead-letter queue. The source outbox and broker messages still contain the code; restrict access, require TLS, control retention and protect backups. No raw event bodies or registration codes are persisted in the service database or written to application logs.

There is no automatic backfill of CentralAPI customers. Publish snapshots for existing customers explicitly. Event ledger and deletion tombstones are retained; introduce retention only alongside a defined source replay horizon.

## Registration and Grafana authorization

Keycloak adds the registration-code field through the custom registration flow. The provider verifies it through the internal API, then confirms assignment using a short-lived signed ticket and the immutable Keycloak user ID. Tickets are rechecked against current customer state/code, so rotation or deletion during registration cannot authorize a stale assignment. Codes are reusable; there is no one-time consumption.

Keycloak and PostgreSQL do not share a transaction. If a local Keycloak registration rolls back after the backend mapping was committed, the orphan mapping cannot authorize an absent user; immutable IDs must never be reused. A required browser step rechecks active membership before Grafana authentication and refreshes the server-managed `egwn_org` attribute. Backend outages deny access, not bypass authorization. See the Keycloak setup instructions for the required profile restrictions and flow placement.

The service owns organization names `egwn-<customer-id-without-hyphens>`. Customer names appear in dashboard titles, so a rename never changes tenant identity. Reserve this namespace exclusively for the service; do not rename, create, reassign or reuse these organizations manually. Dashboards use stable organization-scoped hashed UIDs and ownership tags.

Grafana Generic OAuth does **not** dynamically resolve `*:*:Viewer` to a user's organization; that wildcard would grant access to all organizations. The service instead synchronizes explicit claim-to-organization mappings through the SSO settings API under a global database lock. Strict mapping plus the direct role `None` rejects missing or unauthorized claims. Do not edit the managed org mapping independently. Use a single provisioning database for each Grafana instance.

Grafana persists SSO settings in its database, overriding file/environment defaults. After initial provisioning, rotate the Grafana OAuth client secret in Keycloak **and in Grafana's SSO settings API**, as well as the deployment secret. Changing only `.env` and restarting Grafana is insufficient. Preserve the explicit organization mappings during this operation.

Customer deletion removes the Grafana organization, removes the managed Keycloak attribute and logs out affected Keycloak sessions. Other realm users are not deleted. Failed cleanup remains pending and retryable. Registration-code rotation does not revoke existing memberships. Per-user removal and account recovery/reassignment are administrative workflows outside this first implementation.

**Viewer is not a database isolation boundary.** Measurement access uses one PostgreSQL login and owned schema per customer, based on their immutable `customerNumber`. Only the customer's own `(community_id, meter_point_number)` pairs appear in raw measurement views; community totals and KPIs cover their authorized communities. A community total for a direction/hour is suppressed unless at least three *foreign* meters reported values for that hour. This reduces trivial inference by subtracting one's own data; it is not a formal differential-privacy guarantee. Neither the login nor Grafana receives SELECT on unrestricted Persistence tables. Public grants on TimescaleDB extension-owned metadata are excluded from the privilege check; non-extension tables and rollups, including Timescale-managed customer data, are still checked. Check inherited and PUBLIC grants before rollout.

### Measurement data source (opt-in)

The Persistence database (`egwndata2` on the current live test server) must have `public.meter_points("Id", meter_point_number, energy_direction)`, `public.meter_point_communities(meter_id, community_id)`, `public.raw_measurements(meter_id, community_id, timestamp, total_consumption, grid_share, community_share)` and `public.kpi_measurements(community_id, timestamp, autarky, own_consumption, community, feed, remaining_demand)`. `energy_direction` must contain `consumption`/`generation`; it identifies directions of other community participants for aggregate dashboards. Directions of the customer's own meters come from `energyDirection` in the RabbitMQ snapshot, with the same values. **The service does not migrate the Persistence database.** It uses `customer.energyCommunities[].sourceId` as the external `community_id`, and only meter IDs/codes also present in `customer.meteringPoints` count as customer-owned. A missing/duplicate `sourceId`, inconsistent ownership or unsupported direction rejects provisioning. Existing customer numbers must be unique, immutable and consist of at most 30 ASCII letters, numbers, `_` or `-` (starting with a letter/number); `-` becomes `_`. Collisions are rejected using schema ownership metadata.

Set `MEASUREMENT_ENABLED=true`, `PERSISTENCE_PROVISIONING_CONNECTION` (a dedicated PostgreSQL provisioning account able to create roles/schemas/views and terminate its own tenant sessions), `PERSISTENCE_DB_HOST=egwn-timescaledb`, `PERSISTENCE_DB_PORT=5432` and a distinct random `MEASUREMENT_CREDENTIAL_KEY` of at least 32 characters in the deployment's secret manager. The provisioning connection string must target the actual Persistence database (`egwndata2` for the current live test) **over the private Docker network**, not the server's public IP. Grafana must share that network. `Measurement__*` keys may instead be injected directly as environment variables. Do not put database credentials in RabbitMQ messages or commit them to `.env`. The credential key deterministically derives individual database passwords; protect and back it up. Rotating it requires reconciling all customer data sources and roles.

On each event the service disables the tenant login, terminates pooled connections, atomically refreshes scoped views, checks for PUBLIC or inherited access to other tables, creates/updates the Grafana datasource, imports the customer's overview and per-meter and per-community dashboards, then re-enables the login. Meter dashboards show consumption or generation totals, grid and community shares, and time series for each associated community; community dashboards show direction-specific totals and time series. If provisioning fails, the customer remains not ready and the tenant login remains disabled until retry. Deletion disables the login and terminates pooled connections before removing the Grafana organization, schema and role; retries tolerate an already removed tenant. The login owns neither schema nor views and receives only USAGE on its schema and SELECT on its views. Aggregates expose only `community_id`, hour, direction and totals; no individual foreign meter identifiers. No separate CentralAPI data source is used: customer metadata comes from the RabbitMQ snapshot. For a first rollout, use a test customer and verify the actual network, sourceId mapping, grants and data source connectivity before onboarding others.

## Configuration and deployment

Use Docker Compose on Ubuntu with Docker Engine and the Compose plugin. Copy `.env.example` to `.env`, generate independent random secrets, and supply actual domains/broker credentials. Never commit `.env`. Use base64url/hex secrets to avoid connection-string and interpolation delimiters. Environment variables are a deployment convenience; production secrets should come from a restricted secret manager/injected environment, with Docker daemon access restricted.

```powershell
docker compose build
docker compose up -d
docker compose ps
```

`compose.yml` includes separate service and Keycloak PostgreSQL databases, Grafana, Keycloak, a one-shot EF migrator, the service and Caddy for HTTPS. Only ports 80/443 are published; databases and application ports remain on the container network. RabbitMQ is external and requires trusted TLS certificates. Configure DNS for all three domains; the host and containers must be able to reach them, including the registration domain used by the provider. Caddy needs certificate issuance connectivity.

Configure the `egwn` realm, clients and required flows using the instructions in `keycloak\README.md` before enabling customer registrations. Match Grafana's client secret and the provisioning client's secret to `.env`. The provisioning service account needs user lookup/update and logout access in that realm. Replace/remove the temporary Keycloak bootstrap administrator after creating a permanent secured administrator. Restrict Keycloak administrative endpoints at the reverse proxy/firewall as appropriate.

The registration HTTPS route is authenticated with a distinct backend bearer token and rate limited. It is not a public customer API. Prefer restricting its network ingress to Keycloak in production. Do not add request-body/access-token logging at proxies. Tighten Keycloak's trusted proxy CIDRs to the actual deployment network.

Grafana organization administration requires server-admin Basic credentials; a regular organization-scoped service-account token cannot create organizations. Retain Basic authentication for that private provisioning path; customers authenticate through Keycloak. Protect the admin account with restricted ingress and independently managed credentials.

Important configuration keys:

| Prefix | Keys |
|---|---|
| `ConnectionStrings` | `Service` |
| `RabbitMQ` | `Enabled`, `HostName`, `Port`, `VirtualHost`, `UserName`, `Password`, `UseTls`, `Exchange`, `RoutingKey`, `Queue`, `MaxAttempts`, `RetrySeconds`, `MaxMessageBytes` |
| `Grafana` | `BaseUrl`, `AdminUser`, `AdminPassword` |
| `Measurement` | `Enabled`, `ProvisioningConnectionString`, `GrafanaDatabaseHost`, `GrafanaDatabasePort`, `CredentialKey` |
| `Keycloak` | `BaseUrl`, `Realm`, `ClientId`, `ClientSecret` |
| `Registration` | `InternalToken`, `HashKey` (distinct secrets, at least 32 characters) |
| `Consul` | `Enabled`, `Address`, `Key`, optional ACL `Token` |
| `Serilog` | Standard Serilog configuration |

Use double underscores in environment variables, e.g. `RabbitMQ__Password`. Consul loads a JSON object at startup and fails explicitly if unavailable when enabled. Environment variables override Consul. Reload requires a restart.

Run database migrations as the explicit one-shot `migrate` service before starting/upgrading consumers. `/health/live` reports process liveness; `/health/ready` checks PostgreSQL connectivity and RabbitMQ connectivity when consumption is enabled. Readiness does not assert that every remote Grafana/Keycloak operation will succeed. Monitor pending `Events.Completed=false`, `Customers.Ready=false`, broker dead letters and error logs.

Back up both PostgreSQL databases, the Grafana volume/database, Caddy certificate data and all secret-manager values. Restore them consistently before replaying broker messages. The registration hash key is required to verify existing codes/tickets; changing it requires new snapshots with new event IDs for all active customers. Do not rotate that key as if it were the per-customer code.

## Validation

With a .NET SDK that supports `net10.0`:

```powershell
dotnet test .\EGWNInterfaceGrafana.sln
docker build -t egwn-keycloak:26.8.2 .\keycloak
```

### Local IDE SDK selection

Like the neighboring EGWN solutions, this solution does not pin an SDK with
`global.json`. It targets `net10.0`, but lets Rider and `dotnet` use an installed
SDK capable of building .NET 10 projects. A stable .NET 10 SDK is recommended;
the .NET 10 Docker build remains independent of the host SDK. Reload the
solution in Rider after changing the installed SDKs or SDK selection.

Infrastructure tests use disposable, isolated containers and explicitly non-production credentials:

```powershell
docker compose -p egwn-grafana-tests -f compose.integration.yml up -d --wait postgres grafana rabbitmq
docker run --rm --network egwn-grafana-tests_default --env EGWN_INTEGRATION=true --mount "type=bind,source=$($PWD.Path),target=/work" -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test EGWNInterfaceGrafana.sln
```

Keep the API consumer stopped during these tests; each test owns a separate database but they share the disposable Grafana instance. Test locks intentionally do not coordinate across different databases. The RabbitMQ consumer test uses a separate exchange/routing key.

For an explicitly requested live smoke test against the **already running** `egwn-timescaledb`, `egwn-rabbitmq` and `egwn-grafana` containers, use `LiveEnvironmentTests.ProvisionsRealMeasurementsThroughRabbitMqAndCleansUp`. Supply `EGWN_LIVE_PROVISIONING_CONNECTION` (private-network connection to the Persistence database), `EGWN_LIVE_GRAFANA_USER`, `EGWN_LIVE_GRAFANA_PASSWORD`, `EGWN_LIVE_GRAFANA_HOST`, `EGWN_LIVE_RABBIT_USER`, `EGWN_LIVE_RABBIT_PASSWORD` and `EGWN_LIVE_RABBIT_HOST` securely in the shell environment, then run:

```powershell
docker run --rm --network egwn_egwn-network --env EGWN_LIVE_INTEGRATION=true `
  --env EGWN_LIVE_PROVISIONING_CONNECTION --env EGWN_LIVE_GRAFANA_USER --env EGWN_LIVE_GRAFANA_PASSWORD `
  --env EGWN_LIVE_GRAFANA_HOST --env EGWN_LIVE_RABBIT_USER --env EGWN_LIVE_RABBIT_PASSWORD `
  --env EGWN_LIVE_RABBIT_HOST --mount "type=bind,source=$($PWD.Path),target=/work" -w /work `
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test .\tests\EGWNInterfaceGrafana.Tests\EGWNInterfaceGrafana.Tests.csproj `
  --filter FullyQualifiedName~LiveEnvironmentTests.ProvisionsRealMeasurementsThroughRabbitMqAndCleansUp
```

This opt-in test uses an existing real meter/community reading in `egwndata2` without modifying source measurements. It publishes a synthetic customer snapshot through its **own** RabbitMQ exchange and queue, creates a disposable service-state database, tenant schema/role and Grafana organization, verifies the datasource and all nine generated panel queries (including a nonempty meter time series), then removes those test resources and restores the previous SSO mapping. It requires a working Grafana server-admin account, PostgreSQL create-database/role/schema and backend-termination privileges, and a populated `meter_points.energy_direction` with `consumption`/`generation`. Do not run it during a migration or concurrently with other Grafana SSO provisioning. It does not create, restart or remove the existing service containers; a failure during cleanup must be investigated before re-running. Never put the credentials in a committed file or the command line.

Browser tests under `tests\browser` exercise two registrations using the same code, actual Grafana Viewer membership, cross-customer denial, code rotation and customer deletion. They require the full integration Compose stack plus the configured Keycloak realm. Build that image and run it on the integration network after setup:

```powershell
docker compose -p egwn-grafana-tests -f compose.integration.yml up -d --build api keycloak
# Apply keycloak\setup.ps1 using the Docker-network command in keycloak\README.md.
# For this isolated stack only, use its documented integration administrator
# and client secrets: integration / integration-tests-only.
docker build -t egwn-grafana-browser:integration .\tests\browser
docker run --rm --network egwn-grafana-tests_default egwn-grafana-browser:integration
docker compose -p egwn-grafana-tests -f compose.integration.yml down -v
```

The integration credentials and HTTP transport are **only for disposable local tests**. Never expose this stack publicly or deploy `compose.integration.yml` as production.
