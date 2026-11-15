# EGWN Keycloak registration and Grafana membership

Pinned to **Keycloak 26.8.2**, Java 21. This directory is a standalone Maven project;
neither Java nor Maven is needed on the host. The provider JAR contains only
the Java extension; the `egwn` login theme is installed separately under
`/opt/keycloak/themes/egwn` in the image. No credentials or realm/client
secrets are included.

The `egwn` realm is configured with German as its only supported login language
(`de`) and uses the email address as the Keycloak username. Existing accounts
retain their original usernames; the new setting applies to newly registered
accounts. The registration page therefore asks for email, first name, last
name, password, password confirmation and registration code, but not a
separate username. The custom password/code fields use the same Keycloak
label/input grid as the built-in profile fields.

### Change the login background

The default image comes from Keycloak's parent theme. The repository's
`compose.yml` mounts `keycloak\theme\egwn` read-only over the image's theme.
For a different deployment host, copy the complete `keycloak\theme\egwn`
directory there and set `KEYCLOAK_THEME_DIR` to its path in the Compose
environment (or add the equivalent bind mount to the Compose file actually
used to run Keycloak):

```yaml
volumes:
  - ./keycloak/theme/egwn:/opt/keycloak/themes/egwn:ro
```

The host directory must exist and contain `login/theme.properties`,
`login/register.ftl`, `login/messages` and `login/resources/css/egwn.css`;
an empty bind mount hides the image's theme and breaks the login page.
After the one-time rebuild, push and deployment of the separated image,
add your own image to the mounted directory at
`login\resources\img\background.webp` and append this rule to
`login\resources\css\egwn.css` in that directory:

```css
.login-pf body {
    background: url("../img/background.webp") center center / cover no-repeat fixed;
}
```

The `egwn.css` file is loaded after Keycloak's `login.css`. Use an image you
own or have permission to use; choose a size optimized for the full-screen
layout. The theme styles give the login and registration pages a rounded,
translucent card and softly tinted background; the background image in the
mounted CSS remains independently replaceable. Changing mounted files requires
no further image build or push.
Keycloak caches theme resources in production: restart Keycloak after a
change, then hard-refresh the browser if the previous image is cached.
Keycloak can retain a gzipped copy of the old CSS across restarts under
`/opt/keycloak/data/tmp/kc-gzip-cache/<version>/login/egwn/css/egwn.css.gz`.
If the page still uses the old styles, remove that specific cache file in the
container and reload the page. This rule affects both login and registration
pages. Keep the mounted theme directory synchronized with future theme
template updates.

## Build and tests

From the repository directory (PowerShell):

```powershell
docker build --progress=plain -t egwn-keycloak:26.8.2 .\keycloak
```

The Maven stage runs `mvn --batch-mode --no-transfer-progress verify` (including
HTTP contract and flow unit tests). The Keycloak stage installs the JAR before
`kc.sh build --db=postgres --health-enabled=true`. The final image starts with
`start --optimized`. Build context must be `keycloak`, not the repository root.

To run just Maven with persistent reports in `keycloak\target`:

```powershell
$source = (Resolve-Path .\keycloak).Path
docker run --rm --mount "type=bind,source=$source,target=/workspace" -w /workspace maven:3.9.11-eclipse-temurin-21 mvn --batch-mode --no-transfer-progress verify
```

Tests use a local stub HTTP server and mock Keycloak contexts, not a real EGWN
backend or Grafana. Successful image construction is not an end-to-end test.
Validation performed: image build with **17 passing unit tests**, plus a
disposable live Keycloak boot verifying both SPI factories are discoverable and
the real FreeMarker registration page returns HTTP 200 with code/password fields.
The standalone smoke check used `start-dev --db=dev-file`. The repository's
browser integration test additionally exercises this provider against PostgreSQL,
the real registration backend and Grafana 13.3.0, including reusable codes,
Viewer membership, cross-customer denial, code rotation and customer deletion.
The authentication SPIs are internal Keycloak APIs: upgrade the pinned version
only with compilation, theme and real browser-flow regression testing.

## Runtime configuration

