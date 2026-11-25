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

The envelope contains `eventId` (GUID), `eventType`, `schemaVersion: 1`, `occurredAtUtc` (an ISO 8601 timestamp normalized to UTC on ingestion) and `customer`. Accepted event types are `customer.created`, `customer.updated` and `customer.deleted`. `customer.isDeleted` must agree with the event type. Customer and resource IDs are GUIDs.

As an alternative to RabbitMQ, send **only the `customer` object** as JSON to `POST /internal/customers` with `Authorization: Bearer <Registration:InternalToken>` and `Content-Type: application/json`. The server generates `eventId`, `eventType` (from `isDeleted`) and a UTC timestamp, returning `eventId` on success. Under the per-customer lock, identical successfully processed snapshots are no-ops; an unfinished identical request resumes its event when possible. Invalid snapshots return HTTP 400, and provisioning failures return an error rather than acknowledging success. Requests are limited to 1 MiB. Use **one delivery channel per customer**: source timestamps from RabbitMQ and server-generated HTTP timestamps have no shared revision ordering. Keep this endpoint on a trusted network; the shared internal token grants provisioning access as well as registration access. It does not publish to RabbitMQ or depend on a running RabbitMQ consumer.

The full payload supplied by CentralAPI is supported, including `customer.meteringPoints`, `customer.energyCommunities` and community participation percentages. An optional `logoUrl` on an energy community displays its logo on the associated customer dashboards; only HTTPS URLs without embedded credentials are accepted. The standalone `dashboard.json` reads `EnergyCommunities."LogoUrl"` from its metadata datasource. The RabbitMQ consumer discards any `customer.registrationCode` before validation, hashing or persistence: CentralAPI owns its human-readable registration codes and Keycloak memberships, not this interface. The independent, trusted `POST /internal/customers` endpoint still requires a high-entropy (32-512 character) code if its legacy local registration flow is used. Historical missing/null collections preserve previous collections; explicit empty arrays remove associations. Deleted customers retain a tombstone; IDs cannot be recycled.

### Delivery, ordering and recovery

The consumer uses manual ACK, prefetch 1, durable event-ID deduplication and PostgreSQL advisory locks across instances sharing the same service database. Duplicate IDs with changed content are rejected. Desired state is committed before remote calls; redelivery resumes interrupted work using deterministic resource identities. CentralAPI remains responsible for authorization to register and for active user memberships.

Transient failures retry up to `RabbitMQ:MaxAttempts` (default 5), with exponential backoff from `RetrySeconds` (default 2, capped at 60 seconds). Invalid or exhausted messages are copied to the durable dead-letter queue with publisher confirms before the original is acknowledged. Monitor that queue and the error logs; after repairing the cause, replay the original event, preserving its ID and content. Do not manufacture a new event ID to bypass a content conflict.

Retry logs include the exception message, inner exceptions and stack trace, plus the broker message ID (when supplied) and queue. Grafana and Persistence HTTP status failures include the service, method and target URL without URL credentials or query parameters. Request/response bodies and authorization headers are not logged because provisioning can contain passwords. An HTTP 500 without server-side details still requires the corresponding upstream service logs.

Older timestamps are ignored and equal-time conflicting events rejected. Timestamps are normalized to PostgreSQL microsecond precision. **This is not a total-order guarantee**: source clock skew and equal timestamps require a future monotonic per-customer revision from CentralAPI. A newer timestamp must never be treated as proof of causality across independent producer clocks.

RabbitMQ topology settings must agree with existing CentralAPI declarations. No new arguments are imposed on the existing source queue. The service account needs read/configure access plus write permission to the dead-letter queue. The source outbox and broker messages may still contain the code; restrict access, require TLS, control retention and protect backups. No raw event bodies or CentralAPI registration codes are persisted in the service database or written to application logs.

There is no automatic backfill of CentralAPI customers. Publish snapshots for existing customers explicitly. Event ledger and deletion tombstones are retained; introduce retention only alongside a defined source replay horizon.

