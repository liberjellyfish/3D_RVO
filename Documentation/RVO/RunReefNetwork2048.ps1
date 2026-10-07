param(
    [string]$Unity='D:/unityhub/Editor/Unity.exe',
    [ValidateSet('Profile','Tests','Capture')][string]$Stage='Tests',
    [string]$Output='Documentation/RVO/Verification/ReefNetwork2048/Recheck'
)
$ErrorActionPreference='Stop'
$networkRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$networkOutput=[IO.Path]::GetFullPath((Join-Path $networkRoot $Output))
if(Test-Path -LiteralPath $networkOutput) { throw 'Choose a fresh output directory.' }
[IO.Directory]::CreateDirectory($networkOutput) | Out-Null
function Invoke-NetworkUnity([string]$Name,[string[]]$Extra) {
    $arguments=@('-batchmode','-projectPath',('"'+$networkRoot+'"'),'--burst-force-sync-compilation','-logFile',('"'+(Join-Path $networkOutput ($Name+'.log'))+'"'))+$Extra
    $process=Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if($process.ExitCode -ne 0) { throw "Unity $Name failed: $($process.ExitCode)" }
}
if($Stage -eq 'Profile') {
    Invoke-NetworkUnity 'profile' @('-nographics','-quit','-executeMethod','Rvo.Editor.ReefPerformance.BuildAndRun','-reef-output',('"'+(Join-Path $networkOutput 'Performance')+'"'))
} elseif($Stage -eq 'Tests') {
    Invoke-NetworkUnity 'navigation' @('-nographics','-runTests','-testPlatform','EditMode','-testFilter','Rvo.Tests.ReefVariationTests;Rvo.Tests.Phase3VolumeTests;Rvo.Tests.TrafficRecoveryTests','-testResults',('"'+(Join-Path $networkOutput 'navigation.xml')+'"'))
    Invoke-NetworkUnity 'presentation' @('-force-d3d11','-runTests','-testPlatform','PlayMode','-testFilter','Rvo.Tests.ReefNetworkPresentationTests','-testResults',('"'+(Join-Path $networkOutput 'presentation.xml')+'"'))
} else {
    foreach($view in @('overview','top','proxies')) {
        $extra=@('-force-d3d11','-executeMethod','Rvo.Editor.OceanVisualCapture.Begin','-rvo-scene','Assets/RVO/Demo/OceanReef/OceanReefLive.unity','-rvo-shot',$(if($view -eq 'overview'){'0'}else{'2'}),'-rvo-capture',('"'+(Join-Path $networkOutput $view)+'"'))
        if($view -eq 'proxies') { $extra+=@('-rvo-proxies','1') }
        Invoke-NetworkUnity $view $extra
    }
}