| Variable | Required/default | Purpose |
| --- | --- | --- |
| `EGWN_BACKEND_URL` | Required | Backend origin, optionally with a base path, e.g. `https://backend.example.test`; no query, fragment or user-info. Do not append `/internal/registration`. |
| `EGWN_INTERNAL_TOKEN` | Required | Backend-only bearer secret, supplied at runtime through secret management; never put it in realm exports, image layers or browser config. |
| `EGWN_ALLOW_INSECURE_HTTP` | Default false | Only the exact value `true` permits HTTP. Use only on an isolated private development network. |
| `EGWN_GRAFANA_CLIENT_ID` | `grafana` | Exact client ID protected by the membership execution. |

Use the usual Keycloak production variables separately: `KC_DB_URL`,
`KC_DB_USERNAME`, `KC_DB_PASSWORD`, `KC_HOSTNAME`, HTTPS certificate/key or
trusted TLS-terminating reverse-proxy settings, and initial admin bootstrap
secrets. The image is built for PostgreSQL and exposes health on Keycloak's
management listener (normally port 9000). Supply your database and certificates;
there is intentionally no embedded production database or default password.
Backend TLS uses the JVM trust store; install your private CA properly, never
disable certificate verification. HTTP connect timeout is 2 seconds, total
request timeout 5 seconds, maximum response body 16 KiB, redirects disabled.

## Automated realm setup (recommended)

Run `setup.ps1` with PowerShell 7 or newer. It creates/updates the realm profile,
theme binding, full registration and browser flows, confidential Grafana client
with PKCE, string claim mapper, and a separate backend service-account client.
It preserves existing standard profile attributes and users, fixes execution
priorities explicitly, and is repeatable without duplicate executions/mappers.
Registration stays disabled if configuration fails. Use a maintenance window
when updating an existing realm; unexpected/custom execution layouts are rejected
rather than silently overwritten. Configure customized MFA/brokering separately
after setup, or adapt the expected layout before rerunning.

Supply secrets from your environment/secret manager; none are stored in the
script or committed config:

```powershell
# Required environment variables (set their actual values using secret management):
# EGWN_KEYCLOAK_SETUP_URL, KC_BOOTSTRAP_ADMIN_USERNAME, KC_BOOTSTRAP_ADMIN_PASSWORD
# EGWN_GRAFANA_REDIRECT_URI, EGWN_GRAFANA_CLIENT_SECRET, EGWN_PROVISIONER_CLIENT_SECRET
.\keycloak\setup.ps1
```

Default realm/client names are `egwn`, `grafana`, `egwn-provisioner`; override with
`-Realm`, `-GrafanaClientId`, `-ProvisionerClientId`. URLs must use HTTPS and an
exact redirect URI without a wildcard. For isolated HTTP integration testing
only, pass `-AllowInsecureHttp` (also creates new realms with `sslRequired=none`).
This switch concerns setup URLs; the Keycloak container still separately needs
`EGWN_BACKEND_URL=http://api:8080` and `EGWN_ALLOW_INSECURE_HTTP=true`.

When Keycloak has no published host port, run PowerShell on its Docker network.
The existing .NET SDK image includes PowerShell; this command does not compile
.NET or require local Java/Maven:

```powershell
$source = (Resolve-Path .\keycloak).Path
docker run --rm --network egwn-grafana-tests_default `
  --mount "type=bind,source=$source,target=/workspace,readonly" `
  -e EGWN_KEYCLOAK_SETUP_URL=http://keycloak:8080 `
  -e KC_BOOTSTRAP_ADMIN_USERNAME -e KC_BOOTSTRAP_ADMIN_PASSWORD `
  -e EGWN_GRAFANA_REDIRECT_URI=http://grafana:3000/login/generic_oauth `
  -e EGWN_GRAFANA_CLIENT_SECRET -e EGWN_PROVISIONER_CLIENT_SECRET `
  --entrypoint pwsh mcr.microsoft.com/dotnet/sdk:10.0 `
  -NoProfile -File /workspace/setup.ps1 -AllowInsecureHttp
