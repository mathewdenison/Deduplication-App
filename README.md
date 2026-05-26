# NAS Deduplicator Enterprise Platform

A high-performance, full-stack enterprise utility designed to identify and isolate duplicate files on a NAS (Network Attached Storage) with real-time monitoring and a web-based management dashboard.

## Architecture

- **Backend**: ASP.NET Core 8.0 Web API with SignalR for real-time log streaming.
- **Frontend**: React + Tailwind CSS dashboard for configuration and live progress monitoring.
- **Telemetry**: Integrated Splunk forwarding for long-term auditing and analytics.
- **Orchestration**: Fully containerized via Docker Compose for one-click deployment.

## Features

- **Web Dashboard**: Real-time progress bars, byte throughput, and success/failure metrics.
- **Logical Path Triage**: Automatically identifies and collapses recursive nesting (e.g., `A/A/B` -> `A/B`) to solve filesystem restore artifacts.
- **Latest Version Promotion**: Groups logical files, preserves the newest version based on `LastWriteTime`, and automatically promotes it to the shortest available path.
- **Path-Agnostic Caching**: Hash cache is normalized to support reuse across local paths, network shares, and Windows long-path formats (`\\?\`).
- **Windows Long Path Support**: Full support for paths up to 32,767 characters via internal `\\?\` prefixing.
- **Enterprise Resilience**: Multi-threaded hashing, 32-thread small file processing, and network-resilient I/O with automatic retries.

## Advanced Deduplication Logic

The engine operates in 6 distinct phases to ensure data integrity and structural cleanliness:

1.  **Indexing & Grouping**: Discovery of all files and initial grouping by size.
2.  **Tiered Hashing**: 3-stage hashing (Quick Hash, Sampling, Full Hash) based on file size to maximize throughput.
3.  **Logical Triage**: Grouping by normalized paths. The file with the **Latest LastWriteTime** is selected as the winner. Older identical files go to `Archive`, older different files go to `Suspect`.
4.  **Safe Move**: Files are moved using byte-size verification and atomic renames where possible.
5.  **Path Optimization**: Newest versions found in deep folders are automatically promoted to the shortest available logical path.
6.  **Deep Folder Sweep**: Recursive cleanup of empty directory trees and system junk (`.DS_Store`, `Thumbs.db`).

## Testing & Simulation

To simulate a complex recursive restore scenario (like those found in production), use the provided PowerShell script:

```powershell
.\CreateTestData.ps1
```

This creates a test environment with:
- 8-layer recursive nesting using identical folder names.
- Multi-level versioning where the newest file is buried at the deepest layer.
- Global clones and suspect mismatches.

## Quick Start (Docker)

To spin up the entire platform (UI, API, and Splunk):

```bash
docker-compose up -d
```

- **Management UI**: [http://localhost:3000](http://localhost:3000)
- **Splunk Dashboard**: [http://localhost:8000](http://localhost:8000) (User: `admin`, Pass: `SplunkPassword123!`)

## Manual Development

### 1. Build & Run Backend
```bash
dotnet build
dotnet run
```
The API will be available at `http://localhost:5000`.

### 2. Build & Run Frontend
```bash
cd frontend
npm install
npm start
```
The UI will be available at `http://localhost:3000`.

## How to Run Tests

The project includes an xUnit test suite to verify engine logic:

```bash
dotnet test
```

## Audit Trail

For "Suspect" files, the application generates a `.AuditTrail.txt` file alongside the moved file, explaining the logic used to identify it and providing timestamps for both the master and the copy.
