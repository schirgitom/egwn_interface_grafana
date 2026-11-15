#requires -Version 7.0
[CmdletBinding()]
param([string] $Image = 'egwn-keycloak:26.8.2')
$ErrorActionPreference = 'Stop'
$name = 'egwn-keycloak-setup-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$password = [guid]::NewGuid().ToString('N')
$secret = [guid]::NewGuid().ToString('N')
$themeDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('egwn-keycloak-theme-test-' + [guid]::NewGuid().ToString('N'))
$themeMarker = 'egwn-theme-volume-' + [guid]::NewGuid().ToString('N')
try {
    Copy-Item -LiteralPath "$PSScriptRoot\theme\egwn" -Destination $themeDirectory -Recurse
    Add-Content -LiteralPath (Join-Path $themeDirectory 'login\resources\css\egwn.css') -Value "/* $themeMarker */"
    docker run -d --rm --name $name -p 127.0.0.1::8080 `
        --mount "type=bind,source=$themeDirectory,target=/opt/keycloak/themes/egwn,readonly" `
        -e KC_BOOTSTRAP_ADMIN_USERNAME=setup-admin -e "KC_BOOTSTRAP_ADMIN_PASSWORD=$password" `
        $Image start-dev --db=dev-file --http-enabled=true | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start disposable Keycloak.' }
    $base = 'http://' + (docker port $name 8080/tcp)
    $ready = $false
    for ($i = 0; $i -lt 80; $i++) {
        try {
            $null = Invoke-RestMethod "$base/realms/master/.well-known/openid-configuration" -TimeoutSec 2
            $ready = $true
            break
        } catch { Start-Sleep -Seconds 2 }
    }
    if (!$ready) { throw 'Disposable Keycloak did not become ready.' }
    for ($run = 0; $run -lt 2; $run++) {
        & "$PSScriptRoot\setup.ps1" -KeycloakUrl $base -AdminUsername setup-admin -AdminPassword $password `
            -GrafanaRedirectUri http://grafana:3000/login/generic_oauth -GrafanaClientSecret $secret `
            -ProvisionerClientSecret $secret -AllowInsecureHttp
    }
    $token = Invoke-RestMethod "$base/realms/master/protocol/openid-connect/token" -Method Post -Body @{
        grant_type = 'password'; client_id = 'admin-cli'; username = 'setup-admin'; password = $password
    }
    $headers = @{ Authorization = "Bearer $($token.access_token)" }
    $realm = Invoke-RestMethod "$base/admin/realms/egwn" -Headers $headers
    if ($realm.browserFlow -ne 'egwn-browser' -or $realm.registrationFlow -ne 'egwn-registration-flow' -or
        !$realm.registrationAllowed -or $realm.loginTheme -ne 'egwn' -or
        !$realm.internationalizationEnabled -or $realm.defaultLocale -ne 'de' -or
        @($realm.supportedLocales).Count -ne 1 -or $realm.supportedLocales -notcontains 'de' -or
        !$realm.registrationEmailAsUsername -or !$realm.loginWithEmailAllowed) {
        throw 'Realm bindings, German locale or email-as-username settings were not persisted.'
    }
    $profile = Invoke-RestMethod "$base/admin/realms/egwn/users/profile" -Headers $headers
    $org = @($profile.attributes | Where-Object name -eq 'egwn_org')
    if ($profile.unmanagedAttributePolicy -or $org.Count -ne 1 -or $org[0].permissions.edit -contains 'user') {
        throw 'Profile protection was not persisted.'
    }
    $client = @(Invoke-RestMethod "$base/admin/realms/egwn/clients?clientId=grafana" -Headers $headers)[0]
    if ($client.directAccessGrantsEnabled -or $client.publicClient -or
        $client.attributes.'pkce.code.challenge.method' -ne 'S256') { throw 'Unsafe Grafana client settings.' }
    $mappers = Invoke-RestMethod "$base/admin/realms/egwn/clients/$($client.id)/protocol-mappers/models" -Headers $headers
    if (@($mappers | Where-Object name -eq 'egwn_org').Count -ne 1) { throw 'Claim mapper duplicated or missing.' }
    $serviceToken = Invoke-RestMethod "$base/realms/egwn/protocol/openid-connect/token" -Method Post -Body @{
        grant_type = 'client_credentials'; client_id = 'egwn-provisioner'; client_secret = $secret
    }
    try {
        $null = Invoke-RestMethod "$base/admin/realms/egwn/users?max=1" -Headers @{
            Authorization = "Bearer $($serviceToken.access_token)"
        }
    } catch {
        $encoded = $serviceToken.access_token.Split('.')[1].Replace('-', '+').Replace('_', '/')
        $encoded = $encoded.PadRight($encoded.Length + (4 - $encoded.Length % 4) % 4, '=')
        $claims = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)) | ConvertFrom-Json
        Write-Output ($claims | Select-Object aud, azp, resource_access | ConvertTo-Json -Depth 8)
        throw 'Provisioner management authorization failed.'
    }
    $url = "$base/realms/egwn/protocol/openid-connect/registrations?client_id=grafana&redirect_uri=http%3A%2F%2Fgrafana%3A3000%2Flogin%2Fgeneric_oauth&response_type=code&scope=openid&code_challenge=$('x' * 43)&code_challenge_method=S256"
    $page = Invoke-WebRequest $url -TimeoutSec 15
    if ($page.StatusCode -ne 200 -or !$page.Content.Contains('name="egwn_registration_code"') -or
        $page.Content.Contains('id="username"') -or !$page.Content.Contains('Registrierungscode') -or
        !$page.Content.Contains('class="col-xs-12 col-sm-12 col-md-12 col-lg-12"')) {
        throw 'Configured German email-only registration page or field layout failed.'
    }
    $cssLink = [regex]::Match($page.Content, 'href="([^"]*egwn\.css[^"]*)"')
    if (!$cssLink.Success) { throw 'Registration page does not load the EGWN stylesheet.' }
    $cssPath = [System.Net.WebUtility]::HtmlDecode($cssLink.Groups[1].Value)
    $css = Invoke-WebRequest ([uri]::new([uri] $base, $cssPath)) -TimeoutSec 15
    if (!$css.Content.Contains($themeMarker)) { throw 'Keycloak did not serve the mounted theme stylesheet.' }
    Write-Output 'PASS: fresh setup, repeat setup, persisted profile/flows/client/mapper and configured registration page.'
} finally {
    docker rm -f $name 2>$null | Out-Null
    if (Test-Path -LiteralPath $themeDirectory) { Remove-Item -LiteralPath $themeDirectory -Recurse -Force }
    $password = $null
    $secret = $null
    $token = $null
}
