#Requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'v1'
$schema = Get-Content (Join-Path $root 'schema.json') -Raw
$cases = Get-Content (Join-Path $root 'fixtures.json') -Raw | ConvertFrom-Json
if (@($cases).Count -eq 0) { throw 'No fixtures registered.' }

# Every standalone fixture must be registered, so newly added files cannot be skipped.
$files = @(Get-ChildItem (Join-Path $root 'examples'), (Join-Path $root 'invalid') -Filter '*.json' -File |
    ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
if (@($cases.file | Select-Object -Unique).Count -ne @($cases).Count) {
    throw 'Duplicate fixture registrations.'
}
if (Compare-Object $files @($cases.file)) { throw 'Fixture registrations do not match files.' }

foreach ($case in $cases) {
    $json = Get-Content (Join-Path $root $case.file) -Raw
    # Malformed JSON is a test-data error, not a successful negative test.
    $document = ConvertFrom-Json -InputObject $json -AsHashtable
    $schemaValid = Test-Json -Json $json -Schema $schema -ErrorAction SilentlyContinue

    # Test-Json does not enforce format: date-time on all PowerShell versions.
    # The schema supplies the UTC lexical restrictions; XmlConvert checks calendar/time values.
    if ($schemaValid) {
        try {
            # Read the original string: ConvertFrom-Json may convert timestamps to DateTime.
            $parsedJson = [System.Text.Json.JsonDocument]::Parse($json)
            try { $timestamp = $parsedJson.RootElement.GetProperty('publishedAt').GetString() }
            finally { $parsedJson.Dispose() }
            $null = [System.Xml.XmlConvert]::ToDateTimeOffset($timestamp)
        }
        catch [System.FormatException] { $schemaValid = $false }
    }
    if ($schemaValid -ne $case.schemaValid) {
        throw "$($case.file): expected schemaValid=$($case.schemaValid), got $schemaValid ($($case.reason))."
    }

    # Cross-field filename matching is explicitly outside standard JSON Schema.
    $contractValid = $schemaValid
    if ($contractValid) {
        $regionName = $document.regionId.Split('/')[-1]
        foreach ($artifact in $document.artifacts) {
            $expected = "$regionName.$($artifact.artifactVersion).$($artifact.type)"
            if ($artifact.objectKey.Split('/')[-1] -cne $expected) { $contractValid = $false }
        }
    }
    if ($contractValid -ne $case.contractValid) {
        throw "$($case.file): expected contractValid=$($case.contractValid), got $contractValid ($($case.reason))."
    }
    Write-Output "PASS $($case.file)"
}
Write-Output "Passed $(@($cases).Count) fixtures (schema with timestamp format checks, plus filename contract checks)."
