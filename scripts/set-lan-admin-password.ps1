[CmdletBinding()]
param(
    [PSCredential]$Credential,
    [string]$EnvFile = ""
)

$ErrorActionPreference = "Stop"
function ConvertTo-Base64String([byte[]]$value) { [Convert]::ToBase64String($value) }
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectName = "whisperx-atom"
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repo ".env.lan" }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "ENV_REQUIRED: $EnvFile is missing." }
if (-not $Credential) { throw "CREDENTIAL_REQUIRED: pass a PSCredential for the admin account." }
if ($Credential.UserName -ne "admin") { throw "ADMIN_USERNAME_REQUIRED: only the admin account may be updated." }

$password = $Credential.GetNetworkCredential().Password
if ([string]::IsNullOrWhiteSpace($password)) { throw "PASSWORD_REQUIRED" }

$salt = New-Object byte[] 16
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$derived = $null
$pbkdf2 = $null
try {
    # Windows PowerShell 5.1/.NET Framework does not expose the newer static
    # RandomNumberGenerator.GetBytes or Rfc2898DeriveBytes.Pbkdf2 helpers.
    $random.GetBytes($salt)
    $pbkdf2 = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $password,
        $salt,
        120000,
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    $derived = $pbkdf2.GetBytes(32)
}
finally {
    if ($null -ne $pbkdf2) { $pbkdf2.Dispose() }
    $random.Dispose()
}
$hash = "pbkdf2-sha256`$120000`$" + (ConvertTo-Base64String $salt) + "`$" + (ConvertTo-Base64String $derived)

$sql = @'
BEGIN;
DO $$
DECLARE admin_count integer;
DECLARE user_count integer;
BEGIN
    SELECT COUNT(*) INTO admin_count FROM users WHERE username = 'admin';
    SELECT COUNT(*) INTO user_count FROM users;
    IF admin_count <> 1 OR user_count <> 1 THEN
        RAISE EXCEPTION 'ADMIN_ACCOUNT_COUNT_INVALID';
    END IF;
END
$$;
UPDATE users
SET password_hash = '__HASH__', role = 'Administrator', is_active = true,
    must_change_password = false, password_changed_at = now()
WHERE username = 'admin';
UPDATE sessions SET revoked_at = now()
WHERE user_id = (SELECT id FROM users WHERE username = 'admin') AND revoked_at IS NULL;
UPDATE refresh_sessions SET revoked_at = now()
WHERE user_id = (SELECT id FROM users WHERE username = 'admin') AND revoked_at IS NULL;
COMMIT;
'@
$sql = $sql.Replace("__HASH__", $hash)

Push-Location $repo
try {
    $compose = @(
        "compose", "--project-name", $projectName, "--env-file", $EnvFile,
        "-f", "compose.dev.yml", "-f", "compose.lan.yml",
        "--profile", "core", "exec", "-T", "postgres",
        "psql", "-U", "whisperx", "-d", "whisperx_atom", "-v", "ON_ERROR_STOP=1", "-At"
    )
    $output = $sql | & docker @compose 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ADMIN_PASSWORD_UPDATE_FAILED" }
    Write-Host "LAN admin password updated and previous sessions revoked. Plaintext was not stored." -ForegroundColor Green
}
finally {
    $password = $null
    $derived = $null
    $hash = $null
    $sql = $null
    Pop-Location
}
