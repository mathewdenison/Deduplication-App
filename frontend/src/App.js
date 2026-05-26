import React, { useState, useEffect, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import { Play, Square, Settings, Activity, FileCheck, ShieldAlert } from 'lucide-react';

const API_BASE = "http://localhost:5100";

function App() {
  const [status, setStatus] = useState({
    isRunning: false,
    currentPhase: "Idle",
    filesDiscovered: 0,
    totalBytesHashed: 0,
    successCount: 0,
    failCount: 0,
    elapsedMinutes: 0,
    config: { source: "", archive: "", suspect: "" }
  });

  const [config, setConfig] = useState({
    source: "",
    archive: "",
    suspect: "",
    telemetryUrl: "",
    telemetryToken: ""
  });

  const [logs, setLogs] = useState([]);
  const logEndRef = useRef(null);

  useEffect(() => {
    fetchStatus();
    const interval = setInterval(fetchStatus, 3000);

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE}/logHub`)
      .withAutomaticReconnect()
      .build();

    connection.on("ReceiveLog", (message, severity) => {
      setLogs(prev => [...prev.slice(-100), { message, severity }]);
    });

    connection.start().catch(err => console.error("SignalR Connection Error: ", err));

    return () => {
      clearInterval(interval);
      connection.stop();
    };
  }, []);

  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [logs]);

  const fetchStatus = async () => {
    try {
      const res = await fetch(`${API_BASE}/api/dedupe/status`);
      const data = await res.json();
      setStatus(data);
      if (!data.isRunning && data.config.source) {
        setConfig(prev => ({ ...prev, ...data.config }));
      }
    } catch (e) {}
  };

  const updateConfig = async () => {
    await fetch(`${API_BASE}/api/dedupe/config`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(config)
    });
    alert("Configuration Saved!");
  };

  const startRun = () => fetch(`${API_BASE}/api/dedupe/start`, { method: 'POST' });
  const stopRun = () => fetch(`${API_BASE}/api/dedupe/stop`, { method: 'POST' });

  return (
    <div className="min-h-screen p-6 font-sans">
      <header className="flex justify-between items-center mb-8 border-b border-slate-700 pb-4">
        <h1 className="text-3xl font-bold text-cyan-400 flex items-center gap-3">
          <Activity className="w-8 h-8" /> NAS Deduplicator Enterprise
        </h1>
        <div className="flex gap-4">
          <button onClick={() => fetch(`${API_BASE}/api/dedupe/verify`, { method: 'POST' })} disabled={status.isRunning} className="bg-amber-600 hover:bg-amber-700 px-6 py-2 rounded-lg font-bold flex items-center gap-2 transition-colors disabled:opacity-50">
            <ShieldAlert className="w-4 h-4" /> Run Safety Verification
          </button>
          {!status.isRunning ? (
            <button onClick={startRun} className="bg-green-600 hover:bg-green-700 px-6 py-2 rounded-lg font-bold flex items-center gap-2 transition-colors">
              <Play className="w-4 h-4" /> Start Run
            </button>
          ) : (
            <button onClick={stopRun} className="bg-red-600 hover:bg-red-700 px-6 py-2 rounded-lg font-bold flex items-center gap-2 transition-colors">
              <Square className="w-4 h-4" /> Stop Run
            </button>
          )}
        </div>
      </header>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Stats Column */}
        <div className="lg:col-span-1 space-y-6">
          <section className="bg-slate-800 p-6 rounded-xl border border-slate-700 shadow-lg">
            <h2 className="text-xl font-bold mb-4 flex items-center gap-2 text-slate-300">
              <Activity className="w-5 h-5" /> Live Metrics
            </h2>
            <div className="grid grid-cols-2 gap-4">
              <div className="bg-slate-900 p-4 rounded-lg">
                <p className="text-xs text-slate-500 uppercase font-bold">Phase</p>
                <p className="text-lg font-mono text-cyan-400">{status.currentPhase}</p>
              </div>
              <div className="bg-slate-900 p-4 rounded-lg">
                <p className="text-xs text-slate-500 uppercase font-bold">Discovered</p>
                <p className="text-2xl font-mono">{status.filesDiscovered.toLocaleString()}</p>
              </div>
              <div className="bg-slate-900 p-4 rounded-lg">
                <p className="text-xs text-slate-500 uppercase font-bold">Success</p>
                <p className="text-2xl font-mono text-green-400">{status.successCount.toLocaleString()}</p>
              </div>
              <div className="bg-slate-900 p-4 rounded-lg">
                <p className="text-xs text-slate-500 uppercase font-bold">Failures</p>
                <p className="text-2xl font-mono text-red-400">{status.failCount.toLocaleString()}</p>
              </div>
              <div className="bg-slate-900 p-4 rounded-lg col-span-2">
                <p className="text-xs text-slate-500 uppercase font-bold">Data Hashed</p>
                <p className="text-2xl font-mono">{(status.totalBytesHashed / (1024**3)).toFixed(2)} GB</p>
              </div>
            </div>
          </section>

          <section className="bg-slate-800 p-6 rounded-xl border border-slate-700 shadow-lg">
            <h2 className="text-xl font-bold mb-4 flex items-center gap-2 text-slate-300">
              <Settings className="w-5 h-5" /> Configuration
            </h2>
            <div className="space-y-4">
              <div>
                <label className="text-xs text-slate-500 font-bold block mb-1">SOURCE PATH</label>
                <input value={config.source} onChange={e => setConfig({...config, source: e.target.value})} className="w-full bg-slate-900 border border-slate-700 p-2 rounded text-sm font-mono focus:border-cyan-500 outline-none" />
              </div>
              <div>
                <label className="text-xs text-slate-500 font-bold block mb-1">ARCHIVE PATH</label>
                <input value={config.archive} onChange={e => setConfig({...config, archive: e.target.value})} className="w-full bg-slate-900 border border-slate-700 p-2 rounded text-sm font-mono focus:border-cyan-500 outline-none" />
              </div>
              <div>
                <label className="text-xs text-slate-500 font-bold block mb-1">SUSPECT PATH</label>
                <input value={config.suspect} onChange={e => setConfig({...config, suspect: e.target.value})} className="w-full bg-slate-900 border border-slate-700 p-2 rounded text-sm font-mono focus:border-cyan-500 outline-none" />
              </div>
              <button onClick={updateConfig} disabled={status.isRunning} className="w-full bg-slate-700 hover:bg-slate-600 disabled:opacity-50 py-2 rounded font-bold transition-colors">
                Apply Settings
              </button>
            </div>
          </section>
        </div>

        {/* Logs Column */}
        <div className="lg:col-span-2 flex flex-col h-[calc(100vh-180px)]">
          <section className="bg-slate-800 rounded-xl border border-slate-700 shadow-lg flex-1 flex flex-col overflow-hidden">
            <div className="p-4 border-b border-slate-700 bg-slate-800/50 flex justify-between items-center">
              <h2 className="text-xl font-bold flex items-center gap-2 text-slate-300">
                <Activity className="w-5 h-5" /> Execution Logs
              </h2>
              <span className="px-2 py-1 bg-slate-900 rounded text-[10px] font-mono text-slate-500">REAL-TIME VIA SIGNALR</span>
            </div>
            <div className="p-4 overflow-y-auto font-mono text-sm space-y-1 bg-black/30 flex-1">
              {logs.map((log, i) => (
                <div key={i} className={
                  log.severity.includes("Red") ? "text-red-400" :
                  log.severity.includes("Yellow") ? "text-yellow-400" :
                  log.severity.includes("Cyan") ? "text-cyan-400" :
                  log.severity.includes("Green") ? "text-green-400" :
                  "text-slate-300"
                }>
                  {log.message}
                </div>
              ))}
              <div ref={logEndRef} />
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}

export default App;