```

The provisioner has service accounts enabled, browser/direct grants disabled,
`fullScopeAllowed=false`, and only the explicitly assigned realm-management
`manage-users` role/scope mapping. Keycloak **26.8.2 additionally requires the
`kc-admin-api` client scope/audience**, which the script attaches as a default
scope to this client. The backend obtains tokens using client credentials at
`/realms/egwn/protocol/openid-connect/token` and can use:

* `GET /admin/realms/egwn/users?username=...&exact=true`
* `POST /admin/realms/egwn/users/{immutableId}/logout`
* `DELETE /admin/realms/egwn/users/{immutableId}`

Do not expose its secret or token to Grafana or browsers. `manage-users` is
privileged backend lifecycle access, not an end-user/customer role.

Reproduce the live bootstrap regression test:

```powershell
.\keycloak\test-setup.ps1
```

It boots an isolated disposable Keycloak with a temporary mounted theme, applies
setup twice, verifies persisted profile/flow/client/mapper settings, obtains a
provisioner token and checks its Admin API access, then verifies registration
rendering and that Keycloak serves CSS from the mount before removing it.
No credentials are fixed or written to disk.

Validation also performed against the actual
`egwn-grafana-tests-keycloak-1` instance: bootstrap succeeded; registration
returned HTTP 200; provisioner client credentials successfully created, listed,
logged out and deleted a disposable check account. No check account was retained.
Backend assignment and Grafana browser authorization are covered separately by
the repository's `tests\browser` integration test.

### Browser test URLs and selectors

* Registration:
  `/realms/egwn/protocol/openid-connect/registrations?client_id=grafana&redirect_uri=<URL-encoded-exact-redirect>&response_type=code&scope=openid&code_challenge=<S256-challenge>&code_challenge_method=S256`
* Login: the same query on `/realms/egwn/protocol/openid-connect/auth`.
  Prefer starting from Grafana `/login/generic_oauth` so Grafana owns state,
  PKCE and the callback. A manually generated challenge needs its matching verifier
  for authorization-code exchange.
* Form: `#kc-register-form`; ordinary fields `#email`,
  `#firstName`, `#lastName`, `#password`, `#password-confirm`.
* Code: **`[name="egwn_registration_code"]` / `#egwn_registration_code`**,
  not `registrationCode`. Code error: **`#egwn-code-error`**.
* Submit: `#kc-register-form input[type="submit"]`.
* Assign-time failures are a generic **error page**, not a field-level code
  error; user creation is explicitly rolled back.
* Theme: `egwn`; registration flow: `egwn-registration-flow`; browser flow:
  `egwn-browser`. Its required first subflow `egwn-authenticate` contains
  alternative Cookie then Forms; required `egwn-membership` is a sibling **after**
  that entire subflow, so existing SSO cookies still hit live membership.

## Manual realm setup reference

Use **one common realm**, e.g. `egwn`, not a realm per customer. Complete the
following configuration is what the automated setup applies. It is also a
reference for customized Keycloak 26.8.2 deployments. There is deliberately no
credential-bearing realm import.

### 1. User profile and theme

1. Realm settings → User profile: set **Unmanaged attributes = Disabled**.
   In profile JSON this is an absent/null `unmanagedAttributePolicy`, **not**
   `"DISABLED"`. Keep the standard username/email/name attributes. With
   `registrationEmailAsUsername=true`, the username attribute is not shown
   as a separate registration input.
2. Add managed attribute `egwn_org`, single-valued, not required for users,
   with **View = admin only; Edit = admin only**. Never allow `user` edit
   permissions, including in Account Console or registration. No user-supplied
   organization or organization selector is allowed. Relevant JSON fragment:

   ```json
   {
     "name": "egwn_org",
     "displayName": "EGWN organization",
     "multivalued": false,
     "permissions": { "view": ["admin"], "edit": ["admin"] }
   }
   ```

3. **Do not** add `egwn_registration_code` as a user-profile attribute. It is
   a dedicated transient form parameter, not profile data. The provider fails
   closed if unmanaged attributes are enabled, that code attribute is managed,
   or `egwn_org` is missing/user-editable.
4. Realm settings → Themes → Login theme = **egwn**.

The theme overrides only `register.ftl`, using the official 26.8.2
`user-profile-commons.ftl` and `register-commons.ftl` macro contracts inherited
from `keycloak`. All normal profile fields and their validation remain; password,
confirmation, terms and a visible CAPTCHA slot are rendered. If CAPTCHA is
enabled, use **visible** reCAPTCHA, not the invisible mode. The code is a
password-type required input and is never repopulated on errors; the user must
re-enter it after any validation failure.

