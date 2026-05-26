# NAS Deduplicator - Test Data Generation Script
# This script creates a complex directory structure to test deduplication logic.

$BaseDir = "C:\Temp\DedupeManualTest"
$SourceDir = Join-Path $BaseDir "Source"
$ArchiveDir = Join-Path $BaseDir "Archive"
$SuspectDir = Join-Path $BaseDir "Suspect"

# Cleanup previous runs
if (Test-Path $BaseDir) {
    Write-Host "Cleaning up previous test data..." -ForegroundColor Yellow
    Remove-Item $BaseDir -Recurse -Force
}

# Create base folders
New-Item -ItemType Directory -Path $SourceDir, $ArchiveDir, $SuspectDir | Out-Null

$MasterContent = "This is a global master file content that will be cloned many times."
$MasterPath = Join-Path $SourceDir "GlobalMaster.txt"
Set-Content -Path $MasterPath -Value $MasterContent

Write-Host "Creating test environment at $BaseDir..." -ForegroundColor Cyan

# 1. Create 5 Client Roots
$Clients = @("Client_A", "Client_B", "Client_C", "Client_D", "Client_E")

foreach ($Client in $Clients) {
    $ClientPath = Join-Path $SourceDir $Client
    New-Item -ItemType Directory -Path $ClientPath | Out-Null
    
    # 2. Add 10 unique files to each root
    for ($i = 1; $i -le 10; $i++) {
        $UniquePath = Join-Path $ClientPath "Unique_Root_$i.txt"
        Set-Content -Path $UniquePath -Value "Unique Content for $Client index $i - $(New-Guid)"
        
        # Scenario: Multi-Level Versioning (Prepare Roots for 5 files)
        if ($i -ge 3 -and $i -le 7) {
            (Get-Item $UniquePath).LastWriteTime = (Get-Date).AddDays(-60)
        }
    }

    # 3. Create deep recursive nesting (8 layers)
    # Using the SAME folder name to test the GetLogicalPath normalization
    $CurrentNest = $ClientPath
    for ($Layer = 1; $Layer -le 8; $Layer++) {
        $NestFolderName = "$Client" 
        
        $CurrentNest = Join-Path $CurrentNest $NestFolderName
        New-Item -ItemType Directory -Path $CurrentNest | Out-Null
        
        # Scenario: Nested Duplicate (Exact Clone)
        # Create a clone of a root unique file in every layer
        $ClonePath = Join-Path $CurrentNest "Unique_Root_1.txt"
        Copy-Item -Path (Join-Path $ClientPath "Unique_Root_1.txt") -Destination $ClonePath
        
        # Scenario: Multi-Level Versioning (Intermediate Layers 1-7 for 5 files)
        if ($Layer -ge 1 -and $Layer -le 7) {
            for ($v = 3; $v -le 7; $v++) {
                $VersionPath = Join-Path $CurrentNest "Unique_Root_$v.txt"
                Set-Content -Path $VersionPath -Value "Intermediate Version of file $v - Layer $Layer"
                (Get-Item $VersionPath).LastWriteTime = (Get-Date).AddDays(-50 + $Layer)
            }
        }

        # Scenario: Nested Versioning (Newer Files buried deep)
        # In the very last layer, create NEWER versions for all test cases
        if ($Layer -eq 8) {
            # Test Case 2: Root vs Deep (Single file)
            $PromotionPath = Join-Path $CurrentNest "Unique_Root_2.txt"
            Set-Content -Path $PromotionPath -Value "I AM THE NEWEST VERSION - FOUND IN LAYER 8"
            (Get-Item $PromotionPath).LastWriteTime = (Get-Date)
            (Get-Item (Join-Path $ClientPath "Unique_Root_2.txt")).LastWriteTime = (Get-Date).AddDays(-30)
            
            # Test Case 3: Multi-Level Winners (5 files)
            for ($v = 3; $v -le 7; $v++) {
                $MultiWinnerPath = Join-Path $CurrentNest "Unique_Root_$v.txt"
                Set-Content -Path $MultiWinnerPath -Value "I AM THE ABSOLUTE NEWEST VERSION of file $v - FROM LAYER 8"
                (Get-Item $MultiWinnerPath).LastWriteTime = (Get-Date)
            }

            Write-Host "  [Promotion Test Created] Batch of 5 multi-level files created across all 8 layers of $Client" -ForegroundColor Yellow
        }

        # Add 5 Clones of the Global Master in each layer
        for ($i = 1; $i -le 5; $i++) {
            $ClonePath = Join-Path $CurrentNest "Clone_L$($Layer)_$i.txt"
            Copy-Item -Path $MasterPath -Destination $ClonePath
        }

        # Add 2 Suspect Files (Same name as root uniques, but different content)
        if ($Layer -eq 3 -or $Layer -eq 6) {
            $SuspectPath = Join-Path $CurrentNest "Unique_Root_1.txt"
            Set-Content -Path $SuspectPath -Value "SUSPECT CONTENT - This should be isolated."
        }

        # Add Junk Files
        Set-Content -Path (Join-Path $CurrentNest "Thumbs.db") -Value "Junk metadata"
        Set-Content -Path (Join-Path $CurrentNest ".DS_Store") -Value "More junk"

        # 4. Add "Suspect" Files in Layer 2 and 5
        # Same BaseName pattern, different content, same folder
        if ($Layer -eq 2 -or $Layer -eq 5) {
            $SuspectBaseName = "Conflict_Document"
            Set-Content -Path (Join-Path $CurrentNest "$SuspectBaseName.pdf") -Value "Original Content A - $(New-Guid)"
            # Wait a second to ensure different timestamp
            Start-Sleep -Milliseconds 100 
            Set-Content -Path (Join-Path $CurrentNest "$SuspectBaseName (1).pdf") -Value "Modified Content B - $(New-Guid)"
            Write-Host "  [Suspect Created] in $CurrentNest" -ForegroundColor Gray
        }
    }
}

Write-Host "`nTest Data Generation Complete!" -ForegroundColor Green
Write-Host "------------------------------------------------"
Write-Host "Source Path:  $SourceDir"
Write-Host "Archive Path: $ArchiveDir"
Write-Host "Suspect Path: $SuspectDir"
Write-Host "------------------------------------------------"
Write-Host "Run your app with: ./NASDeduplicator.exe -s `"$SourceDir`" -a `"$ArchiveDir`" -p `"$SuspectDir`""
