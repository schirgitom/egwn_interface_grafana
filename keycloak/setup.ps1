#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $KeycloakUrl = $env:EGWN_KEYCLOAK_SETUP_URL,
    [string] $AdminUsername = $env:KC_BOOTSTRAP_ADMIN_USERNAME,
    [string] $AdminPassword = $env:KC_BOOTSTRAP_ADMIN_PASSWORD,
    [string] $Realm = 'egwn',
    [string] $GrafanaClientId = 'grafana',
    [string] $GrafanaRedirectUri = $env:EGWN_GRAFANA_REDIRECT_URI,
    [string] $GrafanaClientSecret = $env:EGWN_GRAFANA_CLIENT_SECRET,
    [string] $ProvisionerClientId = 'egwn-provisioner',
    [string] $ProvisionerClientSecret = $env:EGWN_PROVISIONER_CLIENT_SECRET,
    [switch] $AllowInsecureHttp
)

$ErrorActionPreference = 'Stop'
foreach ($entry in @{
    KeycloakUrl = $KeycloakUrl; AdminUsername = $AdminUsername; AdminPassword = $AdminPassword
    GrafanaRedirectUri = $GrafanaRedirectUri; GrafanaClientSecret = $GrafanaClientSecret
    ProvisionerClientSecret = $ProvisionerClientSecret
}.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($entry.Value)) { throw "Missing $($entry.Key)." }
}
if ($Realm -eq 'master' -or $Realm -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'Use a dedicated non-master realm with an alphanumeric/hyphen/underscore name.'
}
foreach ($url in @($KeycloakUrl, $GrafanaRedirectUri)) {
    $uri = [uri] $url
    if (!$uri.IsAbsoluteUri -or $uri.UserInfo -or $uri.Fragment -or
        ($uri.Scheme -ne 'https' -and !($AllowInsecureHttp -and $uri.Scheme -eq 'http'))) {
        throw 'URLs require HTTPS (or explicit -AllowInsecureHttp for private development), without user-info or fragments.'
    }
}
if ($GrafanaRedirectUri.Contains('*')) { throw 'Grafana redirect URI must not contain wildcards.' }
$base = $KeycloakUrl.TrimEnd('/')
$realmPath = "/admin/realms/$Realm"
$headers = @{}
$completed = $false

function Invoke-Admin([string] $Method, [string] $Path, $Body = $null) {
    $parameters = @{
        Uri = "$base$Path"; Method = $Method; Headers = $headers; TimeoutSec = 30
        MaximumRedirection = 0
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Body -Depth 40 -Compress))
    }
    try {
        $result = Invoke-RestMethod @parameters
        return $result
    }
    catch {
        # PowerShell HTTP errors include response bodies; never propagate those or credentials.
        $status = if ($_.Exception.Response) { [int] $_.Exception.Response.StatusCode } else { 'unavailable' }
        throw "Keycloak setup request failed: $Method $Path (HTTP $status)."
    }
}

