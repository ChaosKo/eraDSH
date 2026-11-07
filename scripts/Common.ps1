# Shared configuration for the eraDSH bridge scripts. Windows PowerShell 5.1; ASCII source.
#
# Nothing in this repository hardcodes a game folder, an engine file name, or a
# path outside the checkout. Every script resolves the target game through
# Get-EraDshSettings, which uses this order for each value:
#
#   gameRoot          explicit parameter -> env ERADSH_GAME_ROOT -> configuration
#                     file -> the current working directory
#   engineExecutable  explicit parameter -> env ERADSH_ENGINE_EXE -> configuration
#                     file -> auto-detected in gameRoot
#   engineAssemblyVersion, runtimeDirectory   same ladder, both optional
#   runtimeMode       explicit parameter -> env ERADSH_RUNTIME_MODE -> configuration
#                     file -> inplace. `inplace` runs the engine from the game folder
#                     and copies nothing, so the game writes its own saves there;
#                     `copy` mirrors the folder into the runtime directory instead.
#
# The common case therefore needs no configuration at all: open a shell (or a
# DSH workspace) in the game folder and run the build. A configuration file is
# only needed when the game folder is somewhere else; it lives at the repository
# root, is named erabridge.config.json, and is ignored by git because it names a
# machine-local folder. Copy erabridge.config.example.json to create it.

$script:EraDshScriptsRoot = $PSScriptRoot
$script:EraDshRepositoryRoot = Split-Path -Parent $script:EraDshScriptsRoot

# Engine assembly versions this adapter has actually been exercised against.
# The list is used only to prefer a candidate when a folder holds several
# Emuera builds; an untested engine still runs but is reported as unverified,
# and any missing member surfaces as a host error instead of a silent mismatch.
$script:EraDshTestedEngineVersions = @('1.821.5843.38796')

# The assembly name every Emuera build uses. The host rejects anything else.
$script:EraDshEngineAssemblyName = 'Emuera'
# Namespace marker used only to tell a .NET Core single-file Emuera variant apart
# from an unrelated native executable; it is never loaded or trusted.
$script:EraDshEngineNamespace = 'MinorShift.Emuera'

function Get-EraDshRepositoryRoot { return $script:EraDshRepositoryRoot }

function Get-EraDshConfigPath {
 param([string]$ConfigPath)
 if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) { return [IO.Path]::GetFullPath($ConfigPath) }
 if (-not [string]::IsNullOrWhiteSpace($env:ERADSH_CONFIG)) { return [IO.Path]::GetFullPath($env:ERADSH_CONFIG) }
 return (Join-Path $script:EraDshRepositoryRoot 'erabridge.config.json')
}

function Read-EraDshConfigFile {
 param([string]$ConfigPath)
 $values = @{}
 if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return $values }
 $raw = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
 if ([string]::IsNullOrWhiteSpace($raw)) { return $values }
 try { $parsed = $raw | ConvertFrom-Json }
 catch { throw ('Cannot parse configuration file: ' + $ConfigPath + ' :: ' + $_.Exception.Message) }
 foreach ($property in $parsed.PSObject.Properties) {
  if ($null -eq $property.Value) { continue }
  $values[$property.Name] = [string]$property.Value
 }
 return $values
}

# Empty strings count as "not set", so a configuration file may leave a field
# blank to accept the next level of the ladder.
function Test-EraDshValue {
 param([object]$Value)
 if ($null -eq $Value) { return $false }
 return -not [string]::IsNullOrWhiteSpace([string]$Value)
}

# Relative paths resolve against the repository root, so a configuration file
# may use either an absolute game folder or one relative to the checkout.
function Resolve-EraDshPath {
 param([string]$Path,[string]$BaseDirectory)
 if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
 $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim())
 if ($expanded.StartsWith('~')) {
  $expanded = Join-Path $env:USERPROFILE $expanded.Substring(1).TrimStart('\','/')
 }
 if ([IO.Path]::IsPathRooted($expanded)) { return [IO.Path]::GetFullPath($expanded) }
 return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $expanded))
}