### 2. Registration flow

Authentication → Flows: duplicate the built-in **registration** flow to
`egwn-registration-flow`. In its **Registration Form** subflow:

1. **Registration User Profile Creation** — Required, **first**.
2. Keep the existing required password validation and any approved terms/CAPTCHA.
3. Add execution **EGWN registration assignment** (`egwn-registration`) —
   **Required**, after user-profile creation (preferably last).

Bind this as the realm's **Registration flow**. Enable user registration only
after the profile, theme and flow are configured. Do not use any client
registration-flow override that omits this execution.

Keycloak validates *all* form actions before calling their `success()` methods
in execution order. This action resolves the code during validation; user
creation therefore has not happened yet. Only the opaque ticket is placed in
an authentication-session **auth note**, never a user/client-session note.
On successful validation, built-in user creation runs first; assignment uses
`context.getUser().getId()`, never a username/email or submitted user ID.
Assignment failure explicitly marks the local Keycloak transaction rollback-only
and aborts login: it is not treated as successful registration.

### 3. Browser flow (do not skip the SSO-cookie path)

Create `egwn-browser` as a top-level **basic flow**, with exactly this structure:

```text
egwn-browser
  Authenticate user                       REQUIRED (basic subflow)
    Cookie                                ALTERNATIVE
    Forms                                 ALTERNATIVE (basic subflow)
      Username Password Form              REQUIRED
      Conditional OTP                     CONDITIONAL (basic subflow)
        Condition - user configured        REQUIRED
        OTP Form                          REQUIRED
  EGWN active Grafana membership           REQUIRED
```

The nested `Authenticate user` is the equivalent of the built-in browser
authentication selection; preserve/add your organization's existing MFA,
WebAuthn, identity-provider and other authentication executions **inside that
subflow**. The structure above preserves configured-user OTP but does not add
identity providers or passwordless login automatically.

**Important:** do not merely append a Required membership step alongside the
default root Alternative Cookie/Forms steps: Keycloak ignores alternative
executions when required siblings exist. Wrap authentication in the Required
subflow as shown. Do not place membership inside Forms, where cookie SSO could
bypass it. Bind `egwn-browser` as the realm's Browser flow (or an explicit Grafana
browser-flow override referencing this complete flow).

`EGWN active Grafana membership` (`egwn-membership`) checks the backend on
**every Grafana browser authentication**, including an existing SSO cookie.
It refuses an absent user, inactive/missing membership, unsafe profile settings,
malformed responses and backend outages. It refreshes `egwn_org` using
`setSingleAttribute`, replacing all old values. Other clients are explicitly
bypassed; do not attach the Grafana mapper to other clients.

### 4. Grafana OIDC client, claims and authorization

Create confidential OpenID Connect client `grafana` (or match
`EGWN_GRAFANA_CLIENT_ID`). Enable Standard flow (authorization code), client
authentication and PKCE S256. Set exact approved HTTPS redirect URI(s), e.g.
`https://grafana.example.test/login/generic_oauth`, and exact web origins.
Disable **Direct access grants**, **Implicit flow**, **Service accounts**,
device authorization and CIBA. Do not permit token exchange/impersonation to
mint Grafana tokens outside this checked browser path.

On the Grafana client's dedicated client scope, add **User Attribute** mapper:

| Setting | Value |
| --- | --- |
| Name / User Attribute / Token Claim Name | `egwn_org` |
| Claim JSON type | `String` |
| Multivalued | Off |
| Add to ID token / access token / UserInfo | On |

The claim must contain exactly one nonempty backend organization identifier.
Do not use a form field, group, email suffix or client-provided claim as a mapper
source. Do not give customers realm/client administration privileges.

