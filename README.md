# NAS Deduplicator Enterprise Platform

A high-performance, full-stack enterprise utility designed to identify and isolate duplicate files on a NAS (Network Attached Storage) with real-time monitoring and a web-based management dashboard.

## Architecture

- **Backend**: ASP.NET Core 8.0 Web API with SignalR for real-time log streaming.
- **Frontend**: React + Tailwind CSS dashboard for configuration and live progress monitoring.
- **Telemetry**: Integrated Splunk forwarding for long-term auditing and analytics.
- **Orchestration**: Fully containerized via Docker Compose for one-click deployment.

## Features

- **Web Dashboard**: Real-time progress bars, byte throughput, and success/failure metrics.
- **Zero-Config Splunk**: Automatic connection when using the Docker stack.
- **Tiered Hashing Strategy**: Optimized for speed (Sampling) and security (Full Hashing).
- **Enterprise Resilience**: Hash caching, 32k character path support, and folder-isolated parallelism.

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