## Registration and Grafana authorization

Keycloak adds the registration-code field through the custom registration flow. In the current deployment its backend is **CentralAPI**, which validates the short code and owns the user-to-customer assignment. The Grafana interface consumes customer events to create the matching organization and explicit SSO mapping but does not validate or store CentralAPI registration codes. The optional local `/internal/registration` API uses separate high-entropy credentials and is not the backend for the current Keycloak deployment.

Keycloak and PostgreSQL do not share a transaction. If a local Keycloak registration rolls back after the backend mapping was committed, the orphan mapping cannot authorize an absent user; immutable IDs must never be reused. A required browser step rechecks active membership before Grafana authentication and refreshes the server-managed `egwn_org` attribute. Backend outages deny access, not bypass authorization. See the Keycloak setup instructions for the required profile restrictions and flow placement.

The service displays customer organizations as `<customerNumber> - <customerName>` and updates the name when the customer is renamed. The immutable `egwn-<customer-id-without-hyphens>` identity remains the Keycloak claim and SSO mapping key; the Grafana organization ID and dashboard UIDs remain stable across renames. Existing `egwn-...` Grafana organizations are renamed on their next customer reconciliation. Do not manually rename, create, reassign or reuse managed organizations. Dashboards use stable organization-scoped hashed UIDs and ownership tags.

Grafana Generic OAuth does **not** dynamically resolve `*:*:Viewer` to a user's organization; that wildcard would grant access to all organizations. The service instead synchronizes explicit claim-to-organization mappings through the SSO settings API under a global database lock. Strict mapping plus the direct role `None` rejects missing or unauthorized claims. Do not edit the managed org mapping independently. Use a single provisioning database for each Grafana instance.

Grafana persists SSO settings in its database, overriding file/environment defaults. After initial provisioning, rotate the Grafana OAuth client secret in Keycloak **and in Grafana's SSO settings API**, as well as the deployment secret. Changing only `.env` and restarting Grafana is insufficient. Preserve the explicit organization mappings during this operation.

Customer organizations open their first energy community's **Energiegemeinschaft · Übersicht (gesamt)** dashboard by default. Other community and direction-labeled metering-point dashboards are available through the dashboard links. The former full-height customer information overview is no longer created. Without any community, a compact empty-state overview remains the home dashboard. Meter dashboards use own 15-minute measurements; community dashboards use hourly aggregates of **all** community members, separated by generation and consumption. Peak and average power are calculated from each source's actual interval (15 minutes versus one hour). The dashboards show the energy split, EG coverage, interval and daily peak/average power, weekday/weekend hourly profile, previous-week comparison and top 10 peaks, with two decimal places. EG coverage and peak cards use fixed colors; a provided community logo appears once per community dashboard. The heatmap, hourly time-of-day panel, completeness/quality panels and load-duration curve are omitted.

Customer deletion removes the Grafana organization and its SSO mapping; CentralAPI owns the corresponding membership lifecycle. The interface revokes sessions for any users recorded in its optional local registration store. Failed cleanup remains pending and retryable. Per-user removal and account recovery/reassignment are administrative workflows outside this first implementation.

**Viewer is not a database isolation boundary.** Persistence owns the PostgreSQL login, scoped schema, views and database grants for each stable customer UUID. The Grafana interface sends only authorized community IDs and `(communityId, meterPointNumber)` pairs from the CentralAPI snapshot. Persistence must enforce isolation in the database: own-meter raw measurements, community KPIs and direction-specific aggregates without leaking individual foreign meters. The Grafana login must not receive SELECT on unrestricted Persistence tables or rollups. Check PUBLIC and inherited grants before rollout.

### Measurement data source (opt-in)

