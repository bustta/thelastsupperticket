#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$Region = "ap-northeast-1",
    [string]$RepositoryName = "thelastsupperticket",
    [string]$FunctionName = "the-last-supper-ticket",
    [string]$StackName = "TheLastSupperTicket-Stack",
    [string]$TemplateFilePath = "serverless.yaml",
    [string[]]$StackCapabilities = @("CAPABILITY_IAM"),
    [string]$Platform = "linux/amd64",
    [string]$EnvFilePath = ".env",
    [switch]$DeployStack,
    [switch]$DeployStackOnly,
    [switch]$SkipInvoke,
    [switch]$SkipEnvSync,
    [switch]$SkipEcrLifecyclePolicy,
    [switch]$NoBuild,
    [switch]$NoPush,
    [string]$BuildCacheTag = "buildcache",
    [switch]$DisableBuildCache,
    [int]$KeepTaggedImageCount = 5,
    [int]$KeepBuildCacheImageCount = 2,
    [int]$ExpireUntaggedAfterDays = 3
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Assert-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

function Test-DockerDaemon {
    $null = docker info 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Ensure-EcrRepository {
    param(
        [string]$Repo,
        [string]$AwsRegion
    )

    $exists = aws ecr describe-repositories --repository-names $Repo --region $AwsRegion --query "repositories[0].repositoryName" --output text 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($exists) -or $exists -eq "None") {
        Write-Host "ECR repository '$Repo' not found. Creating..." -ForegroundColor Yellow
        aws ecr create-repository --repository-name $Repo --region $AwsRegion | Out-Null
        Write-Host "Created ECR repository '$Repo'." -ForegroundColor Green
    }
    else {
        Write-Host "ECR repository '$Repo' exists." -ForegroundColor Green
    }
}

function Ensure-EcrLifecyclePolicy {
        param(
                [string]$Repo,
                [string]$AwsRegion,
                [string]$BuildCacheImageTag,
                [int]$TaggedImageRetention,
                [int]$BuildCacheRetention,
                [int]$UntaggedExpireDays
        )

        if ($TaggedImageRetention -lt 1) {
                throw "KeepTaggedImageCount must be >= 1."
        }

        if ($BuildCacheRetention -lt 1) {
                throw "KeepBuildCacheImageCount must be >= 1."
        }

        if ($UntaggedExpireDays -lt 1) {
                throw "ExpireUntaggedAfterDays must be >= 1."
        }

        $policyText = @"
{
    "rules": [
        {
            "rulePriority": 1,
            "description": "Retain latest build cache images",
            "selection": {
                "tagStatus": "tagged",
                "tagPrefixList": ["$BuildCacheImageTag"],
                "countType": "imageCountMoreThan",
                "countNumber": $BuildCacheRetention
            },
            "action": {
                "type": "expire"
            }
        },
        {
            "rulePriority": 2,
            "description": "Expire untagged images after configured days",
            "selection": {
                "tagStatus": "untagged",
                "countType": "sinceImagePushed",
                "countUnit": "days",
                "countNumber": $UntaggedExpireDays
            },
            "action": {
                "type": "expire"
            }
        },
        {
            "rulePriority": 3,
            "description": "Retain latest images overall",
            "selection": {
                "tagStatus": "any",
                "countType": "imageCountMoreThan",
                "countNumber": $TaggedImageRetention
            },
            "action": {
                "type": "expire"
            }
        }
    ]
}
"@

        $tempPolicyFile = [System.IO.Path]::GetTempFileName()
        try {
                $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
                [System.IO.File]::WriteAllText($tempPolicyFile, $policyText, $utf8WithoutBom)

                aws ecr put-lifecycle-policy --repository-name $Repo --region $AwsRegion --lifecycle-policy-text "file://$tempPolicyFile" --query "lifecyclePolicyText" --output text | Out-Null
                if ($LASTEXITCODE -ne 0) {
                        throw "Failed to apply ECR lifecycle policy for repository '$Repo'."
                }

                Write-Host "ECR lifecycle policy applied: keep tagged=$TaggedImageRetention, keep cache=$BuildCacheRetention, expire untagged after $UntaggedExpireDays days." -ForegroundColor Green
        }
        finally {
                Remove-Item -Path $tempPolicyFile -ErrorAction SilentlyContinue
        }
}

