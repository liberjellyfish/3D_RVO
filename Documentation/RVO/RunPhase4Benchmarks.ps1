param(
    [string]$Player = "Builds/Phase4Ocean/Phase4Ocean.exe",
    [string]$Output = "Documentation/RVO/Verification/Phase4Continuation/Benchmark",
    [string[]]$Cases = @("mesh-10000", "ocean-10000", "mesh-20000", "ocean-20000", "mesh-30000", "ocean-30000"),
    [int]$Repeats = 3,
    [int]$Frames = 1800,
    [int]$Warmup = 300,
    [ValidateSet("d3d11", "d3d12")][string]$Api = "d3d11"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$playerPath = (Resolve-Path (Join-Path $root $Player)).Path
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $Output))
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$matrix = @{}
foreach ($count in @(10000, 20000, 30000)) {
    $matrix["mesh-$count"] = @("-rvo-count", "$count", "-rvo-no-ocean")
    $matrix["ocean-$count"] = @("-rvo-count", "$count")
}
$matrix["vat-10000"] = @("-rvo-count", "10000", "-rvo-no-ocean", "-rvo-lod", "0", "-rvo-animation", "VertexTexture")
$matrix["bone-10000"] = @("-rvo-count", "10000", "-rvo-no-ocean", "-rvo-lod", "0", "-rvo-animation", "BoneTexture")
$matrix["procedural-10000"] = @("-rvo-count", "10000", "-rvo-no-ocean", "-rvo-lod", "0")
$matrix["cross-10000"] = @("-rvo-count", "10000", "-rvo-no-ocean", "-rvo-lod", "2", "-rvo-far", "CrossQuads")
$matrix["billboard-10000"] = @("-rvo-count", "10000", "-rvo-no-ocean", "-rvo-lod", "3", "-rvo-far", "Billboard")
$matrix["fog-only-10000"] = @("-rvo-count", "10000", "-rvo-caustic", "Off")
$matrix["caustic512-10000"] = @("-rvo-count", "10000", "-rvo-caustic", "Shared512")
$matrix["direct-10000"] = @("-rvo-count", "10000", "-rvo-caustic", "DirectReference")
function Percentile($values, $fraction) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    return $sorted[[Math]::Max(0, [Math]::Ceiling($fraction * $sorted.Count) - 1)]
}
$summary = @()
foreach ($case in $Cases) {
    if (!$matrix.ContainsKey($case)) { throw "Unknown case: $case" }
    for ($run = 1; $run -le $Repeats; $run++) {
        $directory = Join-Path $outputPath "$case-$Api-run$run"
        if (Test-Path (Join-Path $directory "frames.csv")) { throw "Evidence already exists: $directory. Choose a fresh output directory." }
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $arguments = @("-force-$Api", "-screen-fullscreen", "0", "-screen-width", "1920", "-screen-height", "1080",
            "-rvo-benchmark", "-rvo-offscreen", "-rvo-fixed-replay", "-rvo-frames", "$Frames", "-rvo-warmup", "$Warmup",
            "-rvo-output", ('"' + $directory + '"'), "-logFile", ('"' + (Join-Path $directory "player.log") + '"')) + $matrix[$case]
        # 串行独占 GPU；隐藏窗口仅使用显式离屏渲染，结果不包含窗口呈现。
        $process = Start-Process -FilePath $playerPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Player failed: $case (exit $($process.ExitCode))" }
        $rows = @(Import-Csv (Join-Path $directory "frames.csv"))
        $frameMs = @($rows | ForEach-Object { [double]::Parse($_.frame_ms, [Globalization.CultureInfo]::InvariantCulture) })
        $gpu = @($rows | Where-Object { [double]$_.latest_gpu_ms -gt 0 } | Group-Object gpu_timing_timestamp | ForEach-Object { [double]$_.Group[0].latest_gpu_ms })
        $summary += [ordered]@{
            case = $case; run = $run; frames = $rows.Count; cpu_frame_p50 = (Percentile $frameMs 0.5)
            cpu_frame_p95 = (Percentile $frameMs 0.95); cpu_frame_p99 = (Percentile $frameMs 0.99)
            gpu_unique_samples = $gpu.Count; gpu_p95 = (Percentile $gpu 0.95)
            sample_seconds = ($frameMs | Measure-Object -Sum).Sum / 1000
            dropped_ticks_delta = [long]$rows[-1].dropped_ticks - [long]$rows[0].dropped_ticks
            note = "Fixed synthetic replay; CPU loop is not GPU time; null GPU means unavailable."
        }
        $summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $outputPath "summary.json")
        Write-Output "$case run $run complete; GPU unique samples=$($gpu.Count)"
    }
}
