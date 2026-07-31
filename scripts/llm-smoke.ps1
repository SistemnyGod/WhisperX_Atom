[CmdletBinding()]
param(
  [string]$BaseUrl = "http://127.0.0.1:8081",
  [int]$TimeoutSeconds = 600,
  [switch]$Start
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
if ($Start) {
  docker compose -f compose.dev.yml --profile llm up -d llama-server
  if ($LASTEXITCODE -ne 0) { throw "Unable to start llama-server" }
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
  try {
    $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 5
    if ($health.status -eq "ok") { break }
  } catch { }
  Start-Sleep -Seconds 3
} while ((Get-Date) -lt $deadline)
if ((Get-Date) -ge $deadline) { throw "llama-server did not become ready in $TimeoutSeconds seconds" }

$schema = @{
  type = "object"
  required = @("status", "message")
  additionalProperties = $false
  properties = @{
    status = @{ type = "string"; enum = @("ok") }
    message = @{ type = "string"; minLength = 1 }
  }
}
$body = @{
  model = "qwen3-8b"
  messages = @(
    @{ role = "system"; content = "Верни только JSON без пояснений." }
    @{ role = "user"; content = "Подтверди готовность локальной модели по-русски." }
  )
  temperature = 0.0
  max_tokens = 256
  response_format = @{ type = "json_object"; schema = $schema }
} | ConvertTo-Json -Depth 10

$http = [Net.Http.HttpClient]::new()
try {
  $payload = [Net.Http.ByteArrayContent]::new([Text.Encoding]::UTF8.GetBytes($body))
  $payload.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new("application/json")
  $httpResponse = $http.PostAsync("$BaseUrl/v1/chat/completions", $payload).GetAwaiter().GetResult()
  $responseBytes = $httpResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
  $responseText = [Text.Encoding]::UTF8.GetString($responseBytes)
  if (-not $httpResponse.IsSuccessStatusCode) { throw "LLM HTTP $([int]$httpResponse.StatusCode): $responseText" }
} finally {
  $http.Dispose()
}
$response = $responseText | ConvertFrom-Json
$content = $response.choices[0].message.content
if ([string]::IsNullOrWhiteSpace($content)) { throw "LLM returned an empty response" }
$parsed = $content | ConvertFrom-Json
if ($parsed.status -ne "ok" -or [string]::IsNullOrWhiteSpace($parsed.message)) { throw "Unexpected LLM response: $content" }
Write-Host "LLM smoke passed"
Write-Host $content