The Persistence database (`egwndata2` on the current server) supplies the scoped views `raw_measurements`, `meter_points`, `meter_point_communities`, `kpi_measurements` and `community_hourly_measurements` under the schema returned by its REST API. The latter must expose `timestamp`, `community_id`, `energy_direction`, `total_consumption`, `grid_share` and `community_share` with hourly kWh aggregates for **all** meters in each authorized community, without foreign meter identifiers. Community dashboards query only this view, filtering by technical `community_id` and direction; meter dashboards query the own-meter views. Persistence must grant SELECT on the aggregate only to roles entitled to that community. The Grafana interface never migrates or directly administers the Persistence database. It uses `customer.energyCommunities[].sourceId` as the technical `community_id` and accepts own-meter pairs only when the meter ID and code also occur in `customer.meteringPoints`; missing/duplicate source IDs and inconsistent ownership reject provisioning.

Set `MEASUREMENT_ENABLED=true`, `PERSISTENCE_API_URL=https://egwnpersistence-worker:8443`, a distinct random `PERSISTENCE_SERVICE_TOKEN` of at least 32 characters, `PERSISTENCE_DB_HOST=egwn-timescaledb`, `PERSISTENCE_DB_PORT=5432` and `PERSISTENCE_DB_NAME=egwndata2`. The API endpoint must be reachable over HTTPS with a trusted certificate matching its hostname; never disable TLS verification. The database host/port/name are for **Grafana's** connection from its container, not the service's own SQL connection. `Measurement__*` keys may instead be injected directly as environment variables. Store the service token in a secret manager, not in RabbitMQ messages or source control.

On each event the service calls `POST /internal/grafana/customers/{customerId}/credentials` (200 with `username`, `schema`, `password`) and `PUT .../entitlements` (204 with the complete authorized `communities` and `meters` list). Persistence rotates the password on **every** credentials POST, disconnects old sessions, and leaves login disabled until a successful entitlements PUT. The service then updates the customer organization's Grafana PostgreSQL datasource with the new password in `secureJsonData` and imports the dashboards using the returned fixed schema. Retries must repeat **both** REST calls and update Grafana with the latest password, including after a timeout. No password is persisted in the service database or dashboard JSON. Customer deletion calls `POST /internal/grafana/customers/{customerId}/disable` (204) before removing the Grafana organization; a 404 or other error is not treated as success. Meter dashboards show consumption or generation for the customer's own meter; community overviews show direction-specific totals for all community members. No separate CentralAPI datasource is used: customer metadata comes from the RabbitMQ snapshot. For a first rollout, verify the Persistence API contract, sourceId mapping, grants and datasource connectivity with a test customer.

## Configuration and deployment

Use Docker Compose on Ubuntu with Docker Engine and the Compose plugin. Copy `.env.example` to `.env`, generate independent random secrets, and supply actual domains/broker credentials. Never commit `.env`. Use base64url/hex secrets to avoid connection-string and interpolation delimiters. Environment variables are a deployment convenience; production secrets should come from a restricted secret manager/injected environment, with Docker daemon access restricted.

```powershell
docker compose build
docker compose up -d
docker compose ps
```

`compose.yml` includes separate service and Keycloak PostgreSQL databases, Grafana, Keycloak, a one-shot EF migrator, the service and Caddy for HTTPS. Only ports 80/443 are published; databases and application ports remain on the container network. RabbitMQ is external and requires trusted TLS certificates. Configure DNS for all three domains; the host and containers must be able to reach them, including the registration domain used by the provider. Caddy needs certificate issuance connectivity.