function Read-DotEnvFile {
    param([string]$Path)

    $result = @{}

    if (-not (Test-Path $Path)) {
        return $result
    }

    $lines = Get-Content -Path $Path
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
            continue
        }

        $separatorIndex = $trimmed.IndexOf("=")
        if ($separatorIndex -lt 1) {
            continue
        }

        $key = $trimmed.Substring(0, $separatorIndex).Trim()
        $value = $trimmed.Substring($separatorIndex + 1)

        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $result[$key] = $value
        }
    }

    return $result
}

function Convert-ToHashtable {
    param([object]$InputObject)

    $table = @{}
    if ($null -eq $InputObject) {
        return $table
    }

    $properties = $InputObject.PSObject.Properties
    foreach ($property in $properties) {
        $table[$property.Name] = [string]$property.Value
    }

    return $table
}

function Sync-LambdaEnvironmentVariables {
    param(
        [string]$LambdaFunctionName,
        [string]$AwsRegion,
        [string]$DotEnvPath
    )

    if (-not (Test-Path $DotEnvPath)) {
        Write-Host "Env file '$DotEnvPath' not found. Skip environment sync." -ForegroundColor Yellow
        return
    }

    $dotEnvVariables = Read-DotEnvFile -Path $DotEnvPath
    if ($dotEnvVariables.Count -eq 0) {
        Write-Host "No variables found in '$DotEnvPath'. Skip environment sync." -ForegroundColor Yellow
        return
    }

    $currentRawJson = aws lambda get-function-configuration --function-name $LambdaFunctionName --region $AwsRegion --query "Environment.Variables" --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch current Lambda environment variables."
    }

    $currentObject = $null
    if (-not [string]::IsNullOrWhiteSpace($currentRawJson) -and $currentRawJson.Trim() -ne "null") {
        $currentObject = $currentRawJson | ConvertFrom-Json
    }

    $mergedVariables = Convert-ToHashtable -InputObject $currentObject

    foreach ($entry in $dotEnvVariables.GetEnumerator()) {
        $mergedVariables[$entry.Key] = $entry.Value
    }

    $tempEnvironmentFile = [System.IO.Path]::GetTempFileName()
    try {
        $environmentPayload = @{ Variables = $mergedVariables }
        $environmentJson = $environmentPayload | ConvertTo-Json -Depth 4
        $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($tempEnvironmentFile, $environmentJson, $utf8WithoutBom)

        aws lambda update-function-configuration --function-name $LambdaFunctionName --region $AwsRegion --environment "file://$tempEnvironmentFile" --query "{FunctionName:FunctionName,LastUpdateStatus:LastUpdateStatus}" --output table
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update Lambda environment variables."
        }

        aws lambda wait function-updated --function-name $LambdaFunctionName --region $AwsRegion
        if ($LASTEXITCODE -ne 0) {
            throw "Lambda environment update did not complete successfully."
        }

        Write-Host "Synced $($dotEnvVariables.Count) variables from '$DotEnvPath' to Lambda." -ForegroundColor Green
    }
    finally {
        Remove-Item -Path $tempEnvironmentFile -ErrorAction SilentlyContinue
    }
}

Write-Step "Preflight checks"
Assert-Command "aws"

$accountId = (aws sts get-caller-identity --query Account --output text).Trim()
if ([string]::IsNullOrWhiteSpace($accountId) -or $accountId -eq "None") {
    throw "Unable to read AWS account ID. Check your AWS CLI credentials."
}

$imageBase = "$accountId.dkr.ecr.$Region.amazonaws.com/$RepositoryName"
$imageTag = "latest"
$imageWithTag = "${imageBase}:${imageTag}"
$buildCacheRef = "${imageBase}:${BuildCacheTag}"

