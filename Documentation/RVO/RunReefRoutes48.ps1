param(
    [string]$Unity = 'D:/unityhub/Editor/Unity.exe',
    [ValidateSet('Profile','Tests','Capture')][string]$Stage = 'Tests',
    [string]$Output = 'Documentation/RVO/Verification/ReefRoutes48/Recheck',
    [ValidateSet('d3d11','d3d12')][string]$Api = 'd3d11'
)
$ErrorActionPreference = 'Stop'
$reefRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$reefOutput = [IO.Path]::GetFullPath((Join-Path $reefRoot $Output))
if (Test-Path -LiteralPath $reefOutput) { throw 'Choose a fresh evidence directory.' }
[IO.Directory]::CreateDirectory($reefOutput) | Out-Null
function Invoke-ReefUnity([string]$Name, [string[]]$Extra) {
    $arguments = @('-batchmode','-projectPath',('"'+$reefRoot+'"'),'-logFile',('"'+(Join-Path $reefOutput ($Name+'.log'))+'"')) + $Extra
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Unity $Name failed: $($process.ExitCode)" }
}
if ($Stage -eq 'Profile') {
    # Rebuilds the saved reef scene and navigation assets; run outside Play mode.
    Invoke-ReefUnity 'profile' @('-nographics','-quit','--burst-force-sync-compilation','-executeMethod','Rvo.Editor.ReefPerformance.BuildAndRun','-reef-output',('"'+(Join-Path $reefOutput 'Performance')+'"'))
} elseif ($Stage -eq 'Tests') {
    Invoke-ReefUnity 'navigation-tests' @('-nographics','--burst-force-sync-compilation','-runTests','-testPlatform','EditMode','-testFilter','Rvo.Tests.ReefVariationTests;Rvo.Tests.Phase3VolumeTests;Rvo.Tests.TrafficRecoveryTests','-testResults',('"'+(Join-Path $reefOutput 'navigation-tests.xml')+'"'))
    Invoke-ReefUnity 'caustic-tests' @("-force-$Api",'-runTests','-testPlatform','PlayMode','-testFilter','Rvo.Tests.CausticSamplingTests','-testResults',('"'+(Join-Path $reefOutput 'caustic-tests.xml')+'"'))
} else {
    foreach ($view in @('near','top','grazing','live','overview','repeat-top')) {
        $extra = @("-force-$Api",'--burst-force-sync-compilation','-executeMethod','Rvo.Editor.OceanVisualCapture.Begin','-rvo-scene','Assets/RVO/Demo/OceanReef/OceanReefLive.unity','-rvo-capture',('"'+(Join-Path $reefOutput $view)+'"'))
        if ($view -eq 'overview') { $extra += @('-rvo-shot','2') }
        elseif ($view -eq 'repeat-top') { $extra += @('-rvo-study','top','-rvo-repeat','1') }
        elseif ($view -ne 'live') { $extra += @('-rvo-study',$view) }
        Invoke-ReefUnity $view $extra
    }
}