function Get-Flows { @(Invoke-Admin GET "$realmPath/authentication/flows") }
function Get-Executions([string] $Alias) {
    @(Invoke-Admin GET "$realmPath/authentication/flows/$([uri]::EscapeDataString($Alias))/executions")
}
function Set-Requirement([string] $Alias, $Execution, [string] $Requirement) {
    Invoke-Admin PUT "$realmPath/authentication/flows/$([uri]::EscapeDataString($Alias))/executions" @{
        id = $Execution.id; requirement = $Requirement; priority = $Execution.priority
    } | Out-Null
}
function Ensure-Execution([string] $Alias, [string] $Provider, [string] $Requirement) {
    $matches = @(Get-Executions $Alias | Where-Object { $_.providerId -eq $Provider -and $_.level -eq 0 })
    if ($matches.Count -eq 0) {
        Invoke-Admin POST "$realmPath/authentication/flows/$([uri]::EscapeDataString($Alias))/executions/execution" @{
            provider = $Provider
        } | Out-Null
        $matches = @(Get-Executions $Alias | Where-Object { $_.providerId -eq $Provider -and $_.level -eq 0 })
    }
    if ($matches.Count -ne 1) { throw "Expected exactly one $Provider execution in $Alias." }
    Set-Requirement $Alias $matches[0] $Requirement
}
function Ensure-Subflow([string] $Parent, [string] $Alias, [string] $Requirement) {
    $execution = @(Get-Executions $Parent | Where-Object { $_.displayName -eq $Alias -and $_.level -eq 0 -and $_.authenticationFlow })
    if ($execution.Count -eq 0) {
        Invoke-Admin POST "$realmPath/authentication/flows/$([uri]::EscapeDataString($Parent))/executions/flow" @{
            alias = $Alias; type = 'basic-flow'; provider = 'basic-flow'; description = 'Managed by EGWN setup'
        } | Out-Null
        $execution = @(Get-Executions $Parent | Where-Object { $_.displayName -eq $Alias -and $_.level -eq 0 -and $_.authenticationFlow })
    }
    if ($execution.Count -ne 1) { throw "Subflow $Alias is not a direct child of $Parent." }
    Set-Requirement $Parent $execution[0] $Requirement
}
function Assert-Children([string] $Alias, [string[]] $Expected) {
    $actual = @(Get-Executions $Alias | Where-Object level -eq 0 | ForEach-Object {
        if ($_.authenticationFlow) { $_.displayName } else { $_.providerId }
    })
    if (($actual -join '|') -ne ($Expected -join '|')) {
        throw "Unexpected execution layout in $Alias; refusing to bind an unsafe flow."
    }
}
function Set-ChildOrder([string] $Alias, [string[]] $Expected) {
        $children = @(Get-Executions $Alias | Where-Object level -eq 0)
        if ($children.Count -ne $Expected.Count) { throw "Unexpected number of executions in $Alias." }
        for ($index = 0; $index -lt $Expected.Count; $index++) {
            $key = $Expected[$index]
            $execution = @($children | Where-Object {
                ($_.authenticationFlow -and $_.displayName -eq $key) -or (!$_.authenticationFlow -and $_.providerId -eq $key)
            })
            if ($execution.Count -ne 1) { throw "Expected one $key execution in $Alias." }
            Invoke-Admin PUT "$realmPath/authentication/flows/$([uri]::EscapeDataString($Alias))/executions" @{
                id = $execution[0].id; requirement = $execution[0].requirement; priority = ($index + 1) * 10
            } | Out-Null
        }
        Assert-Children $Alias $Expected
}