function Test-EraDshSamePath {
 param([string]$Left,[string]$Right)
 if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
 $a = [IO.Path]::GetFullPath($Left).TrimEnd('\','/')
 $b = [IO.Path]::GetFullPath($Right).TrimEnd('\','/')
 return [string]::Equals($a,$b,[StringComparison]::OrdinalIgnoreCase)
}

function New-EraDshSetupMessage {
 param([string]$ConfigPath,[string]$Reason)
 $lines = New-Object System.Collections.Generic.List[string]
 [void]$lines.Add($Reason)
 [void]$lines.Add('')
 [void]$lines.Add('The bridge looks for the game in this order:')
 [void]$lines.Add('  1. -GameRoot <folder> -EngineExecutable <file.exe> on the command line')
 [void]$lines.Add('  2. environment variables ERADSH_GAME_ROOT and ERADSH_ENGINE_EXE')
 [void]$lines.Add('  3. ' + $ConfigPath)
 [void]$lines.Add('  4. the current working directory, with the engine detected inside it')
 [void]$lines.Add('')
 [void]$lines.Add('So either run the script from the Era game folder, or write the location once:')
 [void]$lines.Add('  (copy erabridge.config.example.json to erabridge.config.json, then set gameRoot)')
 [void]$lines.Add('')
 [void]$lines.Add('Detection accepts an .exe whose assembly name is Emuera and records its assembly version.')
 return ($lines -join [Environment]::NewLine)
}

function New-EraDshUnsupportedEngineMessage {
 param([string]$Path,[string]$Kind)
 $lines = New-Object System.Collections.Generic.List[string]
 [void]$lines.Add('engineExecutable is a ' + (Get-EraDshEngineKindLabel -Kind $Kind) + ': ' + $Path)
 [void]$lines.Add('')
 [void]$lines.Add('This adapter loads the engine as a .NET Framework assembly and reflects into it.')
 [void]$lines.Add('A .NET Core build cannot be loaded that way, so it cannot be driven here.')
 [void]$lines.Add('')
 [void]$lines.Add('Options:')
 [void]$lines.Add('  1. use the .NET Framework build of the same game, if it ships one')
 [void]$lines.Add('  2. keep the .NET Core build for normal play; this bridge cannot automate it yet')
 [void]$lines.Add('')
 [void]$lines.Add('See docs/engine-compatibility.md for the tested matrix.')
 return ($lines -join [Environment]::NewLine)
}

# Classifies an executable without running it:
#   framework - a .NET Framework assembly this adapter can load by reflection
#   core      - a .NET (Core) build, usually published as a single-file bundle;
#               it has no loadable CLR header in the .exe, so reflection cannot reach it
#   native    - anything else (plain native launcher, updater tool, ...)
# A .NET Core single-file bundle carries the CoreCLR hosting strings and the
# Emuera namespace inside the file, which is what distinguishes it from a random
# native executable.
function Get-EraDshEngineKind {
 param([string]$Path)
 $assemblyName = $null
 try { $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($Path) } catch { $assemblyName = $null }
 if ($null -ne $assemblyName) {
  if ($assemblyName.Name -eq $script:EraDshEngineAssemblyName) { return 'framework' }
  return 'managed-other'
 }
 try {
  $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($Path))
  if ($text.IndexOf('hostfxr', [StringComparison]::Ordinal) -ge 0 -and
      $text.IndexOf($script:EraDshEngineNamespace, [StringComparison]::Ordinal) -ge 0) { return 'core' }
 } catch { return 'unreadable' }
 return 'native'
}

function Get-EraDshEngineKindLabel {
 param([string]$Kind)
 switch ($Kind) {
  'framework' { return 'NET Framework Emuera (supported)' }
  'core' { return '.NET Core single-file build (not loadable by reflection)' }
  'managed-other' { return 'managed .NET assembly, but not an Emuera build' }
  'unreadable' { return 'unreadable file' }
  default { return 'native executable (not the engine this adapter drives)' }
 }
}

# Lists every executable directly inside a folder, classified. Only the top level
# is scanned: every Emuera build keeps its launcher next to CSV/ERB/resources.
function Get-EraDshEngineCandidates {
 param([string]$GameRoot)
 $candidates = @()
 foreach ($file in Get-ChildItem -LiteralPath $GameRoot -File -Filter '*.exe' -ErrorAction SilentlyContinue) {
  $kind = Get-EraDshEngineKind -Path $file.FullName
  $assemblyName = $null
  try { $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { $assemblyName = $null }
  $version = if ($null -ne $assemblyName -and $null -ne $assemblyName.Version) { $assemblyName.Version.ToString() } else { '' }
  $candidates += [pscustomobject]@{
   Path = $file.FullName
   FileName = $file.Name
   Kind = $kind
   KindLabel = (Get-EraDshEngineKindLabel -Kind $kind)
   AssemblyName = if ($null -ne $assemblyName) { $assemblyName.Name } else { '' }
   Version = $version
   Tested = ($null -ne $assemblyName -and $assemblyName.Name -eq $script:EraDshEngineAssemblyName -and $script:EraDshTestedEngineVersions -contains $version)
  }
 }
 return $candidates
}

function Format-EraDshEngineList {
 param([object[]]$Candidates)
 return (($Candidates | ForEach-Object { '  ' + $_.FileName + ' - ' + $_.KindLabel }) -join [Environment]::NewLine)
}

function Get-EraDshSettings {
 [CmdletBinding()]
 param(
  [string]$GameRoot,
  [string]$EngineExecutable,
  [string]$EngineAssemblyVersion,
  [string]$RuntimeDirectory,
  [string]$RuntimeMode,
  [string]$ConfigPath
 )
 $repositoryRoot = $script:EraDshRepositoryRoot
 $resolvedConfigPath = Get-EraDshConfigPath -ConfigPath $ConfigPath
 $file = Read-EraDshConfigFile -ConfigPath $resolvedConfigPath
 $configFound = Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf

 # ---- game folder ----
 $gameRootValue = $null
 $gameRootSource = $null
 if (Test-EraDshValue $GameRoot) { $gameRootValue = $GameRoot; $gameRootSource = 'parameter' }
 elseif (Test-EraDshValue $env:ERADSH_GAME_ROOT) { $gameRootValue = $env:ERADSH_GAME_ROOT; $gameRootSource = 'environment' }
 elseif (Test-EraDshValue $file['gameRoot']) { $gameRootValue = $file['gameRoot']; $gameRootSource = 'configuration' }
 else { $gameRootValue = (Get-Location).Path; $gameRootSource = 'current-directory' }

 $gameRootPath = Resolve-EraDshPath -Path $gameRootValue -BaseDirectory $repositoryRoot
 if (-not (Test-Path -LiteralPath $gameRootPath -PathType Container)) {
  throw (New-EraDshSetupMessage -ConfigPath $resolvedConfigPath -Reason ('gameRoot does not exist: ' + $gameRootPath))
 }
 if (Test-EraDshSamePath -Left $gameRootPath -Right $repositoryRoot) {
  throw (New-EraDshSetupMessage -ConfigPath $resolvedConfigPath -Reason ('gameRoot resolved to the plugin checkout itself (' + $gameRootPath + '); the game folder is a different directory.'))
 }

 # ---- engine ----
 $engineValue = $null
 $engineKind = $null
 $engineSource = $null
 $engineVerified = $false
 if (Test-EraDshValue $EngineExecutable) { $engineValue = $EngineExecutable; $engineSource = 'parameter' }
 elseif (Test-EraDshValue $env:ERADSH_ENGINE_EXE) { $engineValue = $env:ERADSH_ENGINE_EXE; $engineSource = 'environment' }
 elseif (Test-EraDshValue $file['engineExecutable']) { $engineValue = $file['engineExecutable']; $engineSource = 'configuration' }

 if ($engineValue) {
  $enginePath = Resolve-EraDshPath -Path $engineValue -BaseDirectory $gameRootPath
  if (-not (Test-Path -LiteralPath $enginePath -PathType Leaf)) {
   throw (New-EraDshSetupMessage -ConfigPath $resolvedConfigPath -Reason ('engineExecutable does not exist: ' + $enginePath))
  }
  $engineKind = Get-EraDshEngineKind -Path $enginePath
  if ($engineKind -eq 'native' -or $engineKind -eq 'unreadable') {
   throw ('engineExecutable is not a managed .NET assembly (' + $engineKind + '): ' + $enginePath + '.' + [Environment]::NewLine +
     'This adapter loads the engine by reflection and only drives .NET Framework Emuera builds; see docs/engine-compatibility.md.')
  }
  if ($engineKind -eq 'core') {
   # A bundled .NET build has no loadable CLR header; scripts/Build-Bridge.ps1 drives
   # it through the startup-hook adapter instead of reflection, so no version exists.
   $engineVersion = ''
  } else {
   $engineIdentity = [Reflection.AssemblyName]::GetAssemblyName($enginePath)
   if ($engineIdentity.Name -ne $script:EraDshEngineAssemblyName) {
    throw ('engineExecutable is a .NET assembly but not an Emuera build (' + $engineIdentity.Name + '): ' + $enginePath)
   }
   $engineVersion = $engineIdentity.Version.ToString()
  }
  $engineVerified = ($script:EraDshTestedEngineVersions -contains $engineVersion)
  if (Test-EraDshValue $EngineAssemblyVersion -or Test-EraDshValue $env:ERADSH_ENGINE_VERSION -or (Test-EraDshValue $file['engineAssemblyVersion'])) {
   $expected = $null
   if (Test-EraDshValue $EngineAssemblyVersion) { $expected = $EngineAssemblyVersion }
   elseif (Test-EraDshValue $env:ERADSH_ENGINE_VERSION) { $expected = $env:ERADSH_ENGINE_VERSION }
   else { $expected = $file['engineAssemblyVersion'] }
   if ($expected -ne $engineVersion) {
    throw ('Configured engineAssemblyVersion ' + $expected + ' does not match ' + $enginePath + ' (' + $engineVersion + ')')
   }
  }
 } else {
  $discovered = @(Get-EraDshEngineCandidates -GameRoot $gameRootPath)
  # Both families are drivable now: the .NET Framework build through reflection, the
  # bundled .NET build through the startup-hook adapter. Native tools are not engines.
  $candidates = @($discovered | Where-Object { $_.Kind -eq 'framework' -or $_.Kind -eq 'core' })
  if ($candidates.Count -eq 0) {
   if ($discovered.Count -gt 0) {
    throw ('No Emuera engine found in ' + $gameRootPath + '. Executables present:' + [Environment]::NewLine +
      (Format-EraDshEngineList $discovered) + [Environment]::NewLine + [Environment]::NewLine +
      'Expected either a .NET Framework Emuera assembly or a bundled .NET Emuera executable. See docs/engine-compatibility.md.')
   }
   throw (New-EraDshSetupMessage -ConfigPath $resolvedConfigPath -Reason ('No executable found in ' + $gameRootPath + '.'))
  }
  $tested = @($candidates | Where-Object { $_.Tested })
  if ($tested.Count -eq 1) {
   $chosen = $tested[0]
  } elseif ($tested.Count -gt 1) {
   throw ('Several supported Emuera builds are present in ' + $gameRootPath + '; pick one with engineExecutable:' + [Environment]::NewLine + (Format-EraDshEngineList $tested))
  } elseif ($candidates.Count -eq 1) {
   $chosen = $candidates[0]
  } else {
   throw ('Several Emuera builds are present in ' + $gameRootPath + ' and none is a tested version; pick one with engineExecutable:' + [Environment]::NewLine + (Format-EraDshEngineList $candidates))
  }
  $enginePath = $chosen.Path
  $engineValue = $chosen.FileName
  $engineVersion = $chosen.Version
  $engineKind = $chosen.Kind
  $engineVerified = $chosen.Tested
  $engineSource = 'detected'
 }
 $engineName = [IO.Path]::GetFileName($enginePath)

 # ---- runtime mode ----
 # copy    : the game folder is mirrored into <runtime>, and the bridge writes only there.
 # inplace : no mirror; the engine runs from the game folder itself, so the game writes
 #           its own Sav/Debug/Dat and logs into the original directory.
 $runtimeModeValue = $null
 if (Test-EraDshValue $RuntimeMode) { $runtimeModeValue = $RuntimeMode }
 elseif (Test-EraDshValue $env:ERADSH_RUNTIME_MODE) { $runtimeModeValue = $env:ERADSH_RUNTIME_MODE }
 elseif (Test-EraDshValue $file['runtimeMode']) { $runtimeModeValue = $file['runtimeMode'] }
 else { $runtimeModeValue = 'inplace' }
 $runtimeModeValue = $runtimeModeValue.Trim().ToLowerInvariant()
 if ($runtimeModeValue -ne 'copy' -and $runtimeModeValue -ne 'inplace') {
  throw ('runtimeMode must be copy or inplace, not ' + $runtimeModeValue)
 }

 # ---- isolated runtime ----
 $runtimeValue = $null
 if (Test-EraDshValue $RuntimeDirectory) { $runtimeValue = $RuntimeDirectory }
 elseif (Test-EraDshValue $env:ERADSH_RUNTIME_DIR) { $runtimeValue = $env:ERADSH_RUNTIME_DIR }
 elseif (Test-EraDshValue $file['runtimeDirectory']) { $runtimeValue = $file['runtimeDirectory'] }
 elseif ($runtimeModeValue -eq 'inplace') { $runtimeValue = Join-Path 'runtime' 'host' }
 else { $runtimeValue = Join-Path 'runtime' 'instance' }
 $runtimePath = Resolve-EraDshPath -Path $runtimeValue -BaseDirectory $repositoryRoot

 if (Test-EraDshSamePath -Left $runtimePath -Right $gameRootPath) {
  throw 'runtimeDirectory must not be the game folder itself; the bridge needs its own directory.'
 }
 if (Test-EraDshSamePath -Left $runtimePath -Right $repositoryRoot) {
  throw 'runtimeDirectory must be a subdirectory, not the repository root.'
 }

 return [pscustomobject]@{
  RepositoryRoot = $repositoryRoot
  ScriptsRoot = $script:EraDshScriptsRoot
  ConfigPath = $resolvedConfigPath
  ConfigFound = $configFound
  GameRoot = $gameRootPath
  GameRootSource = $gameRootSource
  EngineExecutable = $engineValue
  EnginePath = $enginePath
  EngineSource = $engineSource
  EngineKind = $engineKind
  EngineVerified = $engineVerified
  RuntimeMode = $runtimeModeValue
  RuntimeDirectory = $runtimeValue
  RuntimePath = $runtimePath
 }
}