The API has a `Properties\launchSettings.json` profile for Rider/`dotnet run`. It sets the development environment and local HTTP address only; it does not supply credentials. `src\EGWNInterfaceGrafana.Api\appsettings.json` lists all local option names with non-secret defaults and empty secret placeholders. `ConnectionStrings:Service` must point to a **separate, migrated interface service database**, never the Persistence `egwndata2` measurement database. `Grafana:BaseUrl` defaults to `http://localhost:3000`; `Measurement:ApiBaseUrl` is the trusted HTTPS Persistence API, and `Measurement:ServiceToken` authenticates the interface to it. `Measurement:GrafanaDatabaseHost`/`GrafanaDatabasePort`/`GrafanaDatabaseName` are the **separate address Grafana uses** for its tenant datasource. Do **not** fill secret placeholders in the tracked `appsettings.json`: store the database connection string, Grafana password, Persistence token, Keycloak client secret, registration keys and RabbitMQ credentials outside the repository via User Secrets or environment variables. The current API settings enable measurements and reject startup until the token is supplied; set `Measurement:Enabled=false` only for metadata-only dashboards. Compose instead uses `GRAFANA_ADMIN_*` and `MEASUREMENT_ENABLED` from its environment, with measurements off by default. For local IDE runs, use `dotnet user-secrets set --project .\src\EGWNInterfaceGrafana.Api <key> <value>`; Rider must run in Development to load User Secrets. Leave `RabbitMQ:Enabled=false` until the service database is migrated and broker credentials are configured; enable it only when intentionally consuming the queue. When running on the host, configure a trusted HTTPS URL reachable from that host for `Measurement:ApiBaseUrl`; the Docker-only hostname `egwnpersistence-worker` may not resolve there. Grafana still uses its private database host inside Docker.

Configure the `egwn` realm, clients and required flows using the instructions in `keycloak\README.md` before enabling customer registrations. Match Grafana's client secret and the provisioning client's secret to `.env`. The provisioning service account needs user lookup/update and logout access in that realm. Replace/remove the temporary Keycloak bootstrap administrator after creating a permanent secured administrator. Restrict Keycloak administrative endpoints at the reverse proxy/firewall as appropriate.

The registration HTTPS route is authenticated with a distinct backend bearer token and rate limited. It is not a public customer API. Prefer restricting its network ingress to Keycloak in production. Do not add request-body/access-token logging at proxies. Tighten Keycloak's trusted proxy CIDRs to the actual deployment network.

Grafana organization administration requires server-admin Basic credentials; a regular organization-scoped service-account token cannot create organizations. Retain Basic authentication for that private provisioning path; customers authenticate through Keycloak. Protect the admin account with restricted ingress and independently managed credentials.

Important configuration keys:

| Prefix | Keys |
|---|---|
| `ConnectionStrings` | `Service` |
| `RabbitMQ` | `Enabled`, `HostName`, `Port`, `VirtualHost`, `UserName`, `Password`, `UseTls`, `Exchange`, `RoutingKey`, `Queue`, `MaxAttempts`, `RetrySeconds`, `MaxMessageBytes` |
| `Grafana` | `BaseUrl`, `AdminUser`, `AdminPassword` |
| `Measurement` | `Enabled`, `ApiBaseUrl`, `ServiceToken`, `GrafanaDatabaseHost`, `GrafanaDatabasePort`, `GrafanaDatabaseName` |
| `Keycloak` | `BaseUrl`, `Realm`, `ClientId`, `ClientSecret` |
| `Registration` | `InternalToken`, `HashKey` (distinct secrets, at least 32 characters) |
| `Consul` | `Enabled`, `Address`, `Key`, optional ACL `Token` |
| `Serilog` | Standard Serilog configuration |

Use double underscores in environment variables, e.g. `RabbitMQ__Password`. Consul loads a JSON object at startup and fails explicitly if unavailable when enabled. Environment variables override Consul. Reload requires a restart.

Run database migrations as the explicit one-shot `migrate` service before starting/upgrading consumers. Rider's EF **Update Database** reads `ConnectionStrings:Service` from the API `appsettings.json`, overridden by the API project's User Secrets and then `ConnectionStrings__Service` in the environment. It no longer defaults to `egwn_grafana`; point it to the separate, already created interface database (the existing measurement PostgreSQL container is published on host port 5433). `/health/live` reports process liveness; `/health/ready` checks PostgreSQL connectivity and RabbitMQ connectivity when consumption is enabled. Readiness does not assert that every remote Grafana/Keycloak operation will succeed. Monitor pending `Events.Completed=false`, `Customers.Ready=false`, broker dead letters and error logs.

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