Grafana **13.3.0** must **deny login if the organization claim is missing/unrecognized**,
not silently fall back to its main organization. Configure strict organization
mapping against an explicit allowlist of backend organization IDs to existing
Grafana organization IDs, and map **only Viewer**, never Editor/Admin or
Grafana server admin. Use strict role mapping, disable assigning Grafana admin,
and disable local/password/basic-auth login as applicable. A typical Generic
OAuth configuration includes `role_attribute_path = 'None'`,
`role_attribute_strict = true`, `allow_assign_grafana_admin = false`,
`skip_org_role_sync = false`, `org_attribute_path = [egwn_org]`, and an **explicit**
`org_mapping` entry `egwn-<customerN>:<orgId>:Viewer` for every permitted customer.
The deployment synchronizes this allowlist through Grafana OSS SSO settings
`PATCH /api/v1/sso-settings/generic_oauth` (runtime reload, no restart). The global
role is deliberately `None`; Viewer comes only from a recognized organization
mapping. Do not configure a wildcard/fallback mapping: Grafana `*` means all
organizations, **not** dynamic identity-based mapping. Verify missing/unknown
claims deny access with strict mapping.

Browser executions do **not** run on refresh grants, and existing Grafana
sessions are not continuously rechecked. For the strict “live membership at
every login” policy, disable Grafana refresh-token use, omit `offline_access`,
use short Keycloak/Grafana session lifetimes, and revoke both sessions upon
customer deactivation/deletion. Existing access tokens remain usable until
expiry unless the resource server performs live membership checking. Never
claim this browser SPI is token-refresh or per-request authorization.

## Internal backend contract and invariants

All calls are JSON POST with `Authorization: Bearer <EGWN_INTERNAL_TOKEN>`.
Only HTTP **200** is accepted; **400**, outages and every other status deny
the operation. Error pages are deliberately generic; the code, ticket, bearer,
request/response bodies and exception causes are never logged by this provider.
Also disable HTTP body tracing in proxies/APM and request-body logging globally.

| Path | Request | HTTP 200 response |
| --- | --- | --- |
| `/internal/registration/resolve` | `{"code":"..."}` | `{"ticket":"...","organization":"customer-a"}` |
| `/internal/registration/assign` | `{"ticket":"...","userId":"immutable-keycloak-id"}` | `{"customerId":"GUID","organization":"customer-a"}` |
| `/internal/registration/membership` | `{"userId":"immutable-keycloak-id"}` | `{"customerId":"GUID","organization":"customer-a"}` |

Invalid code/ticket and missing/inactive membership return a generic **400**.
The backend, not this provider, must:

* Authenticate the internal caller on all three endpoints.
* Issue cryptographically authenticated, short-lived tickets (maximum **5 min**).
  Bind each ticket to the resolved customer/current code generation, without
  embedding or persisting the raw reusable code in the ticket.
* Revalidate ticket signature, expiry, code generation, active code and active
  customer **at assignment time**. Code rotation/deletion must invalidate
  previously resolved tickets. Never trust the organization returned at resolve
  as authorization; assignment's current organization is authoritative.
* Enforce one customer per immutable Keycloak user ID. Repeated assignment of
  the same user/customer must be idempotent; reject reassignment to a different
  customer. A code is reusable for multiple distinct users until rotation or
  deletion; do not consume it after registration.
* Restrict each deployed internal caller/mapping namespace to this common realm
  because the contract transmits user ID, not realm ID.

The ticket is removed before assignment and when redisplaying the form, and
expires server-side even if abandoned. Raw codes exist only in the incoming
request and outgoing resolve call, not user attributes/authentication notes.
An assignment may commit remotely before the local Keycloak transaction fails.
That orphan backend mapping is safe because the immutable Keycloak user ID is
never reused and no access exists without its account. Repeated calls must
remain idempotent; account deletion/orphan cleanup is a backend administrative
concern. Do not add a compensating “unassign” that could race successful retries.

## Deployment acceptance checks

Before production, test with the real backend and Grafana:

1. Valid code creates one account and one customer mapping; a second user can use
   the same code. Neither user attributes nor exported sessions contain the code.
2. Bad/rotated/deleted codes fail generically. Rotate between resolve/assign;
   assignment must fail and **no local user** may survive the rollback.
3. Backend outage during resolve creates no user; outage during assign rolls
   back creation. Check remote orphan mapping behavior and idempotence.
4. Login to Grafana with/without an SSO cookie refreshes the single claim.
   Inactive membership and backend outage deny both paths.
5. Account Console/forged profile requests cannot change `egwn_org`; duplicate
   code parameters fail. Missing/unmapped org claims are rejected by Grafana.
6. Viewer only, correct customer organization, no default-org fallback, no
   direct grants, no local Grafana login or alternate client bypass.