if ($DeployStackOnly) {
    $DeployStack = $true
}

$needsDocker = -not $DeployStackOnly -and -not $NoBuild

if ($needsDocker) {
    Assert-Command "docker"

    docker buildx version | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "docker buildx is required but unavailable."
    }

    if (-not (Test-DockerDaemon)) {
        throw "Docker daemon is not reachable. Start Docker Desktop and wait until engine is running, then rerun. If you only need CloudFormation changes, run with -DeployStackOnly."
    }
}

Write-Host "AWS Account : $accountId"
Write-Host "Region      : $Region"
Write-Host "Repository  : $RepositoryName"
Write-Host "Function    : $FunctionName"
Write-Host "Image       : $imageWithTag"
if (-not $DisableBuildCache) {
    Write-Host "Build cache : $buildCacheRef"
}
if (-not $SkipEcrLifecyclePolicy) {
    Write-Host "ECR policy  : keep tagged=$KeepTaggedImageCount, keep cache=$KeepBuildCacheImageCount, untagged days=$ExpireUntaggedAfterDays"
}

if ($DeployStack) {
    Write-Host "Stack       : $StackName"
    Write-Host "Template    : $TemplateFilePath"
}

if ($DeployStack) {
    Write-Step "Deploy CloudFormation stack"

    if (-not (Test-Path $TemplateFilePath)) {
        throw "Template file not found: $TemplateFilePath"
    }

    $parameterOverrides = @(
        "FunctionName=$FunctionName",
        "ImageRepositoryName=$RepositoryName",
        "ImageTag=$imageTag"
    )

    aws cloudformation deploy --template-file $TemplateFilePath --stack-name $StackName --capabilities $StackCapabilities --parameter-overrides $parameterOverrides --region $Region
    if ($LASTEXITCODE -ne 0) {
        throw "CloudFormation deployment failed."
    }

    Write-Host "CloudFormation deployment completed." -ForegroundColor Green
}

if ($DeployStackOnly) {
    Write-Step "Skip image/code update"
    Write-Host "Stack-only mode enabled. Skipping image build/push, Lambda code update, environment sync, and invoke." -ForegroundColor Yellow
    Write-Step "Done"
    Write-Host "Stack deployment completed successfully." -ForegroundColor Green
    return
}

Write-Step "Ensure ECR repository"
Ensure-EcrRepository -Repo $RepositoryName -AwsRegion $Region

if (-not $SkipEcrLifecyclePolicy) {
    Write-Step "Apply ECR lifecycle policy"
    Ensure-EcrLifecyclePolicy -Repo $RepositoryName -AwsRegion $Region -BuildCacheImageTag $BuildCacheTag -TaggedImageRetention $KeepTaggedImageCount -BuildCacheRetention $KeepBuildCacheImageCount -UntaggedExpireDays $ExpireUntaggedAfterDays
}
else {
    Write-Step "Skip ECR lifecycle policy"
    Write-Host "ECR lifecycle policy sync skipped by -SkipEcrLifecyclePolicy." -ForegroundColor Yellow
}

Write-Step "Login to ECR"
if ($needsDocker) {
    aws ecr get-login-password --region $Region | docker login --username AWS --password-stdin "$accountId.dkr.ecr.$Region.amazonaws.com" | Out-Null
    Write-Host "ECR login succeeded." -ForegroundColor Green
}
else {
    Write-Host "Skip Docker/ECR login because -NoBuild is enabled." -ForegroundColor Yellow
}