The former direct-PostgreSQL live test was removed because it provisioned roles/views through a deprecated path. `MeasurementProvisioningTests` check the Persistence HTTP contract with a fake server without touching running containers. The opt-in `LivePersistenceIntegrationTests.ProvisionRealPersistenceUserAndGrafanaDashboard` calls the **real Persistence HTTPS API and Grafana directly**, without starting the API, RabbitMQ or any container. It reads the supplied CentralAPI event from the test fixture, and uses `EGWN_LIVE_DB_CONNECTION` only to check both meters' readings and hold Grafana's SSO advisory lock; it does not create a database or write measurement data. It requires `EGWN_LIVE_PERSISTENCE=true`, `EGWN_LIVE_DB_CONNECTION` (to `egwndata2`), `EGWN_LIVE_PERSISTENCE_URL`, `EGWN_LIVE_PERSISTENCE_TOKEN`, `EGWN_LIVE_GRAFANA_URL`, `EGWN_LIVE_GRAFANA_USER`, `EGWN_LIVE_GRAFANA_PASSWORD` and `EGWN_LIVE_GRAFANA_DB_HOST`. Supply these outside source control as process environment variables or local API-project User Secrets, with a trusted Persistence TLS certificate, and run only when no other customer is being provisioned:

```powershell
dotnet test .\tests\EGWNInterfaceGrafana.Tests\EGWNInterfaceGrafana.Tests.csproj `
  --filter FullyQualifiedName~LivePersistenceIntegrationTests.ProvisionRealPersistenceUserAndGrafanaDashboard
```

The test replaces the event's real customer UUID, name, number and community UUID with test values, but retains the community's display name, the two meter codes, directions and technical Persistence `community_id` (`sourceId: RC101900`). It verifies nonempty Grafana time series for both meters and the scoped community aggregate. By default it removes the test Grafana organization and calls Persistence `/disable`. **Persistence has no delete endpoint: the disabled test role and schema remain there.** Set `EGWN_LIVE_KEEP_RESOURCES=true` only when you want to inspect a successful test afterwards: the test prints the test customer ID, organization ID and both meter dashboard URLs with measurement time ranges, and leaves the organization, SSO mapping and active Persistence login in place. A failed test still cleans up. Remove the retained organization and disable the customer through the normal provisioning cleanup path after inspection. Cleanup failures are reported rather than silently ignored. Never use an existing customer's UUID for this test.

The provided CentralAPI JSON (customer number `232`, with its original `+02:00` timestamp, both generation and consumption meters and `sourceId: RC101900`) is covered by `PolicyTests.OriginalRabbitMqJsonBuildsMetadataDashboardsWithoutLiveServices` and `MeasurementProvisioningTests.OriginalRabbitMqJsonSendsScopedPersistenceEntitlements`. Run either directly from Rider's test gutter; they use simulated Persistence responses and require no database, credentials, RabbitMQ or runsettings. They do not create a live Grafana organization or assert actual measurements.

The **separate live test** uses both supplied meters with actual readings. In Rider, explicitly select `tests\EGWNInterfaceGrafana.Tests\LivePersistence.runsettings` under **Settings → Build, Execution, Deployment → Unit Testing → Test Runner → Test Settings**; changing this file alone does not attach it to the Rider test runner. Set `EGWN_LIVE_PERSISTENCE=true` to enable that test and `EGWN_LIVE_KEEP_RESOURCES=true` to retain a successful organization; leave the live flag `false` for ordinary test runs. Then run `ProvisionRealPersistenceUserAndGrafanaDashboard` from the test gutter/Unit Tests window. All other `EGWN_LIVE_*` values can be supplied through local .NET User Secrets of `src\EGWNInterfaceGrafana.Api` (or process environment variables); the Persistence token falls back to the existing `Measurement:ServiceToken` User Secret. Never place passwords, database connection strings or bearer tokens in the `.runsettings` file.

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
