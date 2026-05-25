# File Deduplicator

A high-performance, enterprise-grade C# utility designed to identify and isolate duplicate files on a NAS (Network Attached Storage) or other storage types with maximum efficiency and data safety.

## Key Features

- **Tiered Hashing Strategy**:
  - **Small Files (<= 10MB)**: Full XxHash64 for 100% accuracy.
  - **Medium/Large Files (10MB - 1GB)**: Multi-Point Sampling (Start/Middle/End) for maximum speed.
  - **Massive Files (>= 1GB)**: Full XxHash64 for maximum security on critical data.
- **Enterprise Resilience**:
  - **Hash Cache**: Remembers previously hashed files to make subsequent runs near-instant. Auto-saves every 1,000 files.
  - **Long Path Support**: Supports directory paths up to 32,767 characters via `\\?\UNC\` prefix.
  - **Network Error Recovery**: Automatic 5-second retries for common NAS connection blips.
  - **Folder-Isolated Parallelism**: Processes folders in parallel while keeping file operations sequential within folders to prevent NAS metadata locking.
- **Safe Isolation**: Files are never permanently deleted. They are moved to an archive share after byte-size verification.

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## How to Build

To build the project in the current directory:

```bash
dotnet build
```

## How to Run Tests

The project includes an xUnit test suite to verify hashing and path logic:

```bash
dotnet test
```

## Creating a Standalone Executable

To publish the application as a single, self-contained executable file (so you can run it on a machine without .NET installed):

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The resulting `.exe` will be located in:
`bin/Release/net8.0/win-x64/publish/`

## Configuration

The application will prompt for the following paths at startup:
1. **Source Path**: The NAS directory to scan for duplicates.
2. **Archive/Dupes Path**: Where to move identical clones.
3. **Suspect Path**: Where to move files with identical names but different content (for manual review).

## Audit Trail

For "Suspect" files, the application generates a `.AuditTrail.txt` file alongside the moved file, explaining the logic used to identify it and providing timestamps for both the master and the copy.