try {
    try {
        $token = Invoke-RestMethod "$base/realms/master/protocol/openid-connect/token" -Method Post `
            -TimeoutSec 30 -MaximumRedirection 0 -Body @{
                grant_type = 'password'; client_id = 'admin-cli'
                username = $AdminUsername; password = $AdminPassword
            }
    } catch { throw 'Keycloak admin authentication failed (credentials/response suppressed).' }
    $headers.Authorization = "Bearer $($token.access_token)"
    $realms = @(Invoke-Admin GET '/admin/realms')
    if (!($realms | Where-Object realm -eq $Realm)) {
        Invoke-Admin POST '/admin/realms' @{
            realm = $Realm; enabled = $true; registrationAllowed = $false
            sslRequired = $(if ($AllowInsecureHttp) { 'none' } else { 'external' })
            resetPasswordAllowed = $true; rememberMe = $false; loginTheme = 'egwn'
            bruteForceProtected = $true; duplicateEmailsAllowed = $false
            internationalizationEnabled = $true; supportedLocales = @('de'); defaultLocale = 'de'
            registrationEmailAsUsername = $true; loginWithEmailAllowed = $true
        } | Out-Null
    }
    # Keep registration disabled if any subsequent operation fails.
    Invoke-Admin PUT $realmPath @{ registrationAllowed = $false } | Out-Null
    $formProviders = @(Invoke-Admin GET "$realmPath/authentication/form-action-providers")
    $authProviders = @(Invoke-Admin GET "$realmPath/authentication/authenticator-providers")
    if (!($formProviders | Where-Object id -eq 'egwn-registration') -or
        !($authProviders | Where-Object id -eq 'egwn-membership')) {
        throw 'EGWN providers are missing; run the custom Keycloak image.'
    }

    $profile = Invoke-Admin GET "$realmPath/users/profile"
    $profile | Add-Member -NotePropertyName unmanagedAttributePolicy -NotePropertyValue $null -Force
    $profile.attributes = @($profile.attributes | Where-Object {
        $_.name -ne 'egwn_org' -and $_.name -ne 'egwn_registration_code'
    }) + @(@{
        name = 'egwn_org'; displayName = 'EGWN organization'; multivalued = $false
        permissions = @{ view = @('admin'); edit = @('admin') }
    })
    Invoke-Admin PUT "$realmPath/users/profile" $profile | Out-Null

    $registration = 'egwn-registration-flow'
    if (!(Get-Flows | Where-Object alias -eq $registration)) {
        Invoke-Admin POST "$realmPath/authentication/flows/registration/copy" @{ newName = $registration } | Out-Null
    }
    $page = @(Get-Executions $registration | Where-Object { $_.providerId -eq 'registration-page-form' })
    if ($page.Count -ne 1) { throw 'Expected one Registration Form in copied registration flow.' }
    $formFlow = Invoke-Admin GET "$realmPath/authentication/flows/$($page[0].flowId)"
    $formAlias = $formFlow.alias
    Ensure-Execution $formAlias 'registration-user-creation' 'REQUIRED'
    Ensure-Execution $formAlias 'registration-password-action' 'REQUIRED'
    Ensure-Execution $formAlias 'egwn-registration' 'REQUIRED'
    Set-ChildOrder $formAlias @('registration-user-creation', 'registration-password-action',
        'registration-recaptcha-action', 'registration-terms-and-conditions', 'egwn-registration')
    Set-Requirement $registration $page[0] 'REQUIRED'

    $browser = 'egwn-browser'
    if (!(Get-Flows | Where-Object alias -eq $browser)) {
        Invoke-Admin POST "$realmPath/authentication/flows" @{
            alias = $browser; description = 'EGWN browser with live Grafana membership'
            providerId = 'basic-flow'; topLevel = $true; builtIn = $false
        } | Out-Null
    }
    Ensure-Subflow $browser 'egwn-authenticate' 'REQUIRED'
    Ensure-Execution 'egwn-authenticate' 'auth-cookie' 'ALTERNATIVE'
    Ensure-Subflow 'egwn-authenticate' 'egwn-forms' 'ALTERNATIVE'
    Ensure-Execution 'egwn-forms' 'auth-username-password-form' 'REQUIRED'
    Ensure-Subflow 'egwn-forms' 'egwn-conditional-otp' 'CONDITIONAL'
    Ensure-Execution 'egwn-conditional-otp' 'conditional-user-configured' 'REQUIRED'
    Ensure-Execution 'egwn-conditional-otp' 'auth-otp-form' 'REQUIRED'
    Ensure-Execution $browser 'egwn-membership' 'REQUIRED'
    Set-ChildOrder $browser @('egwn-authenticate', 'egwn-membership')
    Set-ChildOrder 'egwn-authenticate' @('auth-cookie', 'egwn-forms')
    Set-ChildOrder 'egwn-forms' @('auth-username-password-form', 'egwn-conditional-otp')
    Set-ChildOrder 'egwn-conditional-otp' @('conditional-user-configured', 'auth-otp-form')

    $clients = @(Invoke-Admin GET "$realmPath/clients?clientId=$([uri]::EscapeDataString($GrafanaClientId))")
    if ($clients.Count -gt 1) { throw 'Ambiguous Grafana client ID.' }
    $client = @{
        clientId = $GrafanaClientId; name = 'Grafana'; protocol = 'openid-connect'; enabled = $true
        publicClient = $false; clientAuthenticatorType = 'client-secret'; secret = $GrafanaClientSecret
        standardFlowEnabled = $true; implicitFlowEnabled = $false; directAccessGrantsEnabled = $false
        serviceAccountsEnabled = $false; fullScopeAllowed = $false
        redirectUris = @($GrafanaRedirectUri)
        webOrigins = @(([uri] $GrafanaRedirectUri).GetLeftPart([System.UriPartial]::Authority))
        attributes = @{
            'pkce.code.challenge.method' = 'S256'
            'oauth2.device.authorization.grant.enabled' = 'false'
            'oidc.ciba.grant.enabled' = 'false'
            'standard.token.exchange.enabled' = 'false'
        }
        authenticationFlowBindingOverrides = @{}
    }
    if ($clients.Count -eq 0) {
        Invoke-Admin POST "$realmPath/clients" $client | Out-Null
        $clients = @(Invoke-Admin GET "$realmPath/clients?clientId=$([uri]::EscapeDataString($GrafanaClientId))")
    } else {
        Invoke-Admin PUT "$realmPath/clients/$($clients[0].id)" $client | Out-Null
    }
    $clientPath = "$realmPath/clients/$($clients[0].id)"
    $mappers = @(Invoke-Admin GET "$clientPath/protocol-mappers/models")
    foreach ($mapper in @($mappers | Where-Object { $_.name -eq 'egwn_org' -or $_.config.'claim.name' -eq 'egwn_org' })) {
        Invoke-Admin DELETE "$clientPath/protocol-mappers/models/$($mapper.id)" | Out-Null
    }
    Invoke-Admin POST "$clientPath/protocol-mappers/models" @{
        name = 'egwn_org'; protocol = 'openid-connect'; protocolMapper = 'oidc-usermodel-attribute-mapper'
        consentRequired = $false
        config = @{
            'user.attribute' = 'egwn_org'; 'claim.name' = 'egwn_org'; 'jsonType.label' = 'String'
            'multivalued' = 'false'; 'id.token.claim' = 'true'; 'access.token.claim' = 'true'
            'userinfo.token.claim' = 'true'
        }
    } | Out-Null
    # No offline refresh scope on this client, including optional client scopes.
    foreach ($kind in @('default-client-scopes', 'optional-client-scopes')) {
        foreach ($scope in @(Invoke-Admin GET "$clientPath/$kind" | Where-Object name -eq 'offline_access')) {
            Invoke-Admin DELETE "$clientPath/$kind/$($scope.id)" | Out-Null
        }
    }
    $provisioners = @(Invoke-Admin GET "$realmPath/clients?clientId=$([uri]::EscapeDataString($ProvisionerClientId))")
    if ($provisioners.Count -gt 1 -or $ProvisionerClientId -eq $GrafanaClientId) {
        throw 'Provisioner requires a unique client ID separate from Grafana.'
    }
    $provisioner = @{
        clientId = $ProvisionerClientId; name = 'EGWN backend account lifecycle'; protocol = 'openid-connect'
        enabled = $true; publicClient = $false; clientAuthenticatorType = 'client-secret'
        secret = $ProvisionerClientSecret; serviceAccountsEnabled = $true
        standardFlowEnabled = $false; implicitFlowEnabled = $false; directAccessGrantsEnabled = $false
        fullScopeAllowed = $false; redirectUris = @(); webOrigins = @()
        attributes = @{
            'oauth2.device.authorization.grant.enabled' = 'false'
            'oidc.ciba.grant.enabled' = 'false'; 'standard.token.exchange.enabled' = 'false'
        }
    }
    if ($provisioners.Count -eq 0) {
        Invoke-Admin POST "$realmPath/clients" $provisioner | Out-Null
        $provisioners = @(Invoke-Admin GET "$realmPath/clients?clientId=$([uri]::EscapeDataString($ProvisionerClientId))")
    } else {
        Invoke-Admin PUT "$realmPath/clients/$($provisioners[0].id)" $provisioner | Out-Null
    }
    $provisionerPath = "$realmPath/clients/$($provisioners[0].id)"
    $serviceUser = Invoke-Admin GET "$provisionerPath/service-account-user"
    $management = @(Invoke-Admin GET "$realmPath/clients?clientId=realm-management")[0]
    $manageUsers = Invoke-Admin GET "$realmPath/clients/$($management.id)/roles/manage-users"
    Invoke-Admin POST "$realmPath/users/$($serviceUser.id)/role-mappings/clients/$($management.id)" @($manageUsers) | Out-Null
    Invoke-Admin POST "$provisionerPath/scope-mappings/clients/$($management.id)" @($manageUsers) | Out-Null
    $adminScope = @(Invoke-Admin GET "$realmPath/client-scopes" | Where-Object name -eq 'kc-admin-api')
    if ($adminScope.Count -ne 1) { throw 'Keycloak 26.8 admin API client scope is missing.' }
    Invoke-Admin PUT "$provisionerPath/default-client-scopes/$($adminScope[0].id)" | Out-Null
    Invoke-Admin PUT $realmPath @{
        browserFlow = $browser; registrationFlow = $registration; loginTheme = 'egwn'
        registrationAllowed = $true; accessTokenLifespan = 300
        internationalizationEnabled = $true; supportedLocales = @('de'); defaultLocale = 'de'
        registrationEmailAsUsername = $true; loginWithEmailAllowed = $true
    } | Out-Null
    $completed = $true
    Write-Output "Configured realm '$Realm'; browser '$browser'; registration '$registration'; theme 'egwn'; client '$GrafanaClientId'."
    Write-Output 'Registration selectors: #kc-register-form, #email, #firstName, #lastName, #password, #password-confirm, #egwn_registration_code; submit input[type=submit].'
} finally {
    if (!$completed -and $headers.Authorization) {
        try { Invoke-Admin PUT $realmPath @{ registrationAllowed = $false } | Out-Null } catch {}
    }
    $headers.Clear()
    $AdminPassword = $null
    $GrafanaClientSecret = $null
    $ProvisionerClientSecret = $null
    $token = $null
}