if (-not $NoBuild -and -not $NoPush) {
    Write-Step "Build and push image (Lambda-compatible manifest)"
    if (-not $DisableBuildCache) {
        docker buildx build --platform $Platform --provenance=false --sbom=false --cache-from "type=registry,ref=$buildCacheRef" --cache-to "type=registry,ref=$buildCacheRef,mode=max" -t $imageWithTag --push .
    }
    else {
        docker buildx build --platform $Platform --provenance=false --sbom=false -t $imageWithTag --push .
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Docker build/push failed. Stop deployment to avoid publishing a stale image."
    }
}
elseif (-not $NoBuild -and $NoPush) {
    Write-Step "Build image only"
    if (-not $DisableBuildCache) {
        docker buildx build --platform $Platform --provenance=false --sbom=false --cache-from "type=registry,ref=$buildCacheRef" -t $imageWithTag .
    }
    else {
        docker buildx build --platform $Platform --provenance=false --sbom=false -t $imageWithTag .
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Docker build failed."
    }
}
else {
    Write-Step "Skip build"
    Write-Host "Using existing image in ECR." -ForegroundColor Yellow
}

if (-not $NoPush) {
    Write-Step "Validate image manifest type"
    $mediaType = (aws ecr batch-get-image --repository-name $RepositoryName --image-ids imageTag=$imageTag --region $Region --query "images[0].imageManifestMediaType" --output text).Trim()
    Write-Host "Manifest media type: $mediaType"
    if ($mediaType -eq "application/vnd.oci.image.index.v1+json") {
        throw "Image manifest is OCI index (multi-arch). Lambda does not support this."
    }
}

Write-Step "Wait for Lambda update readiness"
aws lambda wait function-updated --function-name $FunctionName --region $Region
if ($LASTEXITCODE -ne 0) {
    throw "Lambda function is not ready for update."
}

Write-Step "Update Lambda by image digest"
$latestDigest = (aws ecr describe-images --repository-name $RepositoryName --image-ids imageTag=$imageTag --region $Region --query "imageDetails[0].imageDigest" --output text).Trim()
if ([string]::IsNullOrWhiteSpace($latestDigest) -or $latestDigest -eq "None") {
    throw "Unable to resolve image digest for tag '$imageTag' from ECR."
}

$imageWithDigest = "${imageBase}@${latestDigest}"
Write-Host "Updating Lambda to image digest: $latestDigest"

aws lambda update-function-code --function-name $FunctionName --region $Region --image-uri $imageWithDigest --query "{FunctionName:FunctionName,LastUpdateStatus:LastUpdateStatus}" --output table
if ($LASTEXITCODE -ne 0) {
    throw "Lambda code update failed."
}

aws lambda wait function-updated --function-name $FunctionName --region $Region
if ($LASTEXITCODE -ne 0) {
    throw "Lambda code update did not complete successfully."
}

aws lambda get-function --function-name $FunctionName --region $Region --query "Configuration.[FunctionName,LastUpdateStatus]" --output table
if ($LASTEXITCODE -ne 0) {
    throw "Failed to verify Lambda function status after code update."
}

if (-not $SkipEnvSync) {
    Write-Step "Sync Lambda environment variables from .env"
    Sync-LambdaEnvironmentVariables -LambdaFunctionName $FunctionName -AwsRegion $Region -DotEnvPath $EnvFilePath
}
else {
    Write-Step "Skip .env environment sync"
    Write-Host "Lambda environment variables sync skipped by -SkipEnvSync." -ForegroundColor Yellow
}

if (-not $SkipInvoke) {
    Write-Step "Invoke Lambda smoke test"
    $responseFile = "lambda-response.json"
    aws lambda invoke --function-name $FunctionName --region $Region --cli-binary-format raw-in-base64-out --payload '{}' $responseFile --query "{StatusCode:StatusCode,FunctionError:FunctionError,ExecutedVersion:ExecutedVersion}" --output table
    if (Test-Path $responseFile) {
        Write-Host "`n${responseFile}:" -ForegroundColor Green
        Get-Content $responseFile
    }
}
else {
    Write-Step "Skip invoke"
    Write-Host "Lambda update completed (invoke skipped)." -ForegroundColor Yellow
}

Write-Step "Done"
Write-Host "Deployment completed successfully." -ForegroundColor Green